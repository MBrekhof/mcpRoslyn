# Session Handoff

**Last updated:** 2026-05-16 (end of autonomous v1.1 cleanup session)

## Where things stand

- **v1.1 follow-ups complete** for everything except the `semantic_search` attribute index. 4 of the 5 acceptance-driven v1.1 items shipped this session.
- **Repo public** on GitHub: https://github.com/MBrekhof/mcpRoslyn — MIT licensed, README is current.
- Branch `main`, working tree clean, fully pushed to `origin`.
- **38 tests passing** (was 30 at start of day, +8 across the warm-up and diagnostics work).

## What shipped this session (in order)

Earlier today (with you):
- `062834e` design spec — warm-up pre-compilation
- `d249744` implementation plan — warm-up
- `a22cd8d` feat: background warm-up pre-compilation (the headline 4.5× speedup)
- `7ff11d0` test: pin LoadAsync-returns-before-warmup contract
- `e993af5` docs: v1.1 warm-up acceptance log
- `a8a970a` docs: mark warm-up done, add `--log-file` follow-up
- `42df6b7` docs: release prep — LICENSE (MIT), README expansion, ARCHITECTURE, SESSION_HANDOFF
- `e712244` chore: gitignore `excalidraw.log`

Autonomous run (while you were away):
- `421cc8f` feat: `--log-file <path>` flag for persistent ILogger output
- `f938fb0` feat: enrich `find_callers` SYMBOL_NOT_FOUND with `workspace_symbol` hint
- `417e86b` feat: surface MSBuild `WorkspaceFailed` events as `WorkspaceLoadDiagnostic`

## Material changes in the autonomous run

1. **`--log-file <path>`** — new optional CLI flag. New `mcpRoslyn.Logging.FileLoggerProvider` (custom `ILoggerProvider`) appends to the file in thread-safe mode, creates parent dirs on demand, formats as `{iso-timestamp} {level} {category} - {message}` with exceptions on the following line. Wired via `builder.Logging.AddProvider(new FileLoggerProvider(path))` in `Program.cs` when the flag is set. 4 new tests.

2. **`find_callers` hint** — when `SYMBOL_NOT_FOUND` is returned from either the symbolId path or the cursor-position path, the `ToolError.Hint` field is populated with a recovery suggestion. The symbolId path points at `workspace_symbol` (the duetGPT acceptance case); the cursor path suggests verifying the position or falling back to `workspace_symbol`. Redundant final null-check removed; each branch now returns inline. 1 new test.

3. **MSBuild diagnostics surfacing** — `WorkspaceFailed` events now accumulate in a list on `WorkspaceService`. New `IWorkspaceService.Diagnostics` property exposes a snapshot (`IReadOnlyList<WorkspaceLoadDiagnostic>`). List is cleared at the top of every `LoadUnsafeAsync`. `ReloadResult` gains a `Diagnostics` field, so `reload_workspace` callers can see exactly which projects failed and why. 3 new tests including a synthetic-broken-`.sln` case that lives in a temp dir (does not pollute the existing fixture). DTO is named `WorkspaceLoadDiagnostic` to avoid colliding with `Microsoft.CodeAnalysis.WorkspaceDiagnostic`.

## What I did NOT do (and why)

- **`semantic_search` attribute index** — Larger change with real design tradeoffs (eager vs lazy build, invalidation on reload-only or per-edit, memory cost on huge solutions). Wanted your input on the lifecycle model before committing. The current ~7.7 s `has-attribute:` time on duetGPT is the next obvious perf win.
- **Re-measure `find_implementations`** — Needed another acceptance run against a live duetGPT Claude Code session; can't do that autonomously without killing your active sessions.
- **Real-session validation** — Inherently requires you. Acceptance logs covered query correctness, not agent-loop ergonomics.
- **Fix the `WorkspaceFailed` obsolete warning (CS0618)** — Out of scope for any of the TODO items; flagged as a nice-to-have.

## What's next

See [`TODO.md`](TODO.md). Open work, in priority order:

1. **`semantic_search` attribute index** — the last meaningful v1.1 perf item. Needs a quick design conversation: eager (build at load + post-warmup) vs lazy (build on first `has-attribute:` call). Lifetime: invalidate on reload only? On document changes? My instinct: eager, invalidate on reload only, memory cost is probably negligible for any solution that fits in `MSBuildWorkspace` to begin with.
2. **Nice-to-haves spotted along the way** (in [`TODO.md`](TODO.md)):
   - Extract `ProjectName` from `WorkspaceLoadDiagnostic.Message` (regex on the embedded csproj path).
   - Migrate off `Workspace.WorkspaceFailed` to `RegisterWorkspaceFailedHandler` (removes the CS0618).
   - Re-measure `find_implementations` to confirm v1.1 acceptance's 300→832 ms wasn't noise.
3. **Real-session validation** — use mcpRoslyn in one duetGPT feature task; capture friction.

## Known limitations / gotchas (unchanged)

- **Windows-only.** `MSBuildLocator` and path-comparison code aren't portable yet.
- **Project-file changes need explicit `reload_workspace`.** Per-call mtime refresh only walks already-known documents; new/deleted .cs files, .csproj edits, and new projects in .sln are NOT auto-detected.
- **Stderr capture window** is no longer a problem — `--log-file <path>` solves it. Use that for any future diagnostics work.
- **`duetGPT.LicenseServer` silent drop** is no longer invisible — the diagnostics list will show why it failed to load. Investigate via `reload_workspace` output next time you're in a duetGPT session.

## Useful commands

```powershell
# Build
dotnet build mcpRoslyn.slnx -c Release

# Run all tests
dotnet test mcpRoslyn.slnx -c Release

# Run a targeted test class
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~FindReferencesToolTests" -c Release

# Re-publish the exe (close any running mcpRoslyn.exe first)
dotnet publish src/mcpRoslyn -c Release -o bin/publish

# Run with persistent logging
mcpRoslyn.exe --log-file c:\users\marti\.claude\debug\mcpRoslyn.log
```

**Note:** the solution file is `mcpRoslyn.slnx` (new XML format), not `mcpRoslyn.sln`.

## Reference

- Architecture summary: [`ARCHITECTURE.md`](ARCHITECTURE.md)
- Open work: [`TODO.md`](TODO.md)
- v1 design: [`docs/plans/2026-05-15-mcproslyn-design.md`](docs/plans/2026-05-15-mcproslyn-design.md)
- v1 implementation plan: [`docs/plans/2026-05-15-mcproslyn-implementation.md`](docs/plans/2026-05-15-mcproslyn-implementation.md)
- v1 acceptance: [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)
- v1.1 warm-up design: [`docs/plans/2026-05-16-warmup-precompilation-design.md`](docs/plans/2026-05-16-warmup-precompilation-design.md)
- v1.1 warm-up plan: [`docs/plans/2026-05-16-warmup-precompilation-implementation.md`](docs/plans/2026-05-16-warmup-precompilation-implementation.md)
- v1.1 warm-up acceptance: [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)
