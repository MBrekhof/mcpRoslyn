# Warm-up Pre-compilation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the ~8 400 ms first-query stall by pre-compiling every project in the background after `WorkspaceService.LoadAsync` returns.

**Architecture:** `LoadUnsafeAsync` keeps its current open-and-seed-mtime-cache behavior, then assigns `_warmupTask = Task.Run(...)` that calls `GetCompilationAsync` for every project in parallel via `Task.WhenAll`. A new `IWorkspaceService.WarmupTask` property exposes this for tests and a possible future hybrid `reload_workspace` blocking mode.

**Tech Stack:** .NET 10, C#, Roslyn (`Microsoft.CodeAnalysis.Workspaces.MSBuild`), NUnit + FluentAssertions.

**Design reference:** [`2026-05-16-warmup-precompilation-design.md`](./2026-05-16-warmup-precompilation-design.md)

**Working directory:** `c:\projects\mcpRoslyn\`

---

## Conventions

- **Branch:** all work on `main` (matches v1 convention).
- **Build command:** `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release` — must produce 0 errors before any commit.
- **Test command:** `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~WorkspaceServiceTests" -c Release` — targeted at the touched class.
- **Commit style:** Conventional commits. Co-author trailer: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **stdout discipline:** never `Console.WriteLine`. Logs go to `ILogger` only — already wired to stderr in `Program.cs`.
- **Namespace casing:** the project uses lowercase `mcpRoslyn` as the C# namespace (not `McpRoslyn`). All using directives and namespace declarations stay lowercase.

---

## File map

| File | Change | Why |
|---|---|---|
| `src/mcpRoslyn/Workspace/IWorkspaceService.cs` | Modify | Add `Task WarmupTask { get; }` property |
| `src/mcpRoslyn/Workspace/WorkspaceService.cs` | Modify | Add backing field, property, `WarmupAsync` private method, `Task.Run` kick from `LoadUnsafeAsync` |
| `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs` | Modify | Add 2 tests: warm-up populates compilations; `LoadAsync` returns before warm-up completes |
| `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md` | Create | Manual acceptance log: cold-start + first-query timings vs v1 baseline |

No new files. Single existing class evolves; single existing test fixture extends.

---

## Task 1: Add `WarmupTask` to interface, implement background warm-up, and verify compilations cache

**Files:**
- Modify: `src/mcpRoslyn/Workspace/IWorkspaceService.cs`
- Modify: `src/mcpRoslyn/Workspace/WorkspaceService.cs`
- Modify: `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs` (inside the existing `WorkspaceServiceTests` class, after the last `[Test]` method):

```csharp
[Test]
public async Task LoadAsync_warmup_populates_project_compilations()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

    await sut.LoadAsync();
    await sut.WarmupTask;

    var solution = await sut.GetFreshSolutionAsync();
    foreach (var project in solution.Projects)
    {
        project.TryGetCompilation(out var compilation).Should().BeTrue(
            "warm-up should have cached the compilation for {0}", project.Name);
        compilation.Should().NotBeNull();
    }
}
```

`Project.TryGetCompilation(out Compilation?)` is a synchronous Roslyn API that returns `true` only if the compilation has already been computed and cached on the `Project` snapshot. No timing assumptions — pure cache-check.

- [ ] **Step 2: Run test to verify it fails (compile error)**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~LoadAsync_warmup_populates_project_compilations" -c Release`

Expected: BUILD FAIL — `'IWorkspaceService' does not contain a definition for 'WarmupTask'`. The test cannot compile because the property doesn't exist yet.

- [ ] **Step 3: Add `WarmupTask` to the interface**

Replace the contents of `src/mcpRoslyn/Workspace/IWorkspaceService.cs` with:

```csharp
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
```

- [ ] **Step 4: Implement the property + warm-up in `WorkspaceService`**

Replace the contents of `src/mcpRoslyn/Workspace/WorkspaceService.cs` with:

```csharp
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
    private Task _warmupTask = Task.CompletedTask;

    public int LoadedProjectCount => _solution?.Projects.Count() ?? 0;
    public Task WarmupTask => _warmupTask;

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
            }

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
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _workspace?.Dispose(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
```

Key points:

- `_warmupTask` is initialized to `Task.CompletedTask` so any caller awaiting it before the first `LoadAsync` gets a no-op (defensive).
- The `Task.Run` snapshot pattern (`var solutionSnapshot = _solution;`) captures the `Solution` value at the moment of load. Even if `GetFreshSolutionAsync` later replaces `_solution` via `WithDocumentText`, the warm-up's compilations against the snapshot are still useful — Roslyn reuses unchanged trees incrementally.
- Per-project `try/catch` swallows everything except `OperationCanceledException`, so a faulted warm-up `Task` only happens on cancellation (benign) — no `UnobservedTaskException` risk.
- Cancellation token threads from `LoadAsync(ct)` → `Task.Run(..., ct)` → `GetCompilationAsync(ct)`, so host shutdown cancels in-flight work cleanly.

