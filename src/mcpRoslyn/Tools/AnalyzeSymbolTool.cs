using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record AnalyzeSymbolSection<T>(int Count, IReadOnlyList<T> Items);

public sealed record AnalyzeSymbolResult(
    Contracts.SymbolInfo Symbol,
    HoverResult? Hover,
    AnalyzeSymbolSection<Contracts.SymbolLocation>? References,
    AnalyzeSymbolSection<Contracts.SymbolInfo>? Implementations,
    AnalyzeSymbolSection<Contracts.SymbolInfo>? DerivedTypes,
    AnalyzeSymbolSection<CallerEntry>? Callers,
    IReadOnlyList<string> Truncated);

[McpServerToolType]
internal sealed class AnalyzeSymbolTool(IWorkspaceService ws, ILogger<AnalyzeSymbolTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "analyze_symbol")]
    [Description("Composite analysis of one symbol — hover + references + implementations + derived types + callers " +
                 "in a single call. Sections skipped via include flags return null.")]
    public Task<Contracts.ToolResult<AnalyzeSymbolResult>> InvokeAsync(
        string? symbolId = null,
        string? filePath = null, int? line = null, int? column = null,
        bool includeHover = true,
        bool includeReferences = true,
        bool includeImplementations = true,
        bool includeDerivedTypes = true,
        bool includeCallers = true,
        int maxPerSection = 20,
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            ISymbol? symbol = null;

            if (!string.IsNullOrWhiteSpace(symbolId))
            {
                symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, symbolId, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<AnalyzeSymbolResult>.Fail(
                        "SYMBOL_NOT_FOUND", $"Symbol ID '{symbolId}' did not resolve.");
            }
            else if (filePath is not null && line is int l && column is int c)
            {
                var doc = RoslynHelpers.FindDocument(solution, filePath);
                if (doc is null)
                    return Contracts.ToolResult<AnalyzeSymbolResult>.Fail(
                        "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<AnalyzeSymbolResult>.Fail(
                        "SYMBOL_NOT_FOUND", $"No symbol at {filePath}:{l}:{c}.");
            }
            else
            {
                return Contracts.ToolResult<AnalyzeSymbolResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            var truncated = new List<string>();

            // Run section queries in parallel
            var hoverTask = includeHover
                ? Task.FromResult<HoverResult?>(BuildHover(symbol, ct2))
                : Task.FromResult<HoverResult?>(null);

            var refsTask = includeReferences
                ? GetReferences(symbol, solution, maxPerSection, truncated, ct2)
                : Task.FromResult<AnalyzeSymbolSection<Contracts.SymbolLocation>?>(null);

            var implTask = includeImplementations
                ? GetImplementations(symbol, solution, maxPerSection, truncated, ct2)
                : Task.FromResult<AnalyzeSymbolSection<Contracts.SymbolInfo>?>(null);

            var derivTask = includeDerivedTypes
                ? GetDerivedTypes(symbol, solution, maxPerSection, truncated, ct2)
                : Task.FromResult<AnalyzeSymbolSection<Contracts.SymbolInfo>?>(null);

            var callTask = includeCallers
                ? GetCallers(symbol, solution, maxPerSection, truncated, ct2)
                : Task.FromResult<AnalyzeSymbolSection<CallerEntry>?>(null);

            await Task.WhenAll(hoverTask, refsTask, implTask, derivTask, callTask);

            return Contracts.ToolResult<AnalyzeSymbolResult>.Ok(new AnalyzeSymbolResult(
                Symbol:          RoslynHelpers.ToSymbolInfo(symbol),
                Hover:           await hoverTask,
                References:      await refsTask,
                Implementations: await implTask,
                DerivedTypes:    await derivTask,
                Callers:         await callTask,
                Truncated:       truncated));
        }, ct);

    // ── per-section helpers ──────────────────────────────────────────────────

    private static HoverResult? BuildHover(ISymbol symbol, CancellationToken ct)
    {
        var symbolInfo = RoslynHelpers.ToSymbolInfo(symbol);
        var signature  = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var xmlDoc     = ExtractSummary(symbol.GetDocumentationCommentXml(cancellationToken: ct));
        return new HoverResult(symbolInfo, xmlDoc, signature);
    }

    private static string? ExtractSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc     = System.Xml.Linq.XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            return summary?.Value.Trim();
        }
        catch { return null; }
    }

    private static async Task<AnalyzeSymbolSection<Contracts.SymbolLocation>?> GetReferences(
        ISymbol s, Solution sol, int max, List<string> trunc, CancellationToken ct)
    {
        var refs  = await SymbolFinder.FindReferencesAsync(s, sol, ct);
        var items = refs
            .SelectMany(r => r.Locations)
            .Select(l => RoslynHelpers.ToLocation(l.Location))
            .Where(l => l is not null)
            .Cast<Contracts.SymbolLocation>()
            .ToList();

        var total = items.Count;
        if (total > max) { trunc.Add("references"); items = items.Take(max).ToList(); }
        return new AnalyzeSymbolSection<Contracts.SymbolLocation>(total, items);
    }

    private static async Task<AnalyzeSymbolSection<Contracts.SymbolInfo>?> GetImplementations(
        ISymbol s, Solution sol, int max, List<string> trunc, CancellationToken ct)
    {
        // Only types have implementations
        if (s is not INamedTypeSymbol) return null;

        var impls = await SymbolFinder.FindImplementationsAsync(s, sol, cancellationToken: ct);
        var items = impls.Select(RoslynHelpers.ToSymbolInfo).ToList();

        var total = items.Count;
        if (total > max) { trunc.Add("implementations"); items = items.Take(max).ToList(); }
        return new AnalyzeSymbolSection<Contracts.SymbolInfo>(total, items);
    }

    private static async Task<AnalyzeSymbolSection<Contracts.SymbolInfo>?> GetDerivedTypes(
        ISymbol s, Solution sol, int max, List<string> trunc, CancellationToken ct)
    {
        // Only named types have derived types; for methods/properties return null
        if (s is not INamedTypeSymbol type) return null;

        IEnumerable<INamedTypeSymbol> derived = type.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindDerivedInterfacesAsync(type, sol, transitive: false, projects: null, ct)
            : await SymbolFinder.FindDerivedClassesAsync(type, sol, transitive: false, projects: null, ct);

        var items = derived.Select(d => RoslynHelpers.ToSymbolInfo(d)).ToList();
        var total = items.Count;
        if (total > max) { trunc.Add("derivedTypes"); items = items.Take(max).ToList(); }
        return new AnalyzeSymbolSection<Contracts.SymbolInfo>(total, items);
    }

    private static async Task<AnalyzeSymbolSection<CallerEntry>?> GetCallers(
        ISymbol s, Solution sol, int max, List<string> trunc, CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(s, sol, ct);
        var items = callers
            .SelectMany(c => c.Locations.Select(loc => new
            {
                CallingSymbol = c.CallingSymbol,
                Location      = RoslynHelpers.ToLocation(loc)
            }))
            .Where(x => x.Location is not null)
            .Select(x => new CallerEntry(RoslynHelpers.ToSymbolInfo(x.CallingSymbol), x.Location!))
            .ToList();

        var total = items.Count;
        if (total > max) { trunc.Add("callers"); items = items.Take(max).ToList(); }
        return new AnalyzeSymbolSection<CallerEntry>(total, items);
    }
}
