# WS-008: degrade instead of dying when the solution fails to load at startup

**Card:** WS-008 (ID: 1801). **Repro (verified 2026-09-22):** `bin\publish\mcpRoslyn.exe` with cwd `C:\Projects\netwasm`
(global.json pins SDK 10.0.302, `rollForward: disable`; machine has 10.0.401) dies with
`RemoteInvocationException ... hostfxr_resolve_sdk2 ... A compatible .NET SDK was not found. Requested SDK version:
10.0.302 global.json file: C:\Projects\netwasm\global.json ... Install the [10.0.302] .NET SDK or update [...global.json]`.
Path: `WorkspaceLoaderHostedService.StartAsync` -> `WorkspaceService.LoadAsync` -> `LoadUnsafeAsync` throws ->
"Hosting failed to start" -> process exits -> Claude Code shows `CONNECTION_CLOSED`.

## Change

1. `WorkspaceService.LoadAsync` (startup only; `ReloadAsync` keeps throwing so `reload_workspace` reports its own failure):
   catch any exception except cancellation of the caller's token, `LogError` it (stderr + `--log-file`), and store it in
   a `volatile Exception? _startupFailure`. The server stays up. `LoadUnsafeAsync` already disposes the unpublished
   generation on failure, so there is nothing to clean up.
2. The four `throw new InvalidOperationException("Workspace not loaded.")` sites (`SymbolIndex`, `InvocationIndex`,
   `GetIndexedSolutionAsync`, `RefreshUnsafeAsync`) go through one helper that, when `_startupFailure` is set, says
   `Workspace not loaded: opening <path> failed at startup: <exception message>`. The helper is only reached while
   `_current` is null, so a later successful `reload_workspace` makes the failure irrelevant without clearing it.
3. `ToolBase` maps that exception to `WORKSPACE_NOT_LOADED` as today, and adds the hint "Fix the cause, then call
   reload_workspace (the server did not need restarting)." A dedicated `WorkspaceNotLoadedException :
   InvalidOperationException` carries it, so other InvalidOperationExceptions do not get that hint.
4. `reload_workspace` with no generation loaded already works: `ReloadAsync` loads `SolutionPath`, which falls back to
   the options path.

The SDK text is not re-derived: the hostfxr message already names the requested SDK, the global.json path and the fix.
The card's optional up-front global.json check is skipped for that reason.

## Tests

- `WorkspaceServiceTests`: a temp dir holding a copy of a one-project fixture plus a `global.json` pinning a
  non-existent SDK (`9.9.999`, `rollForward: disable`). `LoadAsync` completes without throwing;
  `GetFreshSolutionAsync` throws `WorkspaceNotLoadedException` whose message contains `global.json`.
- `McpProtocolTests`: the built exe started over stdio against that temp solution. `initialize` succeeds,
  `project_overview` returns `WORKSPACE_NOT_LOADED` with `isError`, and a second call still answers (the process is alive).

## Codex round 1 (settled)

- **Blocker "MSBuildLocator.RegisterDefaults throws on the pin" was refuted by the repro.** The netwasm launch
  (cwd = the pinned repo) got past `RegisterDefaults` and died inside hosting with the BuildHost's
  `RemoteInvocationException`. No change.
- **Accepted:** `ReloadAsync` with no generation published (checked under `_gate`) wraps its failure in
  `WorkspaceNotLoadedException`, so a failed `reload_workspace` returns `WORKSPACE_NOT_LOADED` plus the hint instead of
  `INTERNAL_ERROR`. A failed reload while a generation is serving keeps today's behaviour.
- **Accepted:** the startup catch excludes `OperationCanceledException` when the caller's token is cancelled.
- **Accepted, tests:** one degraded session goes through: tool fails -> reload fails (`WORKSPACE_NOT_LOADED`) ->
  cause fixed (delete global.json) -> reload succeeds -> an ordinary, an indexed, and a compilation-diagnostics call
  succeed. Plus: a cancelled startup token still throws.
- **Accepted (pre-existing, one line):** `project_overview` reads the solution and diagnostics separately, so it can mix
  generations. It switches to `GetFreshSolutionWithDiagnosticsAsync`.

**Correction during implementation:** the refutation above was wrong in general. With a `9.9.999` pin and the cwd inside
the pinned directory, `MSBuildLocator.RegisterDefaults()` throws (`DotNetSdkLocationHelper.GetSdkFromGlobalSettings`)
at `Program.cs:12`, before the host exists (exit `0xE0434352`). netwasm's pin happened to get past it. The fix:
`RegisterMSBuild()` tries `RegisterDefaults()` so a resolvable pin is still honoured, and on `InvalidOperationException`
registers the newest `DotNetSdk` instance queried with `WorkingDirectory = AppContext.BaseDirectory`. The solution's
load then fails in the BuildHost and is reported as above. The protocol test launches with the cwd inside the pinned
directory.

## Folded in (user, 2026-09-22): no solution found at startup

`ParseArgs` also kills the process before the host starts: `FileNotFoundException` at `Program.cs:65` when no
`.sln`/`.slnx` is discovered and `--solution` is omitted, and at `Program.cs:68` when `--solution` names a missing file.
Same `CONNECTION_CLOSED` symptom.

5. `ParseArgs` stops validating: `SolutionPath = --solution ?? SolutionDiscovery.Discover(cwd)`, and
   `McpRoslynOptions.SolutionPath` becomes `string?`.
6. `LoadAsync` with a null path records `_startupFailure = FileNotFoundException("No --solution provided and no .sln or
   .slnx found by searching up or down from <cwd>")`. A given-but-missing path needs no special case, because
   `OpenSolutionAsync` throws and step 1 catches it.
7. `IWorkspaceService.SolutionPath` stays `string`: `_current?.SolutionPath ?? full(options path) ?? ""`.
   `ReloadAsync(null)` with no known path re-runs discovery from the cwd, since a solution may have been created since
   startup. If discovery still finds nothing it throws the same no-solution message, which `reload_workspace` returns as
   its error.
8. Test: `WorkspaceServiceTests`, with options `SolutionPath = null`. `LoadAsync` completes; a tool call gets
   `WORKSPACE_NOT_LOADED` naming the missing solution.