- [ ] **Step 5: Build**

Run: `dotnet build c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`

Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run the new test to verify it passes**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~LoadAsync_warmup_populates_project_compilations" -c Release`

Expected: 1 test, PASS.

- [ ] **Step 7: Run the full `WorkspaceServiceTests` class to confirm no regression**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~WorkspaceServiceTests" -c Release`

Expected: 3 tests, all PASS (the 2 existing tests plus the new one).

- [ ] **Step 8: Commit**

```
git add src/mcpRoslyn/Workspace/IWorkspaceService.cs src/mcpRoslyn/Workspace/WorkspaceService.cs tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs
git commit -m "feat: background warm-up pre-compilation in WorkspaceService"
```

---

## Task 2: Add `LoadAsync_returns_before_warmup_completes` test

**Files:**
- Modify: `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs`

This test pins down the contract: `LoadAsync` does NOT inline-await warm-up. If a future change accidentally awaits it, this test fails.

- [ ] **Step 1: Add the test**

Append to `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs` (inside the `WorkspaceServiceTests` class, after the test added in Task 1):

```csharp
[Test]
public async Task LoadAsync_returns_before_warmup_completes()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

    await sut.LoadAsync();

    // WarmupTask must be a fresh Task (not the Task.CompletedTask sentinel),
    // proving warm-up was kicked off rather than awaited inline.
    sut.WarmupTask.Should().NotBeSameAs(Task.CompletedTask);

    // And it must complete cleanly when given the chance.
    await sut.WarmupTask;
    sut.WarmupTask.IsCompletedSuccessfully.Should().BeTrue();
}
```

A strict "WarmupTask was incomplete at LoadAsync return" check would race on the small fixture. The reference-identity check is the correct anchor: `Task.CompletedTask` is a singleton, and `Task.Run(...)` always returns a different instance.

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~LoadAsync_returns_before_warmup_completes" -c Release`

Expected: 1 test, PASS. (No production code change needed — Task 1 already implemented the behavior.)

- [ ] **Step 3: Run the full `WorkspaceServiceTests` class to confirm**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln --filter "FullyQualifiedName~WorkspaceServiceTests" -c Release`

Expected: 4 tests, all PASS.

- [ ] **Step 4: Commit**

```
git add tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs
git commit -m "test: pin LoadAsync-returns-before-warmup contract"
```

---

## Task 3: Republish exe and run manual acceptance against `duetGPT.sln`

This task is non-automated. Goal: confirm the first-query latency drop is real on a production-sized solution and document it as a v1.1 acceptance log.

**Files:**
- Create: `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`

- [ ] **Step 1: Run the full test suite to confirm green**

Run: `dotnet test c:\projects\mcpRoslyn\mcpRoslyn.sln -c Release`

Expected: all tests PASS. (Acceptance tests included — they exercise the published exe via stdio and may take a minute.)

- [ ] **Step 2: Republish the self-contained exe**

Run: `dotnet publish c:\projects\mcpRoslyn\src\mcpRoslyn -c Release -o c:\projects\mcpRoslyn\bin\publish`

Expected: `c:\projects\mcpRoslyn\bin\publish\mcpRoslyn.exe` rewritten with the new code. Verify the modified timestamp.

- [ ] **Step 3: Restart any active Claude Code sessions that have mcpRoslyn loaded**

The MCP server is spawned per session; an existing session is running the old exe. Restart the duetGPT Claude Code session so it spawns the new binary.

- [ ] **Step 4: Run the v1 reference queries against `duetGPT.sln` and record timings**

In the duetGPT Claude Code session, invoke the same 4 queries from `docs/acceptance/2026-05-15-v1-acceptance.md`:

1. `find_references` on `IGroupPermissionResolver`
2. `find_implementations` on `IBuiltInToolProvider`
3. `find_callers` on `KnowledgeService.ExpandQueryAsync`
4. `semantic_search has-attribute:McpServerToolType`

For each, record: time, result count, error (if any).

Also note from the **mcpRoslyn process stderr** (visible in Claude Code's MCP debug log):

- Cold-start time ("Loaded N projects in X ms")
- Per-project warm-up timings ("Warmed {Project} in X ms")
- Total warm-up time ("Warm-up complete: N projects in X ms")

- [ ] **Step 5: Compare to v1 baseline**

| Metric | v1 baseline | v1.1 expected |
|---|---|---|
| Cold start (LoadAsync return) | 8 504 ms | similar (~8 500 ms) — open + mtime seed unchanged |
| Total warm-up time | n/a | new metric; expect ~8 000 ms running in background |
| First `find_references` query | 8 400 ms | **near-zero compilation overhead** (~tens to hundreds of ms) if warm-up finished before the call; otherwise similar to v1 if Claude Code sends it instantly |
| Subsequent queries (300 ms, 14 ms, 11 049 ms) | unchanged | unchanged |

The headline result: if warm-up has finished by the time the first user query lands, the first query should be dramatically faster. If the user is fast and the call beats warm-up, behavior degrades gracefully to v1 timing for that one project.

- [ ] **Step 6: Write the acceptance log**

Create `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md` using this template (fill in actual numbers):

```markdown
# v1.1 Warm-up Acceptance — 2026-05-16

