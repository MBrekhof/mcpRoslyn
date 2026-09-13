using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using mcpRoslyn.Contracts;
using RoslynSymbolInfo = Microsoft.CodeAnalysis.SymbolInfo;

namespace mcpRoslyn.Tools;

internal static class RoslynHelpers
{
    /// <summary>
    /// What find_implementations and analyze_symbol mean by implementations. Roslyn's FindImplementationsAsync
    /// answers for types and interface members only — for an abstract or virtual class member it returns
    /// nothing — so there the answer is the member's concrete overrides (TOOL-011); an abstract override
    /// passes the obligation on rather than meeting it. ponytail: an interface event's add/remove accessor
    /// queried directly finds nothing (Roslyn excludes those accessors) — query the event itself.
    /// </summary>
    public static async Task<IEnumerable<ISymbol>> FindImplementationsOrOverridesAsync(
        ISymbol symbol, Solution solution, CancellationToken ct)
    {
        if (symbol is not INamedTypeSymbol && symbol.ContainingType is { TypeKind: not TypeKind.Interface })
            return (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct)).Where(o => !o.IsAbstract);
        return await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct);
    }

    public static Document? FindDocument(Solution solution, string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                d.FilePath is not null &&
                string.Equals(Path.GetFullPath(d.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<ISymbol?> ResolveSymbolAtPositionAsync(
        Document document, int line, int column, CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct);
        // TOOL-010: unchecked, a column past the end of its line ran on into the following lines,
        // and every position-taking tool (rename included) would act on a symbol nobody pointed at.
        if (line < 1 || line > text.Lines.Count)
            throw new PositionInvalidException(
                $"Line {line} is outside {document.FilePath} (valid: 1-{text.Lines.Count}).");
        var lineSpan = text.Lines[line - 1].Span;
        if (column < 1 || column > lineSpan.Length + 1)
            throw new PositionInvalidException(
                $"Column {column} is outside line {line} of {document.FilePath} (valid: 1-{lineSpan.Length + 1}).");
        var position = lineSpan.Start + (column - 1);
        var semantic = await document.GetSemanticModelAsync(ct);
        if (semantic is null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null) return null;
        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null) return null;

        RoslynSymbolInfo info = semantic.GetSymbolInfo(node, ct);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault()
            ?? semantic.GetDeclaredSymbol(node, ct);
    }

    /// <summary>
    /// The symbol a documentation-comment id names. The id carries no assembly identity, so two
    /// projects declaring the same fully-qualified name share it; rather than silently answering about
    /// whichever project enumerates first, that throws <see cref="AmbiguousSymbolIdException"/> (IDX-005).
    /// One symbol seen through several compilations — a referenced project's source, or one assembly
    /// built for several target frameworks — counts once; a file linked into two assemblies counts twice.
    /// </summary>
    public static async Task<ISymbol?> ResolveSymbolByIdAsync(
        Solution solution, string symbolId, CancellationToken ct)
    {
        var found = new List<ISymbol>();
        // Same key shape as SymbolIndex: declaration file + assembly. No span — a multi-targeted
        // project's #if variants of one type are one assembly's symbol, as the index treats them.
        var seen = new HashSet<(string? File, string? Assembly)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var symbol in DocumentationCommentId.GetSymbolsForDeclarationId(symbolId, compilation))
            {
                var source = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                if (seen.Add((source?.SourceTree?.FilePath, symbol.ContainingAssembly?.Name)))
                    found.Add(symbol);
            }
        }

        // Each declaration names its assembly: a linked file's copies share file and line.
        if (found.Count > 1)
            throw new AmbiguousSymbolIdException(symbolId, found
                .Select(s => (Location: s.Locations.Select(ToLocation).FirstOrDefault(l => l is not null),
                              Assembly: s.ContainingAssembly?.Name))
                .Where(x => x.Location is not null)
                .Select(x => $"{x.Location!.FilePath}:{x.Location.Line} in {x.Assembly}")
                .ToList());
        return found.FirstOrDefault();
    }

    /// <summary>
    /// Every symbol one declaration produces: resolved in its declaring documents' own projects and
    /// matched on its declaration file, so a same-named type elsewhere can't stand in. Usually one; a
    /// file linked into several projects yields one symbol per assembly that compiles it (IDX-005).
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> ResolveDeclaredSymbolsAsync(
        Solution solution, IEnumerable<DocumentId> declaringDocs, string symbolId, string filePath, CancellationToken ct)
    {
        var matches = new List<ISymbol>();
        foreach (var projectId in declaringDocs.Select(d => d.ProjectId).Distinct())
        {
            if (solution.GetProject(projectId) is not { } project) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var match = DocumentationCommentId.GetSymbolsForDeclarationId(symbolId, compilation)
                .FirstOrDefault(s => s.Locations.Any(l => l.IsInSource
                    && string.Equals(l.SourceTree?.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));
            if (match is not null) matches.Add(match);
        }
        return matches;
    }

    /// <summary>A symbol id naming distinct symbols in different projects; ToolBase maps it to AMBIGUOUS_SYMBOL_ID (IDX-005).</summary>
    internal sealed class AmbiguousSymbolIdException(string symbolId, IReadOnlyList<string> locations)
        : Exception($"Symbol ID '{symbolId}' names {locations.Count} different symbols: {string.Join(", ", locations)}.");

    /// <summary>
    /// The project's own analyzers (NetAnalyzers, StyleCop, Roslynator, …) exactly as MSBuild resolved them,
    /// or <c>null</c> when it has none. Severities come from <c>project.AnalyzerOptions</c> and the
    /// compilation's .editorconfig/globalconfig tree options, and suppressed diagnostics are not reported,
    /// so a rule the project switched off stays off (DIAG-001).
    /// </summary>
    public static CompilationWithAnalyzers? WithProjectAnalyzers(Project project, Compilation compilation,
        Action<Exception, DiagnosticAnalyzer, Diagnostic>? onAnalyzerException = null)
    {
        var analyzers = project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzers(project.Language))
            .ToImmutableArray();
        return analyzers.IsEmpty ? null : compilation.WithAnalyzers(analyzers,
            new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
    }

    public static SymbolLocation? ToLocation(Location loc)
    {
        if (!loc.IsInSource || loc.SourceTree?.FilePath is null) return null;
        var span = loc.GetLineSpan();
        return new SymbolLocation(
            FilePath: loc.SourceTree.FilePath,
            Line: span.StartLinePosition.Line + 1,
            Column: span.StartLinePosition.Character + 1,
            EndLine: span.EndLinePosition.Line + 1,
            EndColumn: span.EndLinePosition.Character + 1);
    }

    /// <summary>A 1-based line/column outside the document; ToolBase maps it to POSITION_INVALID (TOOL-010).</summary>
    internal sealed class PositionInvalidException(string message) : Exception(message);

    public static Contracts.SymbolInfo ToSymbolInfo(ISymbol symbol)
        => new(
            Name: symbol.Name,
            Kind: symbol.Kind.ToString(),
            SymbolId: DocumentationCommentId.CreateDeclarationId(symbol) ?? "",
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            PrimaryLocation: symbol.Locations.Select(ToLocation).FirstOrDefault(l => l is not null));
}
