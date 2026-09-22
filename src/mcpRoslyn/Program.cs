using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Logging;
using mcpRoslyn.Options;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;

// MUST be first - before any Microsoft.CodeAnalysis.* type is touched.
var msBuildFailure = RegisterMSBuild();

var options = ParseArgs(args) with { MSBuildFailure = msBuildFailure };

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);

if (!string.IsNullOrWhiteSpace(options.LogFile))
    builder.Logging.AddProvider(new FileLoggerProvider(options.LogFile));

builder.Services.AddSingleton(options);

builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddHostedService<WorkspaceLoaderHostedService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    // A ToolResult carrying an Error also sets the MCP isError flag (TOOL-011).
    .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
        (request, ct) => ToolCallOutcome.TrackAsync(() => next(request, ct))));

await builder.Build().RunAsync();

// The locator resolves the SDK through the working directory's global.json, and throws when that pins an SDK that is
// not installed — before the host exists, so the process died with nothing for the client but "Connection closed"
// (WS-008). A pin that resolves is still honoured; one that doesn't falls back to the SDK seen from the exe's own
// directory, and the solution's load then reports the pin through WORKSPACE_NOT_LOADED.
// No SDK at all is returned, not thrown: the startup load reports it and the server stays up.
static Exception? RegisterMSBuild()
{
    try { MSBuildLocator.RegisterDefaults(); return null; }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"MSBuild SDK resolution from {Environment.CurrentDirectory} failed; using the SDK seen from {AppContext.BaseDirectory}. {ex.Message}");
        var instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = AppContext.BaseDirectory,
            })
            .OrderByDescending(i => i.Version)
            .FirstOrDefault();
        if (instance is null)
            return new InvalidOperationException($"No .NET SDK found for MSBuild; install one and restart the MCP server. {ex.Message}", ex);
        try { MSBuildLocator.RegisterInstance(instance); return null; }
        catch (Exception registerFailure) { return registerFailure; }
    }
}

static McpRoslynOptions ParseArgs(string[] args)
{
    string? solution = null;
    string? logFile = null;
    var logLevel = LogLevel.Information;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--solution" when i + 1 < args.Length:
                solution = args[++i];
                break;
            case "--log-level" when i + 1 < args.Length:
                if (Enum.TryParse<LogLevel>(args[++i], true, out var level))
                    logLevel = level;
                break;
            case "--log-file" when i + 1 < args.Length:
                logFile = args[++i];
                break;
        }
    }

    // Not validated here: throwing before the host starts ended the process with nothing but "Connection closed"
    // at the client. A missing or undiscoverable solution is the startup load's failure to report (WS-008).
    if (string.IsNullOrWhiteSpace(solution))
        solution = SolutionDiscovery.Discover(Environment.CurrentDirectory);

    return new McpRoslynOptions { SolutionPath = solution, LogLevel = logLevel, LogFile = logFile };
}

internal sealed class WorkspaceLoaderHostedService(IWorkspaceService ws) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => ws.LoadAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
