using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record HoverResult(
    Contracts.SymbolInfo Symbol,
    string? XmlDocSummary,
    string Signature);

[McpServerToolType]
internal sealed class HoverTool(IWorkspaceService ws, ILogger<HoverTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "hover")]
    [Description("Returns type/signature info and XML doc summary for the symbol at the given cursor position.")]
    public Task<Contracts.ToolResult<HoverResult>> InvokeAsync(
        string filePath, int line, int column,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var doc = RoslynHelpers.FindDocument(solution, filePath);
            if (doc is null)
                return Contracts.ToolResult<HoverResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");

            var symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, line, column, ct2);
            if (symbol is null)
                return Contracts.ToolResult<HoverResult>.Fail(
                    "SYMBOL_NOT_FOUND", $"No symbol at {filePath}:{line}:{column}");

            var symbolInfo = RoslynHelpers.ToSymbolInfo(symbol);
            var signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var xmlDoc = ExtractSummary(symbol.GetDocumentationCommentXml(cancellationToken: ct2));

            var result = new HoverResult(symbolInfo, xmlDoc, signature);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<HoverResult>.OkSummary(result.XmlDocSummary ?? result.Signature);
            return Contracts.ToolResult<HoverResult>.Ok(result);
        }, ct);

    private static string? ExtractSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            return summary?.Value.Trim();
        }
        catch { return null; }
    }
}
