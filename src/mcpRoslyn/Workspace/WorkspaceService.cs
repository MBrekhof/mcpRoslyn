using System.Collections.Concurrent;
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

        /// <summary>Solution, project and Directory.* build files with their mtimes at load (WS-007).</summary>
        public Dictionary<string, DateTime> BuildFileStamps { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>.cs files created or moved in since load; judged against the solution when read.</summary>
        public ConcurrentDictionary<string, byte> AddedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Documents whose file existed at load and was gone at the last complete refresh.</summary>
        public volatile IReadOnlyList<string> MissingDocuments = [];
        public List<FileSystemWatcher> Watchers { get; } = new();
        public volatile bool WatcherFailed;

        public void Dispose()
        {
            foreach (var watcher in Watchers) watcher.Dispose();
            Workspace.Dispose();
            Cts.Dispose();
        }
    }

    private static readonly string[] DirectoryBuildFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    /// <summary>
    /// Why the loaded workspace may no longer match the disk (WS-007): a solution, project or Directory.*
    /// build file changed, appeared or went away; a .cs file appeared in a project directory — an SDK-style
    /// project picks those up without its .csproj changing; or a document's file was gone at the last refresh.
    /// Document edits are not here: every call already refreshes those. Reported, not acted on: a reload
    /// costs seconds of warm-up. ponytail: a generated or compile-excluded .cs file written outside bin/obj
    /// reads as added until the next reload; excluding it would mean evaluating MSBuild's globs.
    /// </summary>
    public IReadOnlyList<string> StaleReasons
    {
        get
        {
            // ponytail: read without the gate — mid-publish this can pair a new generation with the previous
            // solution for one call, which at worst adds or drops a warning once.
            if (_current is not { } gen || _solution is not { } solution) return [];
            var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(options.SolutionPath))!;
            string Show(string path) => Path.GetRelativePath(solutionDirectory, path);

            var reasons = new List<string>();
            foreach (var (path, stamp) in gen.BuildFileStamps)
            {
                var now = LastWriteOrMissing(path);
                if (now == stamp) continue;
                reasons.Add(stamp == MissingFileTime ? $"{Show(path)} added"
                    : now == MissingFileTime ? $"{Show(path)} deleted"
                    : $"{Show(path)} changed");
            }
            reasons.AddRange(gen.MissingDocuments.Select(path => $"{Show(path)} deleted"));

            if (!gen.AddedPaths.IsEmpty)
            {
                var documents = solution.Projects.SelectMany(p => p.Documents)
                    .Select(d => d.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                // Judged now, not when the event fired: an editor saving through a temp file re-creates a file
                // that is still a document, and a file added and removed again is nothing to report.
                reasons.AddRange(gen.AddedPaths.Keys
                    .Where(path => File.Exists(path) && !documents.Contains(path))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(path => $"{Show(path)} added"));
            }

            if (gen.WatcherFailed) reasons.Add("file watching failed, so added source files may be missed");
            return reasons;
        }
    }

    /// <summary>What <see cref="File.GetLastWriteTimeUtc"/> returns for a file that does not exist.</summary>
    private static readonly DateTime MissingFileTime = DateTime.FromFileTimeUtc(0);

    /// <summary>An unreadable path stamps as missing, the same at load and when compared.</summary>
    private static DateTime LastWriteOrMissing(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return MissingFileTime;
        }
    }

    /// <summary>
    /// Build output and tool folders below <paramref name="root"/> — only below it: the root itself may sit
    /// inside a bin folder (the test fixtures do).
    /// </summary>
    private static bool IsIgnoredBelow(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                      || s.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || s.StartsWith('.'));

    /// <summary>
    /// Stamps the build files and watches the solution and project directories for added .cs files, for
    /// <see cref="StaleReasons"/>. Deletions need no watcher: the per-call refresh checks every document's
    /// file anyway, linked files outside these directories included. Never throws — a watcher that cannot
    /// start marks the generation, it does not fail the load.
    /// </summary>
    private void TrackStaleness(Generation gen, Solution solution)
    {
        var solutionPath = Path.GetFullPath(options.SolutionPath);
        var buildFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { solutionPath };
        var directories = new List<string> { Path.GetDirectoryName(solutionPath)! };
        foreach (var projectPath in solution.Projects.Select(p => p.FilePath).OfType<string>())
        {
            buildFiles.Add(Path.GetFullPath(projectPath));
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            directories.Add(projectDirectory);
            // MSBuild imports the nearest Directory.* file walking up to the root, so a missing one counts too.
            for (var dir = new DirectoryInfo(projectDirectory); dir is not null; dir = dir.Parent)
                foreach (var name in DirectoryBuildFiles) buildFiles.Add(Path.Combine(dir.FullName, name));
        }
        foreach (var path in buildFiles) gen.BuildFileStamps[path] = LastWriteOrMissing(path);

        var roots = new List<string>();
        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length))
            if (Directory.Exists(dir) && !roots.Any(root => IsUnder(dir, root))) roots.Add(dir);

        foreach (var root in roots)
        {
            try
            {
                Watch(root, "*.cs", NotifyFilters.FileName, directories: false);
                // A populated directory moved in raises one event for itself and none for the files inside it.
                Watch(root, "*", NotifyFilters.DirectoryName, directories: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                gen.WatcherFailed = true; // the workspace still serves; additions just can't be vouched for
                log.LogWarning(ex, "Watching {Root} for added source files failed", root);
            }
        }

        void Watch(string root, string filter, NotifyFilters notify, bool directories)
        {
            var watcher = new FileSystemWatcher(root, filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = notify,
                InternalBufferSize = 64 * 1024,
            };
            gen.Watchers.Add(watcher); // registered before it can raise or throw, so disposal always reaches it
            watcher.Created += (_, e) => Note(root, e.FullPath, directories);
            watcher.Renamed += (_, e) => Note(root, e.FullPath, directories);
            watcher.Error += (_, _) => gen.WatcherFailed = true;
            watcher.EnableRaisingEvents = true;
        }

        void Note(string root, string path, bool directory)
        {
            if (IsIgnoredBelow(root, path)) return;
            if (!directory)
            {
                // A rename to an editor backup (Foo.cs~) is raised because its old name matched the filter.
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) gen.AddedPaths.TryAdd(path, 0);
                return;
            }
            // Walked by hand so ignored folders and junctions are pruned before descent (AllDirectories follows
            // reparse points, loops included) and one unreadable subdirectory doesn't abandon its siblings.
            // ponytail: on the watcher's thread, once per directory event — a directory created empty costs
            // nothing; a huge tree moved in at once can overflow the buffer, which sets WatcherFailed.
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var dir))
            {
                try
                {
                    var attributes = File.GetAttributes(dir);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
                        if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) gen.AddedPaths.TryAdd(file, 0);
                    foreach (var child in Directory.EnumerateDirectories(dir))
                        if (!IsIgnoredBelow(root, child)) pending.Push(child);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                           || (ex is IOException && !Directory.Exists(dir)))
                {
                    // moved, deleted or replaced by a file again: nothing was added from it
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    gen.WatcherFailed = true; // an unreadable directory may hold added files
                }
            }
        }

        static bool IsUnder(string dir, string root)
            => dir.Equals(root, StringComparison.OrdinalIgnoreCase)
               || dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private int _loadCount;
    public int LoadCount => Volatile.Read(ref _loadCount);

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

        var changed = new List<DocumentId>();
        var missing = new List<string>();
        try
        {
            foreach (var doc in _solution.Projects.SelectMany(p => p.Documents).ToList())
            {
                if (doc.FilePath is null) continue;
                if (!File.Exists(doc.FilePath))
                {
                    // Deleted since load (WS-007) — only if it existed then: a document generated at build time
                    // may never have had a file.
                    if (_mtimeCache.ContainsKey(doc.Id)) missing.Add(doc.FilePath);
                    continue;
                }
                var diskMtime = File.GetLastWriteTimeUtc(doc.FilePath);
                // Any different timestamp, not only a newer one: a file restored with its older time
                // (a copy or unpack preserving times) is a change too (IDX-004).
                if (_mtimeCache.TryGetValue(doc.Id, out var cachedMtime) && cachedMtime == diskMtime)
                    continue;

                var text = await File.ReadAllTextAsync(doc.FilePath, ct);
                _solution = _solution.WithDocumentText(
                    doc.Id,
                    Microsoft.CodeAnalysis.Text.SourceText.From(text));
                _mtimeCache[doc.Id] = diskMtime;
                changed.Add(doc.Id);
            }
        }
        finally
        {
            // The changed documents and the solution holding their new text reach each index in one step —
            // also when a later file's read throws, or the next call would skip the files already cached.
            if (changed.Count > 0)
            {
                _current.SymbolIndex.Invalidate(changed, _solution);
                _current.InvocationIndex.Invalidate(changed, _solution);
            }
        }

        // Only after a complete pass: a partial one would drop deletions it never reached.
        _current.MissingDocuments = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        var mtimes = new Dictionary<DocumentId, DateTime>();
        try
        {
            solution = await gen.Workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: ct);
            log.LogInformation("Loaded {ProjectCount} projects in {Elapsed} ms from {Path}",
                solution.Projects.Count(), sw.ElapsedMilliseconds, options.SolutionPath);

            // Has to happen here, not in the handler: diagnostics arrive during the load, before there
            // is a project list to check them against (WS-004).
            ReclassifyLoadedProjectDiagnostics(gen, solution);

            foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
            {
                if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
                mtimes[doc.Id] = File.GetLastWriteTimeUtc(doc.FilePath);
            }
            TrackStaleness(gen, solution);
        }
        catch
        {
            // Nothing was published: the previous generation, if any, keeps serving (WS-006).
            gen.Dispose();
            throw;
        }

        // Start warm-up before publishing, so a reader never sees the generation with a completed
        // placeholder task. Its token is the generation's, not the caller's: warm-up outlives the
        // request that loaded it, and ends when the generation is retired or disposed.
        gen.Warmup = Task.Run(() => WarmupAsync(solution, gen));

        var retired = _current;
        // Counted before the swap: a call that reads the new generation's reasons must also see the count move,
        // or it could answer from the old generation with neither warning (WS-007).
        Interlocked.Increment(ref _loadCount);
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
            gen.Dispose();
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
                gen.Dispose();
            }
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
