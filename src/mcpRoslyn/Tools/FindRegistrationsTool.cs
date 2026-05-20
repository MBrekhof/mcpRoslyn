using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record DiConsumer(string Type, Contracts.SymbolLocation? CtorLocation);

public sealed record RegistrationEntry(
    string? ServiceType,
    string? ImplType,
    string? Lifetime,
    string RawCall,
    Contracts.SymbolLocation Location,
    IReadOnlyList<DiConsumer> LikelyConsumers);

public sealed record FindRegistrationsResult(
    IReadOnlyList<RegistrationEntry> Registrations,
    IReadOnlyList<RegistrationEntry> Unclassified,
    IReadOnlyList<string> Truncated);

[McpServerToolType]
internal sealed class FindRegistrationsTool(IWorkspaceService ws, ILogger<FindRegistrationsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_registrations")]
    [Description("Returns IServiceCollection DI registrations (AddSingleton/AddTransient/AddScoped) with service/impl/lifetime and likely constructor consumers.")]
    public Task<Contracts.ToolResult<FindRegistrationsResult>> InvokeAsync(
        string? query = null,
        bool includeConsumers = true,
        int maxResults = 20,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var di = Workspace.InvocationIndex.QueryDi();
            var truncated = new List<string>();

            IReadOnlyList<DiEntry> classified = string.IsNullOrWhiteSpace(query)
                ? di.Registrations
                : di.Registrations.Where(r => MatchesQuery(r, query)).ToArray();
            IReadOnlyList<DiEntry> unclassified = string.IsNullOrWhiteSpace(query)
                ? di.Unclassified
                : di.Unclassified.Where(r => r.RawCall.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (classified.Count > maxResults) truncated.Add("registrations");
            if (unclassified.Count > maxResults) truncated.Add("unclassified");

            var regs = classified.Take(maxResults).Select(e => new RegistrationEntry(
                ServiceType: e.ServiceType,
                ImplType:    e.ImplType,
                Lifetime:    e.Lifetime,
                RawCall:     e.RawCall,
                Location:    e.Location,
                LikelyConsumers: includeConsumers && e.ServiceType is not null
                    ? FindConsumers(e.ServiceType, solution).ToArray()
                    : Array.Empty<DiConsumer>())).ToArray();

            var unc = unclassified.Take(maxResults).Select(e => new RegistrationEntry(
                ServiceType: null,
                ImplType:    null,
                Lifetime:    null,
                RawCall:     e.RawCall,
                Location:    e.Location,
                LikelyConsumers: Array.Empty<DiConsumer>())).ToArray();

            var result = new FindRegistrationsResult(regs, unc, truncated);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var lifetimes = result.Registrations
                    .Where(r => r.Lifetime is not null)
                    .GroupBy(r => r.Lifetime!)
                    .Select(g => $"{g.Count()} {g.Key}")
                    .ToList();
                var lifetimeBreakdown = lifetimes.Count > 0 ? string.Join(", ", lifetimes) : "none";
                return Contracts.ToolResult<FindRegistrationsResult>.OkSummary(
                    $"{result.Registrations.Count} DI registrations ({lifetimeBreakdown})");
            }
            return Contracts.ToolResult<FindRegistrationsResult>.Ok(result);
        }, ct);

    private IEnumerable<DiConsumer> FindConsumers(string serviceType, Microsoft.CodeAnalysis.Solution solution)
    {
        // SymbolIndex.QueryParameterType returns SymbolInfo for methods whose parameter type matches.
        // We're targeting constructors specifically; SymbolInfo includes Kind and Signature.
        var matches = Workspace.SymbolIndex.QueryParameterType(serviceType, solution);

        // Also try the simple-name form (e.g., "IFoo") since SymbolIndex indexes both forms.
        var simple = serviceType.Contains('.') ? serviceType[(serviceType.LastIndexOf('.') + 1)..] : serviceType;
        if (simple != serviceType)
            matches = matches.Concat(Workspace.SymbolIndex.QueryParameterType(simple, solution)).ToArray();

        var seen = new HashSet<string>();
        foreach (var m in matches)
        {
            // Prefer constructors. SymbolInfo.Kind comes from ISymbol.Kind.ToString(); a constructor is Kind == "Method"
            // with ContainingType-named method (".ctor"). We expose any method whose ContainingType is the consumer.
            if (m.ContainingType is null || !seen.Add(m.ContainingType)) continue;
            yield return new DiConsumer(m.ContainingType, m.PrimaryLocation);
        }
    }

    private static bool MatchesQuery(DiEntry e, string query)
    {
        return (e.ServiceType?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.ImplType?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || e.RawCall.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
