using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;
using RoslynSymbolInfo = Microsoft.CodeAnalysis.SymbolInfo;

namespace mcpRoslyn.Tools;

public sealed record WorkspaceSymbolResult(IReadOnlyList<Contracts.SymbolInfo> Symbols);

[McpServerToolType]
internal sealed class WorkspaceSymbolTool(IWorkspaceService ws, ILogger<WorkspaceSymbolTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "workspace_symbol")]
    [Description("Fuzzy name search across the entire solution. Returns up to maxResults symbols.")]
    public Task<Contracts.ToolResult<WorkspaceSymbolResult>> InvokeAsync(
        string query,
        string[]? kinds,
        int? maxResults,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var cap = maxResults ?? 100;

            var allowedKinds = kinds is null
                ? null
                : new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);

            var dedup = new HashSet<string>();
            var results = new List<Contracts.SymbolInfo>();

            foreach (var project in solution.Projects)
            {
                if (results.Count >= cap) break;
                var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                    project, query,
                    SymbolFilter.Type | SymbolFilter.Member,
                    ct2);

                foreach (var sym in found)
                {
                    if (results.Count >= cap) break;
                    var info = RoslynHelpers.ToSymbolInfo(sym);

                    if (allowedKinds is not null)
                    {
                        var classifier = ClassifyKind(sym);
                        if (!allowedKinds.Contains(classifier)) continue;
                    }

                    if (!dedup.Add(info.SymbolId)) continue;
                    results.Add(info);
                }
            }

            var result = new WorkspaceSymbolResult(results);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<WorkspaceSymbolResult>.OkSummary($"{result.Symbols.Count} matching symbols");
            return Contracts.ToolResult<WorkspaceSymbolResult>.Ok(result);
        }, ct);

    private static string ClassifyKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "Interface",
        INamedTypeSymbol { TypeKind: TypeKind.Class } => "Class",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "Struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "Enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "Delegate",
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        IEventSymbol => "Event",
        INamespaceSymbol => "Namespace",
        _ => symbol.Kind.ToString()
    };
}