Target: `c:\projects\duetgpt\duetGPT\duetGPT.sln`
Change under test: background pre-compilation (commit <sha>)

## Cold start

- LoadAsync return: <X> ms (v1 baseline: 8 504 ms)
- Warm-up total: <X> ms (new metric)
- Per-project warm-up: <list from stderr>

## Query results

### 1. find_references on IGroupPermissionResolver
- Time: <X> ms (v1 baseline: 8 400 ms)
- Reference count: <N> (v1 baseline: 30)
- Result: PASS / FAIL

### 2. find_implementations on IBuiltInToolProvider
- Time: <X> ms (v1: 300 ms)
- Implementation count: <N> (v1: 1)

### 3. find_callers on KnowledgeService.ExpandQueryAsync
- Time: <X> ms (v1: 14 ms)
- Error: <code or none>

### 4. semantic_search has-attribute:McpServerToolType
- Time: <X> ms (v1: 11 049 ms)
- Match count: <N> (v1: 1)

## Verdict

<Did warm-up land before or after the first user query? What was the observed first-query speedup? Any per-project warm-up failures logged?>

## Follow-ups

<Anything surprising. If `semantic_search` is still slow, that's the attribute-walk problem — separate v1.1 item, not addressed here.>
```

- [ ] **Step 7: Commit the acceptance log**

```
git add docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md
git commit -m "docs: v1.1 warm-up acceptance log against duetGPT.sln"
```

- [ ] **Step 8: Update `TODO.md`**

Strike through (or remove) the "Warm-up / pre-compilation on load" item in `TODO.md` and reference the acceptance log. Commit:

```
git add TODO.md
git commit -m "docs: mark warm-up pre-compilation done"
```

---

## Task summary

| # | Task | Type |
|---|---|---|
| 1 | Add `WarmupTask` interface + impl + cache test | TDD |
| 2 | Pin `LoadAsync_returns_before_warmup_completes` contract | Test (regression-pin) |
| 3 | Republish + manual acceptance against duetGPT.sln + log | Validation |

**Estimated commit count:** 4 (Task 3 produces 2 commits — acceptance log + TODO update). **Estimated test count after Task 2:** 4 in `WorkspaceServiceTests` (2 existing + 2 new).

---

## Risks & traps

- **`Task.CompletedTask` reference identity.** The Task 2 assertion relies on `Task.Run` always returning a new `Task` distinct from `Task.CompletedTask`. This is guaranteed by the BCL — `Task.Run` constructs a new `Task` instance every call.
- **Cancellation token reuse across call lifetimes.** The token passed to `LoadAsync` is forwarded into `Task.Run` and the warm-up. If the caller passes a short-lived `CancellationToken` (e.g., from a request scope) and disposes it before warm-up finishes, the warm-up cancels mid-flight. In the actual server, `WorkspaceLoaderHostedService.StartAsync(CancellationToken ct)` passes the host's startup token, which is only cancelled on host shutdown — fine. Tests pass `CancellationToken.None`. Document this implicit contract via the comment in the production code if a future caller looks suspicious.
- **`Project.TryGetCompilation` semantics.** The Roslyn API returns `true` only if a `Compilation` is already cached on that specific `Project` snapshot. `GetFreshSolutionAsync` returns the same `_solution` reference (or a `WithDocumentText`-derived one for changed files), so test assertion is valid: every project we warmed has a cached compilation on the current solution snapshot. If a test were to call `GetFreshSolutionAsync` AFTER mutating multiple files, the changed projects would have new `Project` instances without cached compilations — out of scope for these tests.
- **Stderr log volume.** Per-project warm-up adds N+1 `LogInformation` lines per load (N projects + 1 summary). On a 4-project solution, that's 5 lines. Acceptable. If a future user complains, demote per-project lines to `LogDebug`.
- **Partial loads on startup.** If `MSBuildWorkspace.OpenSolutionAsync` partially fails (some projects load, some don't — see duetGPT's silent 4/5 case), warm-up runs only against the projects that loaded. Failed projects emit `WorkspaceFailed` events, which already log a warning. No new behavior needed here.
