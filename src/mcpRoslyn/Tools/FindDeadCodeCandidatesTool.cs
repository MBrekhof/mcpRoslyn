using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record DeadCodeCandidate(
    string Symbol,
    string Kind,
    string Accessibility,
    Contracts.SymbolLocation? Location,
    string Confidence,    // "high" | "medium"
    string Reason);

public sealed record DeadCodeSkipped(
    int PublicMembers,
    int Tests,
    int Denylisted);

public sealed record FindDeadCodeResult(
    IReadOnlyList<DeadCodeCandidate> Candidates,
    IReadOnlyList<string> ProjectsScanned,
    DeadCodeSkipped Skipped);

[McpServerToolType]
internal sealed class FindDeadCodeCandidatesTool(IWorkspaceService ws, ILogger<FindDeadCodeCandidatesTool> log)
    : ToolBase(ws, log)
{
    private static readonly HashSet<string> DenylistAttributes = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute", "TestAttribute", "TestMethodAttribute",
        "BenchmarkAttribute", "JsonConstructorAttribute",
        "OnDeserializedAttribute", "OnDeserializingAttribute",
        "ModuleInitializerAttribute", "UnmanagedCallersOnlyAttribute", "DllImportAttribute"
    };

    private static readonly HashSet<string> DenylistMemberNames = new(StringComparer.Ordinal)
        { "Dispose", "DisposeAsync", "ToString", "Equals", "GetHashCode" };

    [McpServerTool(Name = "find_dead_code_candidates")]
    [Description("Returns private/internal members with no references. Skips public surface, attributed members ([Fact]/[Test]/[JsonConstructor]/…), and framework contracts (Dispose/Equals/…). Marks internal members as medium-confidence when [InternalsVisibleTo] applies.")]
    public Task<Contracts.ToolResult<FindDeadCodeResult>> InvokeAsync(
        bool includePrivateMembers = true,
        bool includeInternalTypes = true,
        bool includeTests = false,
        int maxResults = 20,
        string[]? excludePaths = null,
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var indexed = Workspace.SymbolIndex.AllSymbols();

            int publicSkip = 0, testSkip = 0, denySkip = 0;
            var candidates = new List<DeadCodeCandidate>();

            foreach (var entry in indexed)
            {
                ct2.ThrowIfCancellationRequested();
                if (candidates.Count >= maxResults) break;

                // Resolve back to ISymbol for accessibility / attribute / containing-project checks
                var symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, entry.SymbolId, ct2);
                if (symbol is null) continue;

                // Path / test filter
                var loc = entry.Info.PrimaryLocation;
                if (loc is null) continue;
                if (excludePaths is not null && excludePaths.Any(p => loc.FilePath.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                var inTestProject = IsInTestProject(symbol, solution);
                if (inTestProject && !includeTests) { testSkip++; continue; }

                if (symbol.DeclaredAccessibility == Accessibility.Public ||
                    symbol.DeclaredAccessibility == Accessibility.Protected ||
                    symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal)
                { publicSkip++; continue; }

                // Eligibility per include* flags
                bool isPrivateMember = symbol.DeclaredAccessibility == Accessibility.Private;
                bool isInternalLevel = symbol.DeclaredAccessibility == Accessibility.Internal
                                    || symbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal;
                if (isPrivateMember && !includePrivateMembers) continue;
                if (isInternalLevel && !includeInternalTypes) continue;

                if (IsDenylisted(symbol)) { denySkip++; continue; }

                // Reference scan
                var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct2);
                if (refs.Any(r => r.Locations.Any())) continue;

                var confidence = ComputeConfidence(symbol, solution);
                candidates.Add(new DeadCodeCandidate(
                    Symbol: entry.Info.Signature,
                    Kind: symbol.Kind.ToString(),
                    Accessibility: symbol.DeclaredAccessibility.ToString(),
                    Location: loc,
                    Confidence: confidence,
                    Reason: confidence == "high" ? "no-references" : "no-references-but-internals-visible-to-friends"));
            }

            return Contracts.ToolResult<FindDeadCodeResult>.Ok(new FindDeadCodeResult(
                Candidates: candidates,
                ProjectsScanned: solution.Projects.Select(p => p.Name).ToArray(),
                Skipped: new DeadCodeSkipped(publicSkip, testSkip, denySkip)));
        }, ct);

    private static bool IsDenylisted(ISymbol symbol)
    {
        if (DenylistMemberNames.Contains(symbol.Name)) return true;
        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name is not null && DenylistAttributes.Contains(name)) return true;
        }
        // Record primary constructors / [Serializable] zero-param ctors — defer for v1.4
        return false;
    }

    private static bool IsInTestProject(ISymbol symbol, Solution sol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree is null) return false;
        var project = sol.GetDocument(loc.SourceTree)?.Project;
        if (project is null) return false;
        if (project.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("Spec", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var r in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (r.FilePath is null) continue;
            if (r.FilePath.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                r.FilePath.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                r.FilePath.Contains("MSTest", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ComputeConfidence(ISymbol symbol, Solution sol)
    {
        // Internal members in an assembly with [InternalsVisibleTo("X")] → medium (X could reference it but we didn't find any).
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree is null) return "high";
        var project = sol.GetDocument(loc.SourceTree)?.Project;
        if (project is null) return "high";

        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
        if (compilation is null) return "high";

        if (symbol.DeclaredAccessibility == Accessibility.Internal ||
            symbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal)
        {
            var hasInternalsVisibleTo = compilation.Assembly.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "InternalsVisibleToAttribute");
            return hasInternalsVisibleTo ? "medium" : "high";
        }
        return "high";
    }
}
