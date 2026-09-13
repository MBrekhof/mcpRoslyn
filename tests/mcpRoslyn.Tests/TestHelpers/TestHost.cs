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

/// <summary>
/// A disposable host that exposes a single tool instance.
/// Allows <c>await using var host = await TestHost.CreateAsync&lt;MyTool&gt;();</c>
/// while keeping the underlying WorkspaceService lifetime tied to the host.
/// </summary>
internal sealed class ToolHost<T> : IAsyncDisposable where T : class
{
    private readonly WorkspaceService _workspace;
    private readonly ServiceProvider _services;

    internal ToolHost(T tool, WorkspaceService workspace, ServiceProvider services)
    {
        Tool = tool;
        _workspace = workspace;
        _services = services;
    }

    public T Tool { get; }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync(); // the workspace was registered as an instance, so it is not disposed here
        await _workspace.DisposeAsync();
    }
}

internal static class TestHost
{
    public static async Task<ToolHost<T>> CreateAsync<T>() where T : class
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
        var provider = services.BuildServiceProvider();
        return new ToolHost<T>(provider.GetRequiredService<T>(), workspace, provider);
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
