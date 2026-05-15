using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using mcpRoslyn.Options;

namespace mcpRoslyn.Workspace;

public sealed class WorkspaceService(McpRoslynOptions options, ILogger<WorkspaceService> log)
    : IWorkspaceService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<DocumentId, DateTime> _mtimeCache = new();

    public int LoadedProjectCount => _solution?.Projects.Count() ?? 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await LoadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _workspace?.CloseSolution();
            _mtimeCache.Clear();
            await LoadUnsafeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_solution is null) throw new InvalidOperationException("Workspace not loaded.");
            // mtime refresh added in Task 7
            return _solution;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadUnsafeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _workspace = MSBuildWorkspace.Create();
        _workspace.WorkspaceFailed += (_, e) =>
            log.LogWarning("MSBuild workspace event: {Kind} {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);

        _solution = await _workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: ct);
        log.LogInformation("Loaded {ProjectCount} projects in {Elapsed} ms from {Path}",
            _solution.Projects.Count(), sw.ElapsedMilliseconds, options.SolutionPath);

        // seed mtime cache
        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            _mtimeCache[doc.Id] = File.GetLastWriteTimeUtc(doc.FilePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _workspace?.Dispose(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
