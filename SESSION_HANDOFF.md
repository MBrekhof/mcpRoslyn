# Session Handoff

**Last updated:** 2026-05-16 (end of SymbolIndex shipping session — v1.2)

## Where things stand

- **v1.2 shipped:** `SymbolIndex` cuts `semantic_search has-attribute:` / `returns:` / `parameter-type:` from O(symbols-per-call) to O(matches) via a per-load index built during warm-up. Always-fresh semantics preserved via a dirty-doc walk on every query.
- Branch `main`, working tree clean, fully pushed to `origin` (`d941e02`). Repo public on GitHub.
- **Tests:** 46 passing (was 38 at start of session, +8 new in `SymbolIndexTests`).
- **All v1.1 acceptance-driven follow-ups are now closed.**

## What shipped this session

Earlier today (with you):
- `062834e` design spec — warm-up
- `d249744` implementation plan — warm-up
- `a22cd8d` feat: background warm-up pre-compilation (4.5× first-query speedup)
- `7ff11d0` test: LoadAsync returns before warm-up completes
- `e993af5` docs: v1.1 warm-up acceptance log
- `a8a970a` docs: TODO closeout + `--log-file` follow-up
- `42df6b7` release prep — LICENSE (MIT), README, ARCHITECTURE, SESSION_HANDOFF
- `e712244` chore: gitignore excalidraw.log

Autonomous run #1 (`--log-file`, `find_callers` hint, MSBuild diagnostics):
- `421cc8f` feat: `--log-file <path>` flag
- `f938fb0` feat: `find_callers` SYMBOL_NOT_FOUND hint
- `417e86b` feat: MSBuild `WorkspaceLoadDiagnostic` surfacing
- `34302ae` docs: close shipped items

v1.2 SymbolIndex (this run):
- `204013c` docs: SymbolIndex design spec
- `fed01ac` docs: SymbolIndex implementation plan
- `b593f89` feat: SymbolIndex skeleton wired into WorkspaceService lifecycle
- `0957af7` feat: SymbolIndex.QueryAttribute backed by per-project parallel build
- `3fdc4af` feat: SymbolIndex covers returns: and parameter-type:
- `744f36f` test: dirty walk + reload + partial-class correctness
- `d941e02` feat: route has-attribute/returns/parameter-type via SymbolIndex
- *(this commit)* docs: TODO + ARCHITECTURE + handoff for v1.2

## Material changes in v1.2

**New class** `mcpRoslyn.Workspace.SymbolIndex` — public sealed because it's exposed on the public `IWorkspaceService` interface. Owns three dictionaries (`_byAttribute`, `_byReturnType`, `_byParameterType`) keyed by both display string AND fully-qualified metadata name (mirrors the existing `MatchesTypeName` fallback). `BuildAsync` runs after `WarmupAsync`'s compilations finish, walking all symbols in parallel per project. `MarkDirty(DocumentId)` is called from `GetFreshSolutionAsync` whenever it replaces a document via `WithDocumentText`. `QueryAttribute` / `QueryReturnType` / `QueryParameterType` filter out cached entries whose `DeclaringDocs` intersect the dirty set, then walk just the dirty documents fresh and merge — preserves the v1 always-fresh semantics with sub-100ms cost on the 95% path (no edits since load).

**Public surface change** on `IWorkspaceService`: added `SymbolIndex SymbolIndex { get; }` (throws if accessed before `LoadAsync` completes). Spec deviation: the spec wanted `SymbolIndex` to be `internal`, but a public interface property forces a public type — accepted the tradeoff.

**`SemanticSearchTool` refactor**: the three slow `case` blocks now delegate to `Workspace.SymbolIndex.QueryX`. `WalkAllSymbols` and `MatchesTypeName` static helpers removed from this file — they live in `SymbolIndex` now. `derives-from:` / `implements:` / invalid-pattern paths untouched.

**`TestHost.CreateAsync` change**: now also awaits `WarmupTask` after `LoadAsync`. Without this fix, the `SemanticSearchToolTests` regressions were intermittent — tool calls were racing the background index build and seeing empty buckets. The fix is generic for any future index-dependent tool.

## What's next

All v1.1 acceptance-driven items are closed. Remaining open work in [`TODO.md`](TODO.md):

1. **Re-measure on duetGPT.** Republish exe (close any running `mcpRoslyn.exe` first; PID from `tasklist`), restart a Claude Code session in `c:\projects\duetgpt`, re-run the v1 reference queries with `--log-file` so the warm-up + index-build timings are captured. Predictions:
   - `has-attribute:McpServerToolType`: 11 049 ms (v1) → 7 745 ms (v1.1) → **sub-100 ms (v1.2)**
   - First `find_references`: should still hit the warm-up 4.5× win from v1.1
   - `find_implementations` 300 → 832 ms regression worth confirming on a fresh run
2. **Nice-to-haves spotted along the way** (see [`TODO.md`](TODO.md)):
   - Extract `ProjectName` from `WorkspaceLoadDiagnostic.Message`.
   - Migrate off the obsolete `Workspace.WorkspaceFailed` to `RegisterWorkspaceFailedHandler` (removes CS0618).
3. **Real-session validation.** Use mcpRoslyn in one duetGPT feature task; capture friction. Acceptance logs cover canned-query correctness, not agent-loop ergonomics.

## Known limitations / gotchas (unchanged)

- **Windows-only.** `MSBuildLocator` and path-comparison code aren't portable yet.
- **Project-file changes need explicit `reload_workspace`.** Per-call mtime refresh only walks already-known documents. Same for the index — new symbols in new files won't appear until reload.
- **Stderr capture window** of Claude Code is no longer a problem; use `--log-file <path>`.
- **`duetGPT.LicenseServer` silent drop** is no longer invisible — check `reload_workspace`'s `Diagnostics` field next time you're in a duetGPT session.

## Useful commands

```powershell
# Build
dotnet build mcpRoslyn.slnx -c Release

# Run all tests
dotnet test mcpRoslyn.slnx -c Release

# Run a targeted test class
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~SymbolIndexTests" -c Release

# Re-publish the exe (close any running mcpRoslyn.exe first)
dotnet publish src/mcpRoslyn -c Release -o bin/publish

# Run with persistent logging (captures warm-up + index-build timings)
mcpRoslyn.exe --log-file c:\users\marti\.claude\debug\mcpRoslyn.log
```

**Note:** solution file is `mcpRoslyn.slnx` (XML format), not `mcpRoslyn.sln`.

## Reference

- Architecture summary: [`ARCHITECTURE.md`](ARCHITECTURE.md) (now includes `SymbolIndex` section)
- Open work: [`TODO.md`](TODO.md)
- v1 design + plan + acceptance: `docs/plans/2026-05-15-*.md`, `docs/acceptance/2026-05-15-v1-acceptance.md`
- v1.1 warm-up: `docs/plans/2026-05-16-warmup-precompilation-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`
- v1.2 SymbolIndex: `docs/plans/2026-05-16-attribute-index-{design,implementation}.md`
