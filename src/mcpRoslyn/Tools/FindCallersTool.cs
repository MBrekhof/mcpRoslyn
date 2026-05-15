using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record CallerEntry(Contracts.SymbolInfo Caller, Contracts.SymbolLocation CallSite);
public sealed record FindCallersResult(IReadOnlyList<CallerEntry> Callers);

[McpServerToolType]
internal sealed class FindCallersTool(IWorkspaceService ws, ILogger<FindCallersTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_callers")]
    [Description("Returns all methods that invoke the method at the cursor. Pass either (filePath, line, column) OR symbolId. transitive=true follows call chains recursively.")]
    public Task<Contracts.ToolResult<FindCallersResult>> InvokeAsync(
        string? filePath, int? line, int? column, string? symbolId,
        bool transitive = false,
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
                    return Contracts.ToolResult<FindCallersResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
            }
            else
            {
                return Contracts.ToolResult<FindCallersResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            if (symbol is null)
                return Contracts.ToolResult<FindCallersResult>.Fail(
                    "SYMBOL_NOT_FOUND", "Could not resolve symbol.");

            var entries = new List<CallerEntry>();
            var seen = new HashSet<string>();

            await FindCallersRecursiveAsync(symbol, solution, transitive, entries, seen, ct2);

            return Contracts.ToolResult<FindCallersResult>.Ok(new FindCallersResult(entries));
        }, ct);

    private static async Task FindCallersRecursiveAsync(
        ISymbol symbol, Solution solution, bool transitive,
        List<CallerEntry> entries, HashSet<string> seen, CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
        foreach (var caller in callers)
        {
            foreach (var loc in caller.Locations)
            {
                var site = RoslynHelpers.ToLocation(loc);
                if (site is null) continue;
                entries.Add(new CallerEntry(RoslynHelpers.ToSymbolInfo(caller.CallingSymbol), site));
            }

            if (transitive)
            {
                var callerId = caller.CallingSymbol.OriginalDefinition.ToDisplayString();
                if (seen.Add(callerId))
                    await FindCallersRecursiveAsync(caller.CallingSymbol, solution, true, entries, seen, ct);
            }
        }
    }
}
