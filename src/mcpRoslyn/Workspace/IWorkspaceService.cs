using Microsoft.CodeAnalysis;
using mcpRoslyn.Contracts;

namespace mcpRoslyn.Workspace;

public interface IWorkspaceService
{
    Task LoadAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default);

    /// <summary>
    /// The refreshed solution together with the load diagnostics of the same workspace generation, for a
    /// tool that reports on both: read separately, a reload landing in between pairs one generation's
    /// solution with another's load failures (DIAG-002). It does not wait for index warm-up — a tool that
    /// needs indexes uses <see cref="GetIndexedSolutionAsync"/>.
    /// </summary>
    Task<LoadedSolution> GetFreshSolutionWithDiagnosticsAsync(CancellationToken ct = default);
    int LoadedProjectCount { get; }

    /// <summary>
    /// Background pre-compilation and index build of the current workspace generation, kicked off
    /// by the most recent successful <see cref="LoadAsync"/>/<see cref="ReloadAsync"/>. Cancelled
    /// when a reload retires the generation. Tools don't await it directly — they go through
    /// <see cref="GetIndexedSolutionAsync"/>.
    /// </summary>
    Task WarmupTask { get; }

    /// <summary>
    /// MSBuildWorkspace diagnostics raised while loading the current workspace generation — typically
    /// projects that failed to evaluate (missing SDK, missing referenced csproj, etc.). A successful
    /// reload replaces them; a failed reload leaves the previous generation's diagnostics in place.
    /// A tool reporting on a solution and its load failures together reads both through
    /// <see cref="GetFreshSolutionWithDiagnosticsAsync"/> instead of this property.
    /// </summary>
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Why the loaded workspace may no longer match the disk — a solution, project or Directory.* build file
    /// changed, or a .cs file was added or deleted — empty when it still matches. Every tool result carries
    /// these as a warning; only <see cref="ReloadAsync"/> clears them (WS-007).
    /// </summary>
    IReadOnlyList<string> StaleReasons { get; }

    /// <summary>How many workspace generations have been published; a change mid-call means a reload landed.</summary>
    int LoadCount { get; }

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

/// <summary>A refreshed solution and the load diagnostics of the generation it came from.</summary>
public sealed record LoadedSolution(Solution Solution, IReadOnlyList<WorkspaceLoadDiagnostic> LoadDiagnostics);
