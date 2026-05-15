using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record FindDerivedTypesResult(IReadOnlyList<Contracts.SymbolInfo> DerivedTypes);

[McpServerToolType]
internal sealed class FindDerivedTypesTool(IWorkspaceService ws, ILogger<FindDerivedTypesTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_derived_types")]
    [Description("For a class, returns all subclasses. For an interface, returns all derived interfaces. Use find_implementations for concrete implementations of an interface.")]
    public Task<Contracts.ToolResult<FindDerivedTypesResult>> InvokeAsync(
        string? filePath, int? line, int? column, string? symbolId,
        bool transitive,
        CancellationToken ct)
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
                    return Contracts.ToolResult<FindDerivedTypesResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
            }
            else
            {
                return Contracts.ToolResult<FindDerivedTypesResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            if (symbol is not INamedTypeSymbol named)
                return Contracts.ToolResult<FindDerivedTypesResult>.Fail(
                    "SYMBOL_NOT_FOUND", "Symbol is not a named type.");

            IEnumerable<INamedTypeSymbol> derived = named.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindDerivedInterfacesAsync(named, solution, transitive, projects: null, ct2)
                : await SymbolFinder.FindDerivedClassesAsync(named, solution, transitive, projects: null, ct2);

            var results = derived.Select(RoslynHelpers.ToSymbolInfo).ToList();
            return Contracts.ToolResult<FindDerivedTypesResult>.Ok(new FindDerivedTypesResult(results));
        }, ct);
}
