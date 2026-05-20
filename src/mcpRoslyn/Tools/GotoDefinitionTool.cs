using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record GotoDefinitionResult(IReadOnlyList<Contracts.SymbolLocation> Definitions);

[McpServerToolType]
internal sealed class GotoDefinitionTool(IWorkspaceService ws, ILogger<GotoDefinitionTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "goto_definition")]
    [Description("Returns all source-declaration locations for the symbol at the given cursor position. Multiple locations possible for partial classes.")]
    public Task<Contracts.ToolResult<GotoDefinitionResult>> InvokeAsync(
        string filePath, int line, int column,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var doc = RoslynHelpers.FindDocument(solution, filePath);
            if (doc is null)
                return Contracts.ToolResult<GotoDefinitionResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}",
                    "Did you mean to call reload_workspace?");

            var symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, line, column, ct2);
            if (symbol is null)
                return Contracts.ToolResult<GotoDefinitionResult>.Fail(
                    "SYMBOL_NOT_FOUND", $"No symbol at {filePath}:{line}:{column}");

            var locations = symbol.Locations
                .Select(RoslynHelpers.ToLocation)
                .Where(l => l is not null)
                .Cast<Contracts.SymbolLocation>()
                .ToList();

            var result = new GotoDefinitionResult(locations);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var summaryText = result.Definitions.Count > 0
                    ? $"definition at {result.Definitions[0].FilePath}:{result.Definitions[0].Line}"
                    : "no definition found";
                return Contracts.ToolResult<GotoDefinitionResult>.OkSummary(summaryText);
            }
            return Contracts.ToolResult<GotoDefinitionResult>.Ok(result);
        }, ct);
}
