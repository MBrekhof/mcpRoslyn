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
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            ISymbol? symbol = null;

            if (!string.IsNullOrWhiteSpace(symbolId))
            {
                symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, symbolId, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<FindCallersResult>.Fail(
                        "SYMBOL_NOT_FOUND",
                        $"Symbol ID '{symbolId}' did not resolve in any loaded project.",
                        hint: "DocumentationCommentIds are brittle across signature changes. " +
                              "Call workspace_symbol with the method name to discover the current symbolId, " +
                              "then re-issue find_callers with the fresh value.");
            }
            else if (filePath is not null && line is int l && column is int c)
            {
                var doc = RoslynHelpers.FindDocument(solution, filePath);
                if (doc is null)
                    return Contracts.ToolResult<FindCallersResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<FindCallersResult>.Fail(
                        "SYMBOL_NOT_FOUND",
                        $"No symbol at {filePath}:{l}:{c}.",
                        hint: "Verify the line/column points at an identifier, " +
                              "or use workspace_symbol to locate the method by name.");
            }
            else
            {
                return Contracts.ToolResult<FindCallersResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            var entries = new List<CallerEntry>();
            var seen = new HashSet<string> { symbol.OriginalDefinition.ToDisplayString() };
            var seenSites = new HashSet<(string Caller, string FilePath, int Line, int Column, int EndLine, int EndColumn)>();

            await FindCallersRecursiveAsync(symbol, solution, transitive, entries, seen, seenSites, ct2);

            var result = new FindCallersResult(entries);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<FindCallersResult>.OkSummary($"{result.Callers.Count} caller sites");
            return Contracts.ToolResult<FindCallersResult>.Ok(result);
        }, ct);

    private static async Task FindCallersRecursiveAsync(
        ISymbol symbol, Solution solution, bool transitive,
        List<CallerEntry> entries, HashSet<string> seen,
        HashSet<(string Caller, string FilePath, int Line, int Column, int EndLine, int EndColumn)> seenSites,
        CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
        foreach (var caller in callers)
        {
            var callerId = caller.CallingSymbol.OriginalDefinition.ToDisplayString();
            var callerInfo = RoslynHelpers.ToSymbolInfo(caller.CallingSymbol);
            foreach (var loc in caller.Locations)
            {
                var site = RoslynHelpers.ToLocation(loc);
                if (site is null) continue;
                if (seenSites.Add((callerId, site.FilePath, site.Line, site.Column, site.EndLine, site.EndColumn)))
                    entries.Add(new CallerEntry(callerInfo, site));
            }

            if (transitive && seen.Add(callerId))
            {
                await FindCallersRecursiveAsync(caller.CallingSymbol, solution, true, entries, seen, seenSites, ct);
            }
        }
    }
}
