using Microsoft.CodeAnalysis;

namespace mcpRoslyn.Workspace;

public interface IWorkspaceService
{
    Task LoadAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default);
    int LoadedProjectCount { get; }

    /// <summary>
    /// Background pre-compilation task kicked off by the most recent <see cref="LoadAsync"/>
    /// or <see cref="ReloadAsync"/>. Production tool code does not await this; it exists for
    /// tests and for future hybrid-reload modes that want to block until projects are hot.
    /// </summary>
    Task WarmupTask { get; }
}
