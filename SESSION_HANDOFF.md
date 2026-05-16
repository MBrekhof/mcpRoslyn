# Session Handoff

**Last updated:** 2026-05-16 (end of v1.1 warm-up shipping session)

## Where things stand

- **v1.1 shipped:** background pre-compilation in `WorkspaceService`. First `find_references` on `duetGPT.sln` dropped from 8 400 ms → 1 874 ms (**4.5×**) — the exact regression v1 acceptance flagged.
- Branch: `main`. After this session ends: working tree clean, `main` pushed to `origin`, repo **public** on GitHub (https://github.com/MBrekhof/mcpRoslyn).
- 30 tests passing (4 in `WorkspaceServiceTests`, including 2 new for warm-up); fixture-based, no mocks.
- Self-contained `bin/publish/mcpRoslyn.exe` republished and verified live against `duetGPT.sln`.

## What was completed this session

1. **Documentation scaffolding** — created `ARCHITECTURE.md`, `TODO.md`, `SESSION_HANDOFF.md` (was missing).
2. **Warm-up pre-compilation** (v1.1 follow-up #2 from `docs/acceptance/2026-05-15-v1-acceptance.md`):
   - Design: `docs/plans/2026-05-16-warmup-precompilation-design.md` (commit `062834e`).
   - Plan: `docs/plans/2026-05-16-warmup-precompilation-implementation.md` (commit `d249744`).
   - Implementation: background `Task.Run` from `LoadUnsafeAsync`; new `WarmupTask` property on `IWorkspaceService` for tests + future hybrid mode (commit `a22cd8d`).
   - Contract test: `LoadAsync_returns_before_warmup_completes` (commit `7ff11d0`).
   - Acceptance log: `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md` (commit `e993af5`).
   - TODO closeout + new `--log-file` follow-up (commit `a8a970a`).
3. **Public release**: added MIT LICENSE, expanded README (requirements, tool list, performance table), made repo public on GitHub.

## What's next

See [`TODO.md`](TODO.md). Highest-leverage items:

1. **`--log-file <path>` flag** (new — surfaced during v1.1 acceptance). Claude Code's `--debug mcp` only captures stderr until the MCP `initialize` handler completes, so warm-up's per-project log lines were lost. A small `Program.cs` change to tee `ILogger` to a file would make future diagnostics trivial. Small change (~10 lines).
2. **Re-measure `find_implementations`.** v1.1 acceptance showed an apparent regression (300 ms → 832 ms) on a single sample. One more run will tell if it's noise or real.
3. **Expose MSBuild workspace warnings.** `duetGPT.LicenseServer` is still silently dropped (4/5 projects load) — invisible to callers.
4. **`semantic_search` attribute index.** Still ~7.7 s on duetGPT.sln. Pre-built index keyed by attribute full name should bring this sub-second.
5. **Real-session validation.** Use mcpRoslyn in a feature-sized duetGPT task and note end-to-end agent-loop friction.

## Known limitations / gotchas

- **Windows-only.** `MSBuildLocator` and path-comparison code aren't portable yet.
- **Stderr capture window.** Claude Code stops surfacing MCP stderr after `initialize`. For diagnostics, either run `claude --debug mcp` (only captures startup burst) or wait for `--log-file` to land.
- **`duetGPT.LicenseServer` silent drop.** MSBuildWorkspace skips it; no clear error surfaces. Tracked in TODO.
- **Project-file changes need explicit `reload_workspace`.** mtime refresh only walks already-known documents; new .cs files, deleted .cs files, .csproj edits, and new projects in .sln are NOT auto-detected.

## Useful commands

```powershell
# Build
dotnet build mcpRoslyn.slnx -c Release

# Run all tests
dotnet test mcpRoslyn.slnx -c Release

# Run a targeted test class
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~FindReferencesToolTests" -c Release

# Re-publish the exe after a fix (must close any running mcpRoslyn.exe first!)
dotnet publish src/mcpRoslyn -c Release -o bin/publish
```

**Note:** the solution file is `mcpRoslyn.slnx` (new XML format), not `mcpRoslyn.sln`. Earlier docs referenced `.sln` — that's stale.

## Reference

- Architecture summary: [`ARCHITECTURE.md`](ARCHITECTURE.md)
- Open work: [`TODO.md`](TODO.md)
- v1 design: [`docs/plans/2026-05-15-mcproslyn-design.md`](docs/plans/2026-05-15-mcproslyn-design.md)
- v1 implementation plan: [`docs/plans/2026-05-15-mcproslyn-implementation.md`](docs/plans/2026-05-15-mcproslyn-implementation.md)
- v1 acceptance: [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)
- v1.1 warm-up design: [`docs/plans/2026-05-16-warmup-precompilation-design.md`](docs/plans/2026-05-16-warmup-precompilation-design.md)
- v1.1 warm-up plan: [`docs/plans/2026-05-16-warmup-precompilation-implementation.md`](docs/plans/2026-05-16-warmup-precompilation-implementation.md)
- v1.1 acceptance: [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)
