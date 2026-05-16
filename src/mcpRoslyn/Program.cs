using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Logging;
using mcpRoslyn.Options;
using mcpRoslyn.Workspace;

// MUST be first - before any Microsoft.CodeAnalysis.* type is touched.
MSBuildLocator.RegisterDefaults();

var options = ParseArgs(args);

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
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

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

    if (string.IsNullOrWhiteSpace(solution))
        solution = DiscoverSolution();
    else if (!File.Exists(solution))
        throw new FileNotFoundException($"Solution file not found: {solution}");

    return new McpRoslynOptions { SolutionPath = solution, LogLevel = logLevel, LogFile = logFile };
}

static string DiscoverSolution()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        var solutions = dir.GetFiles("*.sln")
            .Concat(dir.GetFiles("*.slnx"))
            .OrderBy(f => f.Extension, StringComparer.OrdinalIgnoreCase) // .sln before .slnx
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (solutions.Count > 0) return solutions[0].FullName;
        dir = dir.Parent;
    }
    throw new FileNotFoundException(
        $"No --solution provided and no .sln or .slnx found by walking up from {Environment.CurrentDirectory}");
}

internal sealed class WorkspaceLoaderHostedService(IWorkspaceService ws) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => ws.LoadAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
