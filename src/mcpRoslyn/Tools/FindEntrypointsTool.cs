using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record EntrypointRoute(string Verb, string? Template, string? Handler, Contracts.SymbolLocation Location);
public sealed record EntrypointMiddleware(string Method, Contracts.SymbolLocation Location);
public sealed record EntrypointHostedService(
    string Kind, string? ServiceType, string? Type, string? BaseType, Contracts.SymbolLocation Location);

public sealed record FindEntrypointsResult(
    IReadOnlyList<EntrypointRoute> Routes,
    IReadOnlyList<EntrypointMiddleware> Middleware,
    IReadOnlyList<EntrypointHostedService> HostedServices,
    IReadOnlyList<string> Truncated);

[McpServerToolType]
internal sealed class FindEntrypointsTool(IWorkspaceService ws, ILogger<FindEntrypointsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "find_entrypoints")]
    [Description("Returns ASP.NET endpoints: routes (MapGet/MapPost/...), middleware (Use* on IApplicationBuilder), and hosted services (AddHostedService<T> + BackgroundService subclasses).")]
    public Task<Contracts.ToolResult<FindEntrypointsResult>> InvokeAsync(
        bool includeAspNetRoutes = true,
        bool includeHostedServices = true,
        bool includeMiddlewarePipeline = true,
        int maxResultsPerSection = 20,
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            // Ensure dirty-doc walk runs before snapshotting the index.
            await Workspace.GetFreshSolutionAsync(ct2);
            var index = Workspace.InvocationIndex;
            var truncated = new List<string>();

            EntrypointRoute[] routes;
            if (includeAspNetRoutes)
            {
                var src = index.QueryRoutes();
                if (src.Count > maxResultsPerSection) truncated.Add("routes");
                routes = src.Take(maxResultsPerSection)
                    .Select(r => new EntrypointRoute(r.Verb, r.Template, r.Handler, r.Location))
                    .ToArray();
            }
            else routes = Array.Empty<EntrypointRoute>();

            EntrypointMiddleware[] middleware;
            if (includeMiddlewarePipeline)
            {
                var src = index.QueryMiddleware();
                if (src.Count > maxResultsPerSection) truncated.Add("middleware");
                middleware = src.Take(maxResultsPerSection)
                    .Select(m => new EntrypointMiddleware(m.Method, m.Location))
                    .ToArray();
            }
            else middleware = Array.Empty<EntrypointMiddleware>();

            EntrypointHostedService[] hosted;
            if (includeHostedServices)
            {
                var src = index.QueryHostedServices();
                // Deduplicate: if a type is registered via AddHostedService<T>, skip the
                // redundant "subclass" entry for the same type so each service appears once.
                var registeredTypes = new HashSet<string>(
                    src.Where(h => h.Kind == "registered" && h.ServiceType is not null)
                       .Select(h => h.ServiceType!),
                    StringComparer.Ordinal);
                var deduplicated = src
                    .Where(h => !(h.Kind == "subclass" && h.Type is not null && registeredTypes.Contains(h.Type)))
                    .ToList();
                if (deduplicated.Count > maxResultsPerSection) truncated.Add("hostedServices");
                hosted = deduplicated.Take(maxResultsPerSection)
                    .Select(h => new EntrypointHostedService(h.Kind, h.ServiceType, h.Type, h.BaseType, h.Location))
                    .ToArray();
            }
            else hosted = Array.Empty<EntrypointHostedService>();

            return Contracts.ToolResult<FindEntrypointsResult>.Ok(new FindEntrypointsResult(
                routes, middleware, hosted, truncated));
        }, ct);
}
