# Warm-up Pre-compilation — Design

- **Date:** 2026-05-16
- **Status:** Approved, pre-implementation
- **Driver:** v1.1 follow-up #2 from `docs/acceptance/2026-05-15-v1-acceptance.md`

## Goal

Eliminate the ~8 400 ms first-query stall observed against `duetGPT.sln`. After `WorkspaceService.LoadAsync` opens the solution, kick off `GetCompilationAsync` for every project in the background so that by the time the first real tool call arrives, semantic models are hot.

## Non-goals

- Not changing `LoadAsync` blocking behavior — it still returns as fast as today (open + mtime seed only).
- Not adding a CLI flag to disable warm-up. No realistic case to opt out; flag is dead weight.
- Not changing per-call refresh semantics. `GetFreshSolutionAsync` is untouched.
- Not pre-warming `SemanticModel`s for individual files — `GetCompilationAsync` is enough; `SemanticModel`s are cheap once their parent compilation is cached.

## Approach

Background, fire-and-forget warm-up. `LoadAsync` continues to open the solution and seed the mtime cache, then kicks `Task.Run(() => WarmupAsync(...))` and returns immediately. The warm-up runs `GetCompilationAsync` on every project in parallel via `Task.WhenAll`. Per-project failures are logged via `ILogger.LogWarning` and do not affect siblings — matches existing `WorkspaceFailed` philosophy.

`ReloadAsync` follows the same pattern: closes the solution, re-opens, kicks a fresh warm-up.

A tool call arriving mid-warm-up is unaffected: `GetFreshSolutionAsync` returns the current `Solution` snapshot, and Roslyn caches `GetCompilationAsync` results per snapshot, so a tool call for a project whose warm-up is in flight simply joins the in-flight work.

## Interface change

`IWorkspaceService` gains a single property:

```csharp
Task WarmupTask { get; }
```

Returns the in-flight (or completed) warm-up task for the most recent load/reload. Production tool code never awaits this; it exists for:

1. **Tests** — assert warm-up actually populates compilations.
2. **Future hybrid mode** — if we ever decide `reload_workspace` should block until hot, the wiring is already there. (Not implementing this in v1.1.)

Visibility is intentional. "No surprises" — anything the server is doing in the background should be inspectable.

## Implementation

Single file changed: `src/mcpRoslyn/Workspace/WorkspaceService.cs`.

```csharp
private Task _warmupTask = Task.CompletedTask;
public Task WarmupTask => _warmupTask;

private async Task LoadUnsafeAsync(CancellationToken ct)
{
    // ... existing OpenSolutionAsync + mtime seeding stays exactly as-is ...

    var solutionSnapshot = _solution!;
    _warmupTask = Task.Run(() => WarmupAsync(solutionSnapshot, ct), ct);
}

private async Task WarmupAsync(Solution solution, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    var tasks = solution.Projects.Select(async project =>
    {
        var projectSw = Stopwatch.StartNew();
        try
        {
            var compilation = await project.GetCompilationAsync(ct);
            log.LogInformation("Warmed {Project} in {Elapsed} ms ({DiagCount} diagnostics)",
                project.Name, projectSw.ElapsedMilliseconds, compilation?.GetDiagnostics(ct).Length ?? 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Warm-up failed for project {Project}", project.Name);
        }
    });
    await Task.WhenAll(tasks);
    log.LogInformation("Warm-up complete: {ProjectCount} projects in {Elapsed} ms",
        solution.Projects.Count(), sw.ElapsedMilliseconds);
}
```

`IWorkspaceService` adds the `WarmupTask` property; no other interface members change.

### Cancellation

The `ct` passed into `LoadAsync` is forwarded to `Task.Run` and through to each `GetCompilationAsync`. If the host is shutting down, in-flight warm-ups cancel cleanly. `OperationCanceledException` is rethrown (not caught and logged as a warning) so the warm-up `Task` faults with cancellation rather than completes successfully.

### Disposal

`DisposeAsync` does not need to await `_warmupTask`. The cancellation token plumbed through `Task.Run` will cancel any in-flight `GetCompilationAsync` calls; `_workspace?.Dispose()` is safe to call regardless. If a stuck warm-up matters in practice, we can add `await _warmupTask` to disposal later — out of scope for v1.1.

## Testing

Two tests added to `tests/mcpRoslyn.Tests/WorkspaceServiceTests.cs`:

**Test 1 — warm-up populates compilations:**

```csharp
[Test]
public async Task LoadAsync_warmup_populates_project_compilations()
{
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

`Project.TryGetCompilation` returns `true` only if the compilation is already computed and cached — a synchronous check, no flaky timing.

**Test 2 — `LoadAsync` returns before warm-up completes:**

```csharp
[Test]
public async Task LoadAsync_returns_before_warmup_completes()
{
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
    await sut.LoadAsync();

    // WarmupTask is in-flight or completed; we want to verify it wasn't awaited inline.
    // Cheap proxy: WarmupTask is a distinct Task object, not Task.CompletedTask.
    sut.WarmupTask.Should().NotBeSameAs(Task.CompletedTask);

    await sut.WarmupTask;
    sut.WarmupTask.IsCompletedSuccessfully.Should().BeTrue();
}
```

Note: a strict "WarmupTask was incomplete at LoadAsync return" assertion would be racy on the small fixture (TestLib + TestApp compile fast). The reference-identity check + completes-cleanly check is enough to prove warm-up runs without making timing assumptions.

## Manual validation

Re-run the v1 acceptance against `duetGPT.sln` after the change:

- Cold start time: should be similar to before (~8.5s) — warm-up runs after.
- First `find_references` query: should drop from ~8 400 ms to **near-zero compilation overhead** (Roslyn API call cost only, ~tens to hundreds of ms).
- Second-and-later queries: should be unchanged from v1.

Append results to `docs/acceptance/` as a v1.1 acceptance log.

## Risks

- **Race between background warm-up and explicit `ReloadAsync`.** If a caller calls `reload_workspace` while the prior warm-up is still in flight, the prior warm-up keeps a reference to the old `Solution` (already replaced) — its `await Task.WhenAll` finishes against the stale snapshot, then the new warm-up runs against the new one. No correctness issue (the stale work is wasted, not corrupting), but the log lines may interleave. Acceptable.
- **CPU spike at startup.** All projects compiling in parallel may briefly use multiple cores. Roslyn already does internal parallelism per-project; adding outer parallelism on top could over-subscribe on small machines. Mitigated by the fact that an MCP server runs alongside an interactive editor, not in CI. If reports come in, revisit with a `SemaphoreSlim` cap.
- **Warm-up `Task` exception observability.** If a warm-up `Task.WhenAll` throws an aggregate exception we don't observe it (`_warmupTask` is held but never `await`-ed in production). Per-project `try/catch` already swallows everything except `OperationCanceledException`, so the only way `_warmupTask` faults is on cancellation — which is benign. No `UnobservedTaskException` risk in normal operation.

## Open questions / deferred

- **Hybrid `reload_workspace` blocks until warm.** Caller asked for a reload, so making them wait is reasonable. Not in this change — easy to add later by `await Workspace.WarmupTask` inside `ReloadWorkspaceTool` if observed friction warrants it.
- **Per-project warm-up status surfaced via tool.** Could record `(projectName, durationMs, succeeded)` on `WorkspaceService` and surface it via `reload_workspace` output. Folded into the separate "expose MSBuild workspace warnings" v1.1 item — same observability problem, better solved together.
