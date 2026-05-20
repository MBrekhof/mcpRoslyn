using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record CalleeEntry(Contracts.SymbolInfo Callee, Contracts.SymbolLocation CallSite);
public sealed record FindCalleesResult(IReadOnlyList<CalleeEntry> Callees);

[McpServerToolType]
internal sealed class FindCalleesTool(IWorkspaceService ws, ILogger<FindCalleesTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_callees")]
    [Description("Returns methods called from the body of the method at the cursor or symbolId. Mirror of find_callers.")]
    public Task<Contracts.ToolResult<FindCalleesResult>> InvokeAsync(
        string? filePath = null, int? line = null, int? column = null, string? symbolId = null,
        int maxResults = 50,
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            ISymbol? symbol = null;

            if (!string.IsNullOrWhiteSpace(symbolId))
            {
                symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, symbolId, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<FindCalleesResult>.Fail(
                        "SYMBOL_NOT_FOUND",
                        $"Symbol ID '{symbolId}' did not resolve in any loaded project.",
                        hint: "DocumentationCommentIds are brittle across signature changes. " +
                              "Call workspace_symbol with the method name to discover the current symbolId.");
            }
            else if (filePath is not null && line is int l && column is int c)
            {
                var doc = RoslynHelpers.FindDocument(solution, filePath);
                if (doc is null)
                    return Contracts.ToolResult<FindCalleesResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<FindCalleesResult>.Fail(
                        "SYMBOL_NOT_FOUND",
                        $"No symbol at {filePath}:{l}:{c}.",
                        hint: "Verify the line/column points at an identifier, " +
                              "or use workspace_symbol to locate the method by name.");
            }
            else
            {
                return Contracts.ToolResult<FindCalleesResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            if (symbol is not IMethodSymbol method)
                return Contracts.ToolResult<FindCalleesResult>.Fail(
                    "SYMBOL_NOT_FOUND",
                    $"Symbol '{symbol.ToDisplayString()}' is a {symbol.Kind}, not a method. find_callees only operates on methods.",
                    hint: "Pass a method's symbolId, or point the cursor at a method declaration.");

            var entries = new List<CalleeEntry>();
            var seen = new HashSet<string>();

            foreach (var declRef in method.DeclaringSyntaxReferences)
            {
                var declNode = await declRef.GetSyntaxAsync(ct2);
                var tree = declNode.SyntaxTree;
                var doc = solution.GetDocument(tree);
                if (doc is null) continue;
                var sem = await doc.GetSemanticModelAsync(ct2);
                if (sem is null) continue;

                foreach (var inv in declNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var callee = sem.GetSymbolInfo(inv).Symbol;
                    if (callee is null) continue;
                    var key = callee.OriginalDefinition.ToDisplayString();
                    if (!seen.Add(key)) continue;
                    var loc = RoslynHelpers.ToLocation(inv.GetLocation());
                    if (loc is null) continue;
                    entries.Add(new CalleeEntry(RoslynHelpers.ToSymbolInfo(callee), loc));
                    if (entries.Count >= maxResults) break;
                }

                if (entries.Count >= maxResults) break;
            }

            return Contracts.ToolResult<FindCalleesResult>.Ok(new FindCalleesResult(entries));
        }, ct);
}
