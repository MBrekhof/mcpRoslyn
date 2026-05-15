using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Options;
using mcpRoslyn.Workspace;

// MUST be first - before any Microsoft.CodeAnalysis.* type is touched.
MSBuildLocator.RegisterDefaults();

var options = ParseArgs(args);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);

builder.Services.AddSingleton(options);

builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddHostedService<WorkspaceLoaderHostedService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static McpRoslynOptions ParseArgs(string[] args)
{
    string? solution = null;
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
        }
    }

    if (string.IsNullOrWhiteSpace(solution))
        throw new ArgumentException("--solution <path> is required");
    if (!File.Exists(solution))
        throw new FileNotFoundException($"Solution file not found: {solution}");

    return new McpRoslynOptions { SolutionPath = solution, LogLevel = logLevel };
}

internal sealed class WorkspaceLoaderHostedService(IWorkspaceService ws) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => ws.LoadAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
