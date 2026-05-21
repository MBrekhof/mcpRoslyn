using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;
using RoslynSymbolInfo = Microsoft.CodeAnalysis.SymbolInfo;

namespace mcpRoslyn.Tools;

public sealed record ListDocumentSymbolsResult(IReadOnlyList<Contracts.SymbolInfo> Symbols);

[McpServerToolType]
internal sealed class ListDocumentSymbolsTool(IWorkspaceService ws, ILogger<ListDocumentSymbolsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "list_document_symbols")]
    [Description("Returns an outline of one file — top-level types and their members.")]
    public Task<Contracts.ToolResult<ListDocumentSymbolsResult>> InvokeAsync(
        string filePath,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var doc = RoslynHelpers.FindDocument(solution, filePath);
            if (doc is null)
                return Contracts.ToolResult<ListDocumentSymbolsResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE",
                    $"File not in workspace: {filePath}",
                    "Did you mean to call reload_workspace?");

            var semantic = await doc.GetSemanticModelAsync(ct2);
            var root = await doc.GetSyntaxRootAsync(ct2);
            if (semantic is null || root is null)
                return Contracts.ToolResult<ListDocumentSymbolsResult>.Fail(
                    "INTERNAL_ERROR",
                    "Could not obtain semantic model or syntax root.");

            var symbols = new List<Contracts.SymbolInfo>();
            foreach (var node in root.DescendantNodes())
            {
                if (node is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax
                    or PropertyDeclarationSyntax or FieldDeclarationSyntax
                    or EventDeclarationSyntax or EventFieldDeclarationSyntax)
                {
                    // FieldDeclarationSyntax / EventFieldDeclarationSyntax declare multiple variables.
                    // GetDeclaredSymbol on the parent node returns null; we need to walk variables.
                    if (node is FieldDeclarationSyntax field)
                    {
                        foreach (var v in field.Declaration.Variables)
                        {
                            var fieldSym = semantic.GetDeclaredSymbol(v, ct2);
                            if (fieldSym is not null) symbols.Add(RoslynHelpers.ToSymbolInfo(fieldSym));
                        }
                        continue;
                    }
                    if (node is EventFieldDeclarationSyntax evt)
                    {
                        foreach (var v in evt.Declaration.Variables)
                        {
                            var evtSym = semantic.GetDeclaredSymbol(v, ct2);
                            if (evtSym is not null) symbols.Add(RoslynHelpers.ToSymbolInfo(evtSym));
                        }
                        continue;
                    }

                    var symbol = semantic.GetDeclaredSymbol(node, ct2);
                    if (symbol is null) continue;

                    symbols.Add(RoslynHelpers.ToSymbolInfo(symbol));
                }
            }

            var result = new ListDocumentSymbolsResult(symbols);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<ListDocumentSymbolsResult>.OkSummary($"{result.Symbols.Count} symbols in document");
            return Contracts.ToolResult<ListDocumentSymbolsResult>.Ok(result);
        }, ct);
}
