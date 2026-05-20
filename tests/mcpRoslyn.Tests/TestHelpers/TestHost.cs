using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tests.TestHelpers;

/// <summary>
/// A disposable host that exposes the underlying WorkspaceService for index tests.
/// </summary>
internal sealed class TestWorkspaceHost : IAsyncDisposable
{
    private readonly WorkspaceService _workspaceService;

    internal TestWorkspaceHost(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public IWorkspaceService Workspace => _workspaceService;

    public ValueTask DisposeAsync() => _workspaceService.DisposeAsync();
}

internal static class TestHost
{
    public static async Task<T> CreateAsync<T>() where T : class
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var workspace = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await workspace.LoadAsync();
        // Wait for warm-up (incl. SymbolIndex build) so tools that depend on
        // the index have something to query. Without this, semantic_search
        // patterns backed by the index race the background build and return
        // empty buckets non-deterministically.
        await workspace.WarmupTask;

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IWorkspaceService>(workspace);
        services.AddSingleton<T>();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<T>();
    }

    /// <summary>
    /// Creates a workspace host that exposes <see cref="IWorkspaceService"/> directly.
    /// Useful for index tests that don't need a specific tool.
    /// </summary>
    public static async Task<TestWorkspaceHost> CreateWorkspaceAsync()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var workspace = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await workspace.LoadAsync();
        await workspace.WarmupTask;

        return new TestWorkspaceHost(workspace);
    }
}
