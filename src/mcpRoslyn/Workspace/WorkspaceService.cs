using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using mcpRoslyn.Contracts;
using mcpRoslyn.Options;

namespace mcpRoslyn.Workspace;

public sealed class WorkspaceService(McpRoslynOptions options, ILogger<WorkspaceService> log)
    : IWorkspaceService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<DocumentId, DateTime> _mtimeCache = new();
    private Task _warmupTask = Task.CompletedTask;
    private readonly List<WorkspaceLoadDiagnostic> _diagnostics = new();
    private readonly object _diagnosticsLock = new();
    private SymbolIndex? _symbolIndex;
    private InvocationIndex? _invocationIndex;

    public int LoadedProjectCount => _solution?.Projects.Count() ?? 0;
    public Task WarmupTask => _warmupTask;

    public IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics
    {
        get { lock (_diagnosticsLock) return _diagnostics.ToArray(); }
    }

    public SymbolIndex SymbolIndex
        => _symbolIndex ?? throw new InvalidOperationException("Workspace not loaded.");

    public InvocationIndex InvocationIndex
        => _invocationIndex ?? throw new InvalidOperationException("Workspace not loaded.");

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

            foreach (var doc in _solution.Projects.SelectMany(p => p.Documents).ToList())
            {
                if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
                var diskMtime = File.GetLastWriteTimeUtc(doc.FilePath);
                if (_mtimeCache.TryGetValue(doc.Id, out var cachedMtime) && cachedMtime >= diskMtime)
                    continue;

                var text = await File.ReadAllTextAsync(doc.FilePath, ct);
                _solution = _solution.WithDocumentText(
                    doc.Id,
                    Microsoft.CodeAnalysis.Text.SourceText.From(text));
                _mtimeCache[doc.Id] = diskMtime;
                _symbolIndex?.MarkDirty(doc.Id);
                _invocationIndex?.MarkDirty(doc.Id);
            }

            // Update InvocationIndex's solution snapshot so dirty re-walks
            // see the freshly-loaded document text.
            _invocationIndex?.UpdateSolution(_solution);

            return _solution;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadUnsafeAsync(CancellationToken ct)
    {
        lock (_diagnosticsLock) _diagnostics.Clear();
        _symbolIndex = new SymbolIndex();
        _invocationIndex = new InvocationIndex();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _workspace = MSBuildWorkspace.Create();
        _workspace.WorkspaceFailed += (_, e) =>
        {
            var diag = new WorkspaceLoadDiagnostic(e.Diagnostic.Kind.ToString(), e.Diagnostic.Message);
            lock (_diagnosticsLock) _diagnostics.Add(diag);
            log.LogWarning("MSBuild workspace event: {Kind} {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);
        };

        _solution = await _workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: ct);
        log.LogInformation("Loaded {ProjectCount} projects in {Elapsed} ms from {Path}",
            _solution.Projects.Count(), sw.ElapsedMilliseconds, options.SolutionPath);

        // seed mtime cache
        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            _mtimeCache[doc.Id] = File.GetLastWriteTimeUtc(doc.FilePath);
        }

        // kick background pre-compilation; do NOT await
        var solutionSnapshot = _solution;
        _warmupTask = Task.Run(() => WarmupAsync(solutionSnapshot, ct), ct);
    }

    private async Task WarmupAsync(Solution solution, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = solution.Projects.Select(async project =>
        {
            var projectSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var compilation = await project.GetCompilationAsync(ct);
                log.LogInformation(
                    "Warmed {Project} in {Elapsed} ms ({DiagCount} diagnostics)",
                    project.Name,
                    projectSw.ElapsedMilliseconds,
                    compilation?.GetDiagnostics(ct).Length ?? 0);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Warm-up failed for project {Project}", project.Name);
            }
        });
        await Task.WhenAll(tasks);
        log.LogInformation(
            "Warm-up complete: {ProjectCount} projects in {Elapsed} ms",
            solution.Projects.Count(),
            sw.ElapsedMilliseconds);

        // Index build runs after compilations are cached so per-project walks
        // reuse warmed state. Failures isolated; one bad project doesn't
        // poison the whole index.
        if (_symbolIndex is not null)
        {
            var indexSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _symbolIndex.BuildAsync(solution, ct);
                log.LogInformation(
                    "Symbol index built in {Elapsed} ms",
                    indexSw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Symbol index build failed; semantic_search will fall back to live walks");
            }
        }

        if (_invocationIndex is not null)
        {
            var invIndexSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _invocationIndex.BuildAsync(solution, ct);
                log.LogInformation(
                    "Invocation index built in {Elapsed} ms",
                    invIndexSw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Invocation index build failed; find_entrypoints/find_registrations will be unavailable");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _workspace?.Dispose(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
