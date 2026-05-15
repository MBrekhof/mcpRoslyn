using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tests.TestHelpers;

internal static class TestHost
{
    public static async Task<T> CreateAsync<T>() where T : class
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var workspace = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await workspace.LoadAsync();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IWorkspaceService>(workspace);
        services.AddSingleton<T>();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<T>();
    }
}
