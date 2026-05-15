using Microsoft.CodeAnalysis;
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
        var position = text.Lines[line - 1].Start + (column - 1);
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
