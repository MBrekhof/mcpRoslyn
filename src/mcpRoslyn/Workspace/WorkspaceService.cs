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
    private Generation? _current;
    private Solution? _solution;
    private Dictionary<DocumentId, DateTime> _mtimeCache = new();
    private readonly List<Generation> _retiring = new();       // lock on itself
    private readonly CancellationTokenSource _disposeCts = new(); // ends retirement grace periods early

    /// <summary>Test seam: awaited in each generation's warm-up after compilation, before the index builds.</summary>
    internal Func<Task>? BeforeIndexBuild { get; set; }

    /// <summary>
    /// ponytail: a query still running on a retired generation's solution when its workspace is
    /// disposed may fail. Reader tracking would close that; a grace period covers every query we
    /// have measured (the slowest, find_dead_code_candidates on BPG, is ~0.5 s).
    /// </summary>
    private static readonly TimeSpan RetiredWorkspaceGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Everything one load produced. Published as a unit only after the solution opened, so a
    /// failed reload leaves the previous generation serving, and a warm-up can only ever build
    /// into its own generation's indexes (WS-006).
    /// </summary>
    private sealed class Generation(MSBuildWorkspace workspace)
    {
        public MSBuildWorkspace Workspace { get; } = workspace;
        public SymbolIndex SymbolIndex { get; } = new();
        public InvocationIndex InvocationIndex { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public Task Warmup { get; set; } = Task.CompletedTask;
        public Exception? SymbolIndexFailure { get; set; }
        public Exception? InvocationIndexFailure { get; set; }
        /// <summary>This load's MSBuild diagnostics; lock on the list itself.</summary>
        public List<WorkspaceLoadDiagnostic> Diagnostics { get; } = new();
    }

    public int LoadedProjectCount => _solution?.Projects.Count() ?? 0;
    public Task WarmupTask => _current?.Warmup ?? Task.CompletedTask;

    /// <summary>
    /// The current generation's load diagnostics. They belong to the generation, so a failed reload —
    /// which leaves the previous generation serving — also leaves its diagnostics (DIAG-002). A tool
    /// reporting on a solution and its load failures together reads both through
    /// <see cref="GetFreshSolutionWithDiagnosticsAsync"/>, not this property.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics
    {
        get
        {
            if (_current is not { } gen) return [];
            lock (gen.Diagnostics) return gen.Diagnostics.ToArray();
        }
    }

    public SymbolIndex SymbolIndex
        => _current?.SymbolIndex ?? throw new InvalidOperationException("Workspace not loaded.");

    public InvocationIndex InvocationIndex
        => _current?.InvocationIndex ?? throw new InvalidOperationException("Workspace not loaded.");

    public async Task<IndexedSolution> GetIndexedSolutionAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var gen = _current ?? throw new InvalidOperationException("Workspace not loaded.");
            try
            {
                await gen.Warmup.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !ReferenceEquals(gen, _current))
            {
                continue; // a reload retired this generation mid-wait; wait for its successor
            }

            await _gate.WaitAsync(ct);
            try
            {
                // Publication happens under the gate, so this check is exact: a successor published
                // while we waited — whether this warm-up finished or failed — is picked up here.
                if (!ReferenceEquals(gen, _current)) continue;
                var solution = await RefreshUnsafeAsync(ct);
                return new IndexedSolution(
                    solution, gen.SymbolIndex, gen.SymbolIndexFailure, gen.InvocationIndex, gen.InvocationIndexFailure);
            }
            finally { _gate.Release(); }
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await LoadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public Task ReloadAsync(CancellationToken ct = default) => LoadAsync(ct);

    public async Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await RefreshUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<LoadedSolution> GetFreshSolutionWithDiagnosticsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Publication happens under the same gate, so the solution and diagnostics are one generation's.
            var solution = await RefreshUnsafeAsync(ct);
            var gen = _current!;
            lock (gen.Diagnostics) return new LoadedSolution(solution, gen.Diagnostics.ToArray());
        }
        finally { _gate.Release(); }
    }

    private async Task<Solution> RefreshUnsafeAsync(CancellationToken ct)
    {
        if (_solution is null || _current is null) throw new InvalidOperationException("Workspace not loaded.");

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
            _current.SymbolIndex.MarkDirty(doc.Id);
            _current.InvocationIndex.MarkDirty(doc.Id);
        }

        // Update InvocationIndex's solution snapshot so dirty re-walks
        // see the freshly-loaded document text.
        _current.InvocationIndex.UpdateSolution(_solution);

        return _solution;
    }

    /// <summary>
    /// MSBuild quotes the offending project's full path in its message, in both wordings we see:
    /// "Cannot open project '…\bpg-frontend.esproj' because…" and
    /// "Msbuild failed when processing the file '…\duetGPT.csproj' with message: …".
    /// Pull the project name out so callers can filter/group without parsing prose (WS-003).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProjectPathInMessage =
        new(@"'([^']+\.[A-Za-z]*proj)'", System.Text.RegularExpressions.RegexOptions.Compiled
                                       | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string? ExtractProjectName(string message)
    {
        var match = ProjectPathInMessage.Match(message);
        if (!match.Success) return null;
        var name = Path.GetFileNameWithoutExtension(match.Groups[1].Value);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// MSBuild's wording when a solution names a project whose extension has no Roslyn language:
    /// "Cannot open project '…' because the file extension '.esproj' is not associated with a language."
    /// </summary>
    internal static bool IsUnsupportedProjectLanguage(string message)
        => message.Contains("is not associated with a language", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MSBuild reports package-pruning suggestions and vulnerability advisories through the same
    /// channel as real load failures, at kind <c>Failure</c> and worded "Msbuild failed when
    /// processing…", for projects that load perfectly well. A diagnostic naming a project that IS
    /// in the loaded solution demonstrably did not stop it loading, so it is re-kinded; one naming
    /// an absent project stays a `Failure`, which is the honest signal for a declared-but-unloaded
    /// project (WS-004).
    /// </summary>
    internal static WorkspaceLoadDiagnostic Reclassify(
        WorkspaceLoadDiagnostic diag, ISet<string> loadedProjectFileNames,
        ISet<string>? loadedProjectPaths = null, string? baseDirectory = null)
    {
        if (diag.Kind != "Failure" || diag.ProjectName is null) return diag;
        // Compare full paths when the message quotes one: two projects in different folders can share a
        // file name, and a failure of B/Foo.csproj must not read as loaded because A/Foo.csproj did
        // (DIAG-002). A relative quoted path resolves against the solution's directory; the file name is
        // the fallback only when there is no path to resolve.
        var quoted = ProjectPathInMessage.Match(diag.Message) is { Success: true } match ? match.Groups[1].Value : null;
        var fullQuoted = quoted is null ? null
            : Path.IsPathRooted(quoted) ? Path.GetFullPath(quoted)
            : baseDirectory is not null ? Path.GetFullPath(Path.Combine(baseDirectory, quoted))
            : null;
        var loaded = loadedProjectPaths is not null && fullQuoted is not null
            ? loadedProjectPaths.Contains(fullQuoted)
            : loadedProjectFileNames.Contains(diag.ProjectName);
        return loaded ? diag with { Kind = "ProjectLoadedWithWarnings" } : diag;
    }

    private void ReclassifyLoadedProjectDiagnostics(Generation gen, Solution solution)
    {
        // Compare on the .csproj path (or file name), not Project.Name — a multi-targeted project is
        // named "Foo(net8.0)" while the message quotes the path to Foo.csproj.
        var projectFiles = solution.Projects.Select(p => p.FilePath).OfType<string>().ToList();
        var loadedNames = projectFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedPaths = projectFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(options.SolutionPath));

        lock (gen.Diagnostics)
        {
            for (var i = 0; i < gen.Diagnostics.Count; i++)
                gen.Diagnostics[i] = Reclassify(gen.Diagnostics[i], loadedNames, loadedPaths, solutionDirectory);
        }
    }

    private async Task LoadUnsafeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gen = new Generation(MSBuildWorkspace.Create());
        // RegisterWorkspaceFailedHandler, not the WorkspaceFailed event: the event is obsolete in
        // Roslyn 5.3 (CS0618 on every build) and its replacement no longer forces the UI thread.
        gen.Workspace.RegisterWorkspaceFailedHandler(e =>
        {
            // A polyglot solution (.esproj, .njsproj, .sqlproj…) always raises a Failure here.
            // That is expected, not broken, so it gets its own kind instead of reading as a real error.
            var kind = IsUnsupportedProjectLanguage(e.Diagnostic.Message)
                ? "SkippedUnsupportedProject"
                : e.Diagnostic.Kind.ToString();
            var diag = new WorkspaceLoadDiagnostic(kind, e.Diagnostic.Message, ExtractProjectName(e.Diagnostic.Message));
            lock (gen.Diagnostics) gen.Diagnostics.Add(diag);
            log.LogWarning("MSBuild workspace event: {Kind} {Message}", kind, e.Diagnostic.Message);
        });

        Solution solution;
        try
        {
            solution = await gen.Workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: ct);
        }
        catch
        {
            // Nothing was published: the previous generation, if any, keeps serving (WS-006).
            gen.Workspace.Dispose();
            gen.Cts.Dispose();
            throw;
        }
        log.LogInformation("Loaded {ProjectCount} projects in {Elapsed} ms from {Path}",
            solution.Projects.Count(), sw.ElapsedMilliseconds, options.SolutionPath);

        // Has to happen here, not in the handler: diagnostics arrive during the load, before there
        // is a project list to check them against (WS-004).
        ReclassifyLoadedProjectDiagnostics(gen, solution);

        var mtimes = new Dictionary<DocumentId, DateTime>();
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            mtimes[doc.Id] = File.GetLastWriteTimeUtc(doc.FilePath);
        }

        // Start warm-up before publishing, so a reader never sees the generation with a completed
        // placeholder task. Its token is the generation's, not the caller's: warm-up outlives the
        // request that loaded it, and ends when the generation is retired or disposed.
        gen.Warmup = Task.Run(() => WarmupAsync(solution, gen));

        var retired = _current;
        _current = gen;
        _solution = solution;
        _mtimeCache = mtimes;
        if (retired is not null)
        {
            lock (_retiring) _retiring.Add(retired);
            _ = RetireAsync(retired);
        }
    }

    /// <summary>
    /// Cancels a replaced generation's warm-up and disposes its workspace after the grace period.
    /// Whoever removes the generation from <see cref="_retiring"/> disposes it: this method after
    /// the grace, or <see cref="DisposeAsync"/> if the service is disposed first.
    /// </summary>
    private async Task RetireAsync(Generation gen)
    {
        try
        {
            gen.Cts.Cancel();
            try { await gen.Warmup; }
            catch (Exception) { /* cancelled or failed — either way it has stopped touching the solution */ }
            try { await Task.Delay(RetiredWorkspaceGrace, _disposeCts.Token); }
            catch (OperationCanceledException) { return; } // DisposeAsync took this generation over
            lock (_retiring) { if (!_retiring.Remove(gen)) return; }
            gen.Workspace.Dispose();
            gen.Cts.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Disposing a retired workspace generation failed");
        }
    }

    private async Task WarmupAsync(Solution solution, Generation gen)
    {
        var ct = gen.Cts.Token;
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

        if (BeforeIndexBuild is { } hook) await hook();

        // Index build runs after compilations are cached so per-project walks reuse warmed state.
        // A failure is recorded on the generation rather than swallowed: the indexed tools report
        // INDEX_UNAVAILABLE instead of answering from a partial index (IDX-002).
        var indexSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await gen.SymbolIndex.BuildAsync(solution, ct);
            log.LogInformation("Symbol index built in {Elapsed} ms", indexSw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gen.SymbolIndexFailure = ex;
            log.LogWarning(ex, "Symbol index build failed; semantic_search/find_registrations/find_dead_code_candidates will report INDEX_UNAVAILABLE");
        }

        var invIndexSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await gen.InvocationIndex.BuildAsync(solution, ct);
            log.LogInformation("Invocation index built in {Elapsed} ms", invIndexSw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gen.InvocationIndexFailure = ex;
            log.LogWarning(ex, "Invocation index build failed; find_entrypoints/find_registrations will report INDEX_UNAVAILABLE");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposeCts.Cancel(); // retirements still in their grace period stop waiting and yield
            List<Generation> generations;
            lock (_retiring) { generations = [.. _retiring]; _retiring.Clear(); }
            if (_current is not null) generations.Add(_current);

            foreach (var gen in generations)
            {
                gen.Cts.Cancel();
                try { await gen.Warmup; }
                catch (Exception) { /* cancelled or failed — it has stopped either way */ }
                gen.Workspace.Dispose();
                gen.Cts.Dispose();
            }
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
