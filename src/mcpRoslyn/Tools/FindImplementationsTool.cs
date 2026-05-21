using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record FindImplementationsResult(IReadOnlyList<Contracts.SymbolLocation> Implementations);

[McpServerToolType]
internal sealed class FindImplementationsTool(IWorkspaceService ws, ILogger<FindImplementationsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_implementations")]
    [Description("For an interface, interface member, or abstract member: returns all concrete implementations.")]
    public Task<Contracts.ToolResult<FindImplementationsResult>> InvokeAsync(
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
                    return Contracts.ToolResult<FindImplementationsResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
            }
            else
            {
                return Contracts.ToolResult<FindImplementationsResult>.Fail(
                    "POSITION_INVALID",
                    "Must provide either symbolId OR (filePath, line, column).");
            }

            if (symbol is null)
                return Contracts.ToolResult<FindImplementationsResult>.Fail(
                    "SYMBOL_NOT_FOUND", "Could not resolve symbol.");

            var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution, projects: null, ct2);

            // Dedup by source span — an implementation site is conceptually unique per
            // source position. Mirrors the FindReferencesTool pattern; same Roslyn quirks
            // (partial declarations, multi-targeted projects) can otherwise double-count.
            var locations = new List<Contracts.SymbolLocation>();
            var seen = new HashSet<(string, int, int, int, int)>();
            foreach (var impl in impls)
            {
                foreach (var loc in impl.Locations)
                {
                    var sl = RoslynHelpers.ToLocation(loc);
                    if (sl is null) continue;
                    if (!seen.Add((sl.FilePath, sl.Line, sl.Column, sl.EndLine, sl.EndColumn))) continue;
                    locations.Add(sl);
                }
            }

            var result = new FindImplementationsResult(locations);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<FindImplementationsResult>.OkSummary($"{result.Implementations.Count} implementations");
            return Contracts.ToolResult<FindImplementationsResult>.Ok(result);
        }, ct);
}
