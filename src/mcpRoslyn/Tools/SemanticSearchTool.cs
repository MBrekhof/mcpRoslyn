using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record SemanticSearchResult(IReadOnlyList<Contracts.SymbolInfo> Matches, bool Truncated = false);

[McpServerToolType]
internal sealed class SemanticSearchTool(IWorkspaceService ws, ILogger<SemanticSearchTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "semantic_search")]
    [Description("Pattern queries Roslyn can answer but Grep cannot. Patterns: " +
                 "derives-from:Namespace.Type, implements:Namespace.IInterface, " +
                 "has-attribute:Namespace.MyAttribute, returns:Namespace.Type, " +
                 "parameter-type:Namespace.Type. Type can also be a primitive alias like 'int' or 'string'. " +
                 "Returns up to maxResults matches (default 50); truncated=true means more matched.")]
    public Task<Contracts.ToolResult<SemanticSearchResult>> InvokeAsync(
        string pattern,
        int maxResults = 50,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var colonIdx = pattern.IndexOf(':');
            if (colonIdx <= 0)
                return Contracts.ToolResult<SemanticSearchResult>.Fail(
                    "INVALID_PATTERN", $"Pattern must be of form 'kind:target': {pattern}");

            var kind = pattern[..colonIdx];
            var target = pattern[(colonIdx + 1)..];
            var solution = await Workspace.GetFreshSolutionAsync(ct2);

            SemanticSearchResult? result = null;
            switch (kind)
            {
                case "derives-from":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var derived = await SymbolFinder.FindDerivedClassesAsync(named, solution, transitive: true, projects: null, ct2);
                    var matches = new List<Contracts.SymbolInfo>();
                    var dedup = new HashSet<string>();
                    foreach (var d in derived) AddIfNew(matches, dedup, d);
                    result = new SemanticSearchResult(matches);
                    break;
                }
                case "implements":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var impls = await SymbolFinder.FindImplementationsAsync(named, solution, transitive: true, projects: null, ct2);
                    var matches = new List<Contracts.SymbolInfo>();
                    var dedup = new HashSet<string>();
                    foreach (var i in impls) AddIfNew(matches, dedup, i);
                    result = new SemanticSearchResult(matches);
                    break;
                }
                case "has-attribute":
                    result = new SemanticSearchResult(Workspace.SymbolIndex.QueryAttribute(target, solution, ct2));
                    break;
                case "returns":
                    result = new SemanticSearchResult(Workspace.SymbolIndex.QueryReturnType(target, solution, ct2));
                    break;
                case "parameter-type":
                    result = new SemanticSearchResult(Workspace.SymbolIndex.QueryParameterType(target, solution, ct2));
                    break;
                default:
                    return Contracts.ToolResult<SemanticSearchResult>.Fail(
                        "INVALID_PATTERN", $"Unknown pattern kind: {kind}");
            }

            // PERF-002: uncapped before — one parameter-type: query was ~5k tokens on BPG, and a
            // primitive target (returns:string) on a large solution is unbounded.
            var cap = Math.Max(0, maxResults);
            if (result.Matches.Count > cap)
                result = new SemanticSearchResult(result.Matches.Take(cap).ToArray(), Truncated: true);

            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<SemanticSearchResult>.OkSummary(
                    $"{result.Matches.Count} matches{(result.Truncated ? " (truncated)" : "")}");
            return Contracts.ToolResult<SemanticSearchResult>.Ok(result);
        }, ct);

    private static void AddIfNew(List<Contracts.SymbolInfo> matches, HashSet<string> dedup, ISymbol sym)
    {
        var info = RoslynHelpers.ToSymbolInfo(sym);
        if (dedup.Add(info.SymbolId.Length > 0 ? info.SymbolId : sym.ToDisplayString()))
            matches.Add(info);
    }

    private static async Task<ISymbol?> FindTypeByDisplayNameAsync(
        Solution solution, string target, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var sym = compilation.GetTypeByMetadataName(target);
            if (sym is not null) return sym;
        }
        return null;
    }
}
