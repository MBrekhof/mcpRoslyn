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
    /// Background pre-compilation and index build of the current workspace generation, kicked off
    /// by the most recent successful <see cref="LoadAsync"/>/<see cref="ReloadAsync"/>. Cancelled
    /// when a reload retires the generation. Tools don't await it directly — they go through
    /// <see cref="GetIndexedSolutionAsync"/>.
    /// </summary>
    Task WarmupTask { get; }

    /// <summary>
    /// MSBuildWorkspace diagnostics raised during the most recent load/reload —
    /// typically projects that failed to evaluate (missing SDK, missing referenced csproj, etc.).
    /// Cleared at the start of each <see cref="LoadAsync"/>/<see cref="ReloadAsync"/>.
    /// </summary>
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics { get; }

    /// <summary>
    /// The current generation's symbol index, which may still be building. Tools use
    /// <see cref="GetIndexedSolutionAsync"/>; this is for tests that have already awaited <see cref="WarmupTask"/>.
    /// Throws InvalidOperationException if accessed before LoadAsync completes.
    /// </summary>
    SymbolIndex SymbolIndex { get; }

    /// <summary>
    /// The current generation's invocation index (routes, middleware, hosted services, DI registrations),
    /// which may still be building. Tools use <see cref="GetIndexedSolutionAsync"/>.
    /// Throws InvalidOperationException if accessed before LoadAsync completes.
    /// </summary>
    InvocationIndex InvocationIndex { get; }

    /// <summary>
    /// Waits for the current generation's index build, then returns its freshly refreshed solution
    /// together with its indexes — the entry point for every indexed tool (WS-006, IDX-002).
    /// </summary>
    Task<IndexedSolution> GetIndexedSolutionAsync(CancellationToken ct = default);
}
