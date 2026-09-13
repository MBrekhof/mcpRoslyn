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
    int Denylisted,
    int DiRegistered = 0,
    int FrameworkReached = 0);

public sealed record FindDeadCodeResult(
    IReadOnlyList<DeadCodeCandidate> Candidates,
    IReadOnlyList<string> ProjectsScanned,
    DeadCodeSkipped Skipped,
    bool Truncated = false);

[McpServerToolType]
internal sealed class FindDeadCodeCandidatesTool(IWorkspaceService ws, ILogger<FindDeadCodeCandidatesTool> log)
    : ToolBase(ws, log)
{
    private static readonly HashSet<string> DenylistAttributes = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute", "TestAttribute", "TestMethodAttribute",
        "BenchmarkAttribute", "JsonConstructorAttribute",
        // EF Core finds migrations by scanning for these, never by referencing the class.
        "MigrationAttribute", "DbContextAttribute",
        "OnDeserializedAttribute", "OnDeserializingAttribute",
        "ModuleInitializerAttribute", "UnmanagedCallersOnlyAttribute", "DllImportAttribute"
    };

    private static readonly HashSet<string> DenylistMemberNames = new(StringComparer.Ordinal)
        { "Dispose", "DisposeAsync", "ToString", "Equals", "GetHashCode" };

    [McpServerTool(Name = "find_dead_code_candidates")]
    [Description("Returns members with no references: private/internal by default, plus unreferenced public types when includePublicTypes is set. Skips attributed members ([Fact]/[Test]/[JsonConstructor]/…) and framework contracts (Dispose/Equals/…). Public types that are DI-registered or framework-reached (Controller/Hub/extension classes) are suppressed. Marks internal members as medium-confidence when [InternalsVisibleTo] applies.")]
    public Task<Contracts.ToolResult<FindDeadCodeResult>> InvokeAsync(
        bool includePrivateMembers = true,
        bool includeInternalTypes = true,
        bool includePublicTypes = false,
        bool includeTests = false,
        int maxResults = 20,
        string[]? excludePaths = null,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            // Solution and indexes from one workspace generation, so references are scanned against
            // the same load the candidates came from (WS-006).
            var ready = await Workspace.GetIndexedSolutionAsync(ct2);
            // Dirty-walked and de-duplicated by the index itself (IDX-001). The index keeps a file linked
            // into several assemblies as one symbol per assembly; here the unit is the declaration you
            // would delete, so those merge into one entry spanning all its declaring projects (IDX-005).
            // Candidates are resolved and scanned against the solution the index names, which a concurrent
            // refresh may have made newer than ready.Solution (IDX-004).
            var snapshot = ready.SymbolIndex.AllSymbols(ready.Solution, ct2);
            var solution = snapshot.Solution;
            var indexed = snapshot.Symbols
                .GroupBy(e => (e.SymbolId, File: e.Info.PrimaryLocation?.FilePath))
                .Select(g => (Entry: g.First(), DeclaringDocs: g.SelectMany(e => e.DeclaringDocs).ToArray()))
                .ToList();

            // Built once, and only when it will be consulted — it walks the whole DI index.
            var registered = includePublicTypes ? RegistrationLookup.Build(ready.InvocationIndex) : null;

            int publicSkip = 0, testSkip = 0, denySkip = 0, diSkip = 0, frameworkSkip = 0;
            var truncated = false;
            var candidates = new List<DeadCodeCandidate>();

            foreach (var (entry, declaringDocs) in indexed)
            {
                ct2.ThrowIfCancellationRequested();

                // Path / test filter
                var loc = entry.Info.PrimaryLocation;
                if (loc is null) continue;

                // Resolve back to ISymbol for accessibility / attribute / containing-project checks — this
                // declaration's own symbol(s), not whichever project first shares its id (IDX-005). A
                // linked file or a multi-targeted project yields one per project, and #if can make them
                // differ, so every judgement leans towards keeping the code: the most exposed copy decides
                // accessibility, it is test code only if every copy is, a denylisted attribute or a
                // reference on ANY copy spares it, and any medium-confidence copy makes it medium.
                var copies = await RoslynHelpers.ResolveDeclaredSymbolsAsync(
                    solution, declaringDocs, entry.SymbolId, loc.FilePath, ct2);
                if (copies.Count == 0) continue;
                var symbol = copies.OrderByDescending(c => ExposureRank(c.DeclaredAccessibility)).First();
                if (excludePaths is not null && excludePaths.Any(p => loc.FilePath.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                var inTestProject = copies.All(c => IsInTestProject(c, solution));
                if (inTestProject && !includeTests) { testSkip++; continue; }

                var isPublicSurface = symbol.DeclaredAccessibility is Accessibility.Public
                                                                   or Accessibility.Protected
                                                                   or Accessibility.ProtectedOrInternal;
                if (isPublicSurface)
                {
                    // A public *member* is reachable from outside the solution in ways a reference
                    // scan cannot see, so it is never a candidate. A public *type* in an application
                    // solution is the case that actually matters — behind an opt-in, because the
                    // reflection/container false-positive risk is real (TOOL-006).
                    var publicType = includePublicTypes && symbol.DeclaredAccessibility == Accessibility.Public
                        ? symbol as INamedTypeSymbol
                        : null;
                    if (publicType is null) { publicSkip++; continue; }
                    if (registered!.Covers(publicType)) { diSkip++; continue; }
                    // Per copy: #if can give only one project's copy the extension methods that reach it.
                    if (copies.OfType<INamedTypeSymbol>().Any(IsFrameworkReached)) { frameworkSkip++; continue; }
                }
                else
                {
                    // Eligibility per include* flags
                    bool isPrivateMember = symbol.DeclaredAccessibility == Accessibility.Private;
                    bool isInternalLevel = symbol.DeclaredAccessibility == Accessibility.Internal
                                        || symbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal;
                    if (isPrivateMember && !includePrivateMembers) continue;
                    if (isInternalLevel && !includeInternalTypes) continue;
                }

                // The runtime calls the entry point; nothing in source ever references it (TOOL-012).
                if (await IsEntryPointAsync(copies, solution, ct2)) { frameworkSkip++; continue; }
                if (copies.Any(IsDenylisted)) { denySkip++; continue; }

                // Everything above is cheap; the reference scan is not. Once the result set is full
                // we keep classifying so the Skipped counters stay accurate, and stop paying for
                // scans whose result we could not report anyway (TOOL-003).
                if (candidates.Count >= maxResults) { truncated = true; continue; }

                // Reference scan. For a public type, references from inside its own declaration
                // (a static factory naming itself, a nested helper) are not evidence that anything
                // else uses it.
                // A linked declaration is dead only if no assembly compiling it uses its copy.
                var referenced = false;
                foreach (var copy in copies)
                {
                    var refs = await SymbolFinder.FindReferencesAsync(copy, solution, ct2);
                    referenced = isPublicSurface
                        ? refs.Any(r => r.Locations.Any(l => !IsInsideOwnDeclaration(copy, l.Location)))
                        : refs.Any(r => r.Locations.Any());
                    if (referenced) break;
                }
                if (referenced) continue;

                var confidence = isPublicSurface || copies.Any(c => ComputeConfidence(c, solution) != "high")
                    ? "medium"
                    : "high";
                var reason = isPublicSurface
                    ? "public-type-no-references-outside-own-declaration-and-not-di-registered"
                    : confidence == "high" ? "no-references" : "no-references-but-internals-visible-to-friends";
                candidates.Add(new DeadCodeCandidate(
                    Symbol: entry.Info.Signature,
                    Kind: symbol.Kind.ToString(),
                    Accessibility: symbol.DeclaredAccessibility.ToString(),
                    Location: loc,
                    Confidence: confidence,
                    Reason: reason));
            }

            var result = new FindDeadCodeResult(
                Candidates: candidates,
                ProjectsScanned: solution.Projects.Select(p => p.Name).ToArray(),
                Skipped: new DeadCodeSkipped(publicSkip, testSkip, denySkip, diSkip, frameworkSkip),
                Truncated: truncated);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var highCount = result.Candidates.Count(c => string.Equals(c.Confidence, "high", StringComparison.OrdinalIgnoreCase));
                var medCount = result.Candidates.Count(c => string.Equals(c.Confidence, "medium", StringComparison.OrdinalIgnoreCase));
                return Contracts.ToolResult<FindDeadCodeResult>.OkSummary(
                    $"{result.Candidates.Count} candidates ({highCount} high, {medCount} medium)");
            }
            return Contracts.ToolResult<FindDeadCodeResult>.Ok(result);
        }, ct);

    /// <summary>
    /// Types the framework reaches by convention rather than by a symbol reference. Without this
    /// every controller in an API project reads as dead the moment includePublicTypes is set.
    /// </summary>
    private static readonly string[] FrameworkReachedSuffixes =
        { "Controller", "Hub", "Middleware", "Migration", "Startup", "Program" };

    private static bool IsFrameworkReached(INamedTypeSymbol type)
    {
        if (FrameworkReachedSuffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal))) return true;
        // A static class holding extension methods is reached through its members: `services.AddFoo()`
        // references AddFoo, never the class that declares it.
        return type.IsStatic && type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsExtensionMethod);
    }

    /// <summary>
    /// True when <paramref name="location"/> falls inside one of the symbol's own declarations.
    /// Span-level, not file-level: two types declared in one file don't mask each other.
    /// </summary>
    private static bool IsInsideOwnDeclaration(ISymbol symbol, Location location)
        => symbol.DeclaringSyntaxReferences.Any(d =>
            d.SyntaxTree == location.SourceTree && d.Span.Contains(location.SourceSpan));

    /// <summary>
    /// True when a copy is the method its compilation reports as the entry point: top-level statements'
    /// synthesized method or a classic <c>static Main</c>. Asked of the compilation rather than matched by
    /// name, so a <c>Main</c> the compiler ignores (a library's, or one shadowed by top-level statements)
    /// is still judged like any other method.
    /// </summary>
    private static async Task<bool> IsEntryPointAsync(IReadOnlyList<ISymbol> copies, Solution solution, CancellationToken ct)
    {
        foreach (var copy in copies)
        {
            if (copy is not IMethodSymbol { IsStatic: true } method) continue;
            var project = solution.GetProject(method.ContainingAssembly, ct);
            var compilation = project is null ? null : await project.GetCompilationAsync(ct);
            if (SymbolEqualityComparer.Default.Equals(compilation?.GetEntryPoint(ct), method)) return true;
        }
        return false;
    }

    private static int ExposureRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => 5,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Protected => 3,
        Accessibility.Internal => 2,
        Accessibility.ProtectedAndInternal => 1,
        _ => 0,
    };

    private static bool IsDenylisted(ISymbol symbol)
    {
        // Compiler-synthesized members — a record's EqualityContract/PrintMembers/copy-constructor,
        // property backing fields, implicit default constructors. Nobody can delete these, and on a
        // record-heavy solution they crowd every real finding out of maxResults (TOOL-006).
        if (symbol.IsImplicitlyDeclared) return true;

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
