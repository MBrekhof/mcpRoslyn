using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record FindReferencesResult(
    Contracts.SymbolInfo Symbol,
    IReadOnlyList<Contracts.SymbolLocation> References);

[McpServerToolType]
internal sealed class FindReferencesTool(IWorkspaceService ws, ILogger<FindReferencesTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_references")]
    [Description("Returns every reference site for the symbol at the cursor or identified by symbolId. " +
                 "Pass either (filePath, line, column) OR symbolId.")]
    public Task<Contracts.ToolResult<FindReferencesResult>> InvokeAsync(
        string? filePath, int? line, int? column, string? symbolId,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            ISymbol? symbol = null;

            if (!string.IsNullOrWhiteSpace(symbolId))
            {
                symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, symbolId, ct2);
            }
            else if (filePath is not null && line is int l && column is int c)
            {
                var doc = RoslynHelpers.FindDocument(solution, filePath);
                if (doc is null)
                    return Contracts.ToolResult<FindReferencesResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
            }
            else
            {
                return Contracts.ToolResult<FindReferencesResult>.Fail(
                    "POSITION_INVALID",
                    "Must provide either symbolId OR (filePath, line, column).");
            }

            if (symbol is null)
                return Contracts.ToolResult<FindReferencesResult>.Fail(
                    "SYMBOL_NOT_FOUND", "Could not resolve symbol.");

            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct2);

            var locations = new List<Contracts.SymbolLocation>();
            foreach (var r in refs)
            {
                foreach (var loc in r.Locations)
                {
                    var sl = RoslynHelpers.ToLocation(loc.Location);
                    if (sl is not null) locations.Add(sl);
                }
            }

            var result = new FindReferencesResult(RoslynHelpers.ToSymbolInfo(symbol), locations);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<FindReferencesResult>.OkSummary(
                    $"{result.References.Count} references in {result.References.Select(r => r.FilePath).Distinct().Count()} files");
            return Contracts.ToolResult<FindReferencesResult>.Ok(result);
        }, ct);
}
