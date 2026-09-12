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

    public static async Task<ISymbol?> ResolveSymbolByIdAsync(
        Solution solution, string symbolId, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(symbolId, compilation);
            if (symbols.Length > 0) return symbols[0];
        }
        return null;
    }

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
