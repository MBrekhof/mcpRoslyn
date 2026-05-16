using Microsoft.CodeAnalysis;
using mcpRoslyn.Contracts;

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

    /// <summary>
    /// MSBuildWorkspace diagnostics raised during the most recent load/reload —
    /// typically projects that failed to evaluate (missing SDK, missing referenced csproj, etc.).
    /// Cleared at the start of each <see cref="LoadAsync"/>/<see cref="ReloadAsync"/>.
    /// </summary>
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics { get; }
}
