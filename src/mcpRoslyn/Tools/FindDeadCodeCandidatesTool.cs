using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

/// <param name="KeptAliveBy">For reason "only-referenced-by-dead-code": the dead declarations every reference sits in
/// (TOOL-013). Null otherwise.</param>
public sealed record DeadCodeCandidate(
    string Symbol,
    string Kind,
    string Accessibility,
    Contracts.SymbolLocation? Location,
    string Confidence,    // "high" | "medium"
    string Reason,
    IReadOnlyList<string>? KeptAliveBy = null);

public sealed record DeadCodeSkipped(
    int PublicMembers,
    int Tests,
    int Denylisted,
    int DiRegistered = 0,
    int FrameworkReached = 0);

/// <summary>A DI registration whose observed constructor consumers are all dead-code candidates (TOOL-013).</summary>
public sealed record RegistrationWithOnlyDeadConsumers(
    string ServiceType,
    string? ImplType,
    Contracts.SymbolLocation Registration,
    IReadOnlyList<string> Consumers);

/// <param name="RegistrationsWithOnlyDeadConsumers">With includePublicTypes: registrations whose every observed
/// constructor consumer is a candidate. Evidence, not a verdict: a service can also be resolved through
/// GetRequiredService, IEnumerable&lt;T&gt; injection or minimal-API parameters. Null without includePublicTypes.</param>
public sealed record FindDeadCodeResult(
    IReadOnlyList<DeadCodeCandidate> Candidates,
    IReadOnlyList<string> ProjectsScanned,
    DeadCodeSkipped Skipped,
    bool Truncated = false,
    IReadOnlyList<RegistrationWithOnlyDeadConsumers>? RegistrationsWithOnlyDeadConsumers = null);

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
    [Description("Returns members with no references: private/internal by default, plus unreferenced public types when includePublicTypes is set. Skips attributed members ([Fact]/[Test]/[JsonConstructor]/…) and framework contracts (Dispose/Equals/…). Public types that are DI-registered or framework-reached (Controller/Hub/extension classes) are suppressed. Marks internal members as medium-confidence when [InternalsVisibleTo] applies. Symbols referenced only from other candidates are reported too, at medium confidence with reason only-referenced-by-dead-code and keptAliveBy naming that dead code. With includePublicTypes, registrationsWithOnlyDeadConsumers lists DI registrations whose every constructor consumer is a candidate: evidence to check, not a verdict.")]
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
            var candidates = new List<DeadCodeCandidate>();
            var deadDeclarations = new List<DeadDeclaration>();
            var stillReferenced = new List<ReferencedSymbol>();

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

                // The runtime calls these, so nothing in source ever references them: the entry point (TOOL-012), static
                // constructors and finalizers. They are neither dead nor roots for the chain pass below (TOOL-013).
                if (copies.Any(c => c is IMethodSymbol { MethodKind: MethodKind.StaticConstructor or MethodKind.Destructor })
                    || await IsEntryPointAsync(copies, solution, ct2)) { frameworkSkip++; continue; }
                if (copies.Any(IsDenylisted)) { denySkip++; continue; }

                // Reference scan, for every eligible symbol. The chain pass below needs each one's references, and a
                // symbol left unscanned could only count as unknown, never as dead, so this no longer stops once
                // maxResults is reached (TOOL-013 replaces TOOL-003's early stop; the cap applies at the end). For a
                // public type, references from inside its own declaration (a static factory naming itself, a nested
                // helper) are not evidence that anything else uses it. A linked declaration is dead only if no
                // assembly compiling it uses its copy, so every copy's references count.
                var references = new List<Location>();
                foreach (var copy in copies)
                {
                    var refs = await SymbolFinder.FindReferencesAsync(copy, solution, ct2);
                    references.AddRange(refs.SelectMany(r => r.Locations).Select(l => l.Location)
                        .Where(l => !isPublicSurface || !IsInsideOwnDeclaration(copy, l)));
                }
                if (references.Count > 0)
                {
                    stillReferenced.Add(new ReferencedSymbol(entry.Info.Signature, symbol, copies, loc, references));
                    continue;
                }

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
                deadDeclarations.AddRange(DeclarationsOf(copies, entry.Info.Signature));
            }

            // TOOL-013: to a fixed point, a symbol whose every reference sits inside an already-dead declaration is dead
            // too. Medium confidence, since one wrongly-dead root takes its whole chain with it; KeptAliveBy names
            // the dead code holding each reference so that root can be checked. A reference outside source (or in a
            // symbol that was never eligible, like a public member) keeps it alive.
            for (var changed = true; changed;)
            {
                changed = false;
                for (var i = stillReferenced.Count - 1; i >= 0; i--)
                {
                    var pending = stillReferenced[i];
                    var keepers = new SortedSet<string>(StringComparer.Ordinal);
                    var onlyFromDeadCode = true;
                    foreach (var reference in pending.References)
                    {
                        if (InnermostDeadDeclaration(reference, deadDeclarations) is not { } keeper) { onlyFromDeadCode = false; break; }
                        keepers.Add(keeper);
                    }
                    if (!onlyFromDeadCode) continue;

                    candidates.Add(new DeadCodeCandidate(
                        Symbol: pending.Signature,
                        Kind: pending.Symbol.Kind.ToString(),
                        Accessibility: pending.Symbol.DeclaredAccessibility.ToString(),
                        Location: pending.Location,
                        Confidence: "medium",
                        Reason: "only-referenced-by-dead-code",
                        KeptAliveBy: keepers.ToArray()));
                    deadDeclarations.AddRange(DeclarationsOf(pending.Copies, pending.Signature));
                    stillReferenced.RemoveAt(i);
                    changed = true;
                }
            }

            // TOOL-013: registrations whose every observed constructor consumer is a candidate. Evidence, not a verdict,
            // so they are listed apart from the candidates: GetRequiredService<T>, IEnumerable<T> injection or a
            // minimal-API parameter resolve a service with no constructor consumer. A registration with no observed
            // consumer at all is not listed, since that proves nothing either way.
            IReadOnlyList<RegistrationWithOnlyDeadConsumers>? onlyDeadConsumers = null;
            if (includePublicTypes)
            {
                // Keyed by name and declaring file, so a live same-named type in another project can't pass for the dead
                // one. A consumer whose constructor sits in another file of a partial type is never matched: the safe way.
                var dead = candidates.Select(c => (c.Symbol, c.Location?.FilePath)).ToHashSet();
                onlyDeadConsumers = ready.InvocationIndex.QueryDi().Registrations
                    .Where(r => r.ServiceType is not null)
                    .Select(r => (Registration: r, Consumers: FindRegistrationsTool
                        .FindConsumers(r.ServiceType!, solution, ready.SymbolIndex).ToArray()))
                    .Where(x => x.Consumers.Length > 0
                                && x.Consumers.All(c => dead.Contains((c.Type, c.CtorLocation?.FilePath))))
                    .Select(x => new RegistrationWithOnlyDeadConsumers(
                        x.Registration.ServiceType!, x.Registration.ImplType, x.Registration.Location,
                        x.Consumers.Select(c => c.Type).ToArray()))
                    .ToArray();
            }

            // The cap applies to the finished analysis, so a capped result is a prefix of the full one.
            var truncated = candidates.Count > maxResults;
            if (truncated) candidates = candidates.Take(maxResults).ToList();

            var result = new FindDeadCodeResult(
                Candidates: candidates,
                ProjectsScanned: solution.Projects.Select(p => p.Name).ToArray(),
                Skipped: new DeadCodeSkipped(publicSkip, testSkip, denySkip, diSkip, frameworkSkip),
                Truncated: truncated,
                RegistrationsWithOnlyDeadConsumers: onlyDeadConsumers);
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
    private sealed record DeadDeclaration(SyntaxTree Tree, TextSpan Span, string Symbol);

    private sealed record ReferencedSymbol(
        string Signature, ISymbol Symbol, IReadOnlyList<ISymbol> Copies, Contracts.SymbolLocation Location,
        IReadOnlyList<Location> References);

    private static IEnumerable<DeadDeclaration> DeclarationsOf(IEnumerable<ISymbol> copies, string signature)
        => copies.SelectMany(PartsOf).SelectMany(s => s.DeclaringSyntaxReferences)
            .Select(d => new DeadDeclaration(d.SyntaxTree, CoveredSpan(d.GetSyntax()), signature));

    /// <summary>A partial method's body lives in its implementation part, and either part can be the one resolved.</summary>
    private static IEnumerable<ISymbol> PartsOf(ISymbol symbol) => symbol is IMethodSymbol method
        ? new ISymbol?[] { method, method.PartialDefinitionPart, method.PartialImplementationPart }
            .OfType<ISymbol>().Distinct(SymbolEqualityComparer.Default)
        : [symbol];

    /// <summary>
    /// The part of a declaration that dies with its symbol. A field's declaring syntax is only its declarator: when it is
    /// the declaration's sole variable, the type before it belongs to it too, while with siblings (<c>DeadType a, b;</c>)
    /// the shared type stays out so a live sibling still keeps it alive. An initializer never belongs to it: it runs
    /// whenever the type or instance initializes, whether or not the field or property is ever read.
    /// </summary>
    private static TextSpan CoveredSpan(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: BaseFieldDeclarationSyntax field } } declarator
            => TextSpan.FromBounds(field.SpanStart, declarator.Initializer is null ? field.Span.End : declarator.Identifier.Span.End),
        VariableDeclaratorSyntax { Initializer: not null } declarator => declarator.Identifier.Span,
        PropertyDeclarationSyntax { Initializer: not null } property => TextSpan.FromBounds(property.SpanStart, property.Initializer.SpanStart),
        _ => node.Span,
    };

    /// <summary>
    /// The innermost dead declaration containing <paramref name="reference"/> (a dead method rather than the dead type
    /// around it), or null when the reference is outside dead code or outside source.
    /// </summary>
    private static string? InnermostDeadDeclaration(Location reference, List<DeadDeclaration> dead)
    {
        if (!reference.IsInSource) return null;
        DeadDeclaration? innermost = null;
        foreach (var declaration in dead)
        {
            if (declaration.Tree != reference.SourceTree || !declaration.Span.Contains(reference.SourceSpan)) continue;
            if (innermost is null || declaration.Span.Length < innermost.Span.Length) innermost = declaration;
        }
        return innermost?.Symbol;
    }

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
