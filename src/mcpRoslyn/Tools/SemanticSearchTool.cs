using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record SemanticSearchResult(IReadOnlyList<Contracts.SymbolInfo> Matches);

[McpServerToolType]
internal sealed class SemanticSearchTool(IWorkspaceService ws, ILogger<SemanticSearchTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "semantic_search")]
    [Description("Pattern queries Roslyn can answer but Grep cannot. Patterns: " +
                 "derives-from:Namespace.Type, implements:Namespace.IInterface, " +
                 "has-attribute:Namespace.MyAttribute, returns:Namespace.Type, " +
                 "parameter-type:Namespace.Type. Type can also be a primitive alias like 'int' or 'string'.")]
    public Task<Contracts.ToolResult<SemanticSearchResult>> InvokeAsync(
        string pattern,
        CancellationToken ct)
        => ExecuteAsync(async ct2 =>
        {
            var colonIdx = pattern.IndexOf(':');
            if (colonIdx <= 0)
                return Contracts.ToolResult<SemanticSearchResult>.Fail(
                    "INVALID_PATTERN", $"Pattern must be of form 'kind:target': {pattern}");

            var kind = pattern[..colonIdx];
            var target = pattern[(colonIdx + 1)..];
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var matches = new List<Contracts.SymbolInfo>();
            var dedup = new HashSet<string>();

            switch (kind)
            {
                case "derives-from":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var derived = await SymbolFinder.FindDerivedClassesAsync(named, solution, transitive: true, projects: null, ct2);
                    foreach (var d in derived) AddIfNew(matches, dedup, d);
                    break;
                }
                case "implements":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var impls = await SymbolFinder.FindImplementationsAsync(named, solution, transitive: true, projects: null, ct2);
                    foreach (var i in impls) AddIfNew(matches, dedup, i);
                    break;
                }
                case "has-attribute":
                {
                    foreach (var project in solution.Projects)
                    {
                        var compilation = await project.GetCompilationAsync(ct2);
                        if (compilation is null) continue;

                        foreach (var sym in WalkAllSymbols(compilation))
                        {
                            if (sym.GetAttributes().Any(a => MatchesTypeName(a.AttributeClass, target)))
                                AddIfNew(matches, dedup, sym);
                        }
                    }
                    break;
                }
                case "returns":
                {
                    foreach (var project in solution.Projects)
                    {
                        var compilation = await project.GetCompilationAsync(ct2);
                        if (compilation is null) continue;

                        foreach (var sym in WalkAllSymbols(compilation))
                        {
                            if (sym is IMethodSymbol m && MatchesTypeName(m.ReturnType, target))
                                AddIfNew(matches, dedup, m);
                        }
                    }
                    break;
                }
                case "parameter-type":
                {
                    foreach (var project in solution.Projects)
                    {
                        var compilation = await project.GetCompilationAsync(ct2);
                        if (compilation is null) continue;

                        foreach (var sym in WalkAllSymbols(compilation))
                        {
                            if (sym is IMethodSymbol m &&
                                m.Parameters.Any(p => MatchesTypeName(p.Type, target)))
                                AddIfNew(matches, dedup, m);
                        }
                    }
                    break;
                }
                default:
                    return Contracts.ToolResult<SemanticSearchResult>.Fail(
                        "INVALID_PATTERN", $"Unknown pattern kind: {kind}");
            }

            return Contracts.ToolResult<SemanticSearchResult>.Ok(new SemanticSearchResult(matches));
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

    private static bool MatchesTypeName(ITypeSymbol? type, string target)
    {
        if (type is null) return false;
        // Try display string first — this handles primitive aliases like "int", "string"
        var displayName = type.ToDisplayString();
        if (displayName == target) return true;
        // Also try fully-qualified metadata name (e.g. "TestLib.MyMarkerAttribute")
        var metadataName = $"{type.ContainingNamespace?.ToDisplayString()}.{type.MetadataName}".TrimStart('.');
        return metadataName == target;
    }

    private static IEnumerable<ISymbol> WalkAllSymbols(Compilation compilation)
    {
        foreach (var sym in WalkNamespace(compilation.GlobalNamespace)) yield return sym;

        static IEnumerable<ISymbol> WalkNamespace(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    foreach (var nested in WalkNamespace(child)) yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in WalkType(type)) yield return nested;
                }
            }
        }

        static IEnumerable<ISymbol> WalkType(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                yield return member;
                if (member is INamedTypeSymbol nestedType)
                {
                    foreach (var nested in WalkType(nestedType)) yield return nested;
                }
            }
        }
    }
}
