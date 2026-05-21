# Session Handoff

**Last updated:** 2026-05-21 (end of v1.3 acceptance + merge to main)

## Where things stand

- **v1.3 IS LIVE ON MAIN.** Merged via `--no-ff` (merge commit `33d8ad4`). Tag not yet cut; do that next session if you want `v1.3.0` to be addressable.
- Branch `feat/v1.3-feature-expansion` still exists on origin (left for issue-referencing convenience; safe to delete once #1–#4 are closed).
- Working tree on main is clean.
- **Tests:** 103 passing (unchanged from end of feature-expansion session — this session was acceptance + merge, no source changes).
- **Acceptance verdict: PASS-WITH-FOLLOWUPS.** Full report at `docs/acceptance/2026-05-21-v1.3-acceptance.md`.

## What this session did

1. **Ran the v1.3 acceptance plan against duetGPT** (4 projects, 492 documents). All 7 new tools functional and correct; format-summary and filter-knob smokes both green. Server-internal timings pulled from `mcpRoslyn-v1.3.log` for accuracy (Claude→MCP wall-clock had too much transport overhead to be useful).
2. **Filled in `docs/acceptance/2026-05-21-v1.3-acceptance.md`** with measurements, spot-check accuracy notes, surfaced issues, and verdict.
3. **Filed 4 GitHub issues** for the regressions + correctness bug (see "Open follow-ups" below).
4. **Merged `feat/v1.3-feature-expansion` → `main` with `--no-ff`** and pushed.

## Open follow-ups (filed as GitHub issues)

| # | Title | Type |
|---|---|---|
| [#1](https://github.com/MBrekhof/mcpRoslyn/issues/1) | InvocationIndex warm-up cost ~30x over predicted budget (~13s vs +400ms) | enhancement |
| [#2](https://github.com/MBrekhof/mcpRoslyn/issues/2) | `find_references` cold-cache 2.8x regression (641ms → 1803ms) | bug |
| [#3](https://github.com/MBrekhof/mcpRoslyn/issues/3) | `find_implementations` 8.4x regression (321ms → 2682ms) — **highest priority** | bug |
| [#4](https://github.com/MBrekhof/mcpRoslyn/issues/4) | `find_references` returns inconsistent counts across calls (35 vs 34, same symbol) | bug |

**Recommended order of attack:** #3 first (biggest absolute hit), then #2 (likely shares a root cause), then #1 (architectural — InvocationIndex laziness), then #4 (a unit test will likely surface the bug fast). All four are tractable; none is catastrophic on the existing code paths.

## Two additional observations (not filed — context, not bugs)

- **`find_dead_code_candidates` false-positives on Blazor `.razor.cs` files.** Event handlers and `[Inject]` properties get flagged because mcpRoslyn doesn't see Razor markup. Worth either skipping classes deriving from `Microsoft.AspNetCore.Components.ComponentBase` or down-weighting them. Mention in #1 follow-up planning if you tackle the Blazor source-generator integration later.
- **Baseline duetGPT carries 622 diagnostics including hard errors** (`CS0234`, `CS0246`, `CS0103`, `CS0115`, `CS0117`, `CS0120`). All trace back to mcpRoslyn not running Blazor source generators, so `App`, `StateHasChanged`, `Pages` namespace, `DbContext.Documents`, and related symbols look missing. **Not a v1.3 regression** — same in v1.2. Long-term fix is deeper MSBuild integration; documented as known limitation.

## What's next when you return

1. **Tag `v1.3.0` on main** if you want it addressable: `git tag -a v1.3.0 33d8ad4 -m "v1.3 — 7 new tools, format-summary, filter knobs" && git push origin v1.3.0`. (Not done by this session — wasn't asked, but it's a one-liner.)
2. **Pick up issue #3** (`find_implementations` 8.4x regression) — biggest user-visible win.
3. **Optionally delete** `feat/v1.3-feature-expansion` from origin once issues #1–#4 stop referencing it.

## Known limitations / gotchas (unchanged)

- **Windows-only.** `MSBuildLocator` and path-comparison code aren't portable yet.
- **Project-file changes need explicit `reload_workspace`.** Per-call mtime refresh only walks already-known documents. Same for the index — new symbols in new files won't appear until reload.
- **Stderr capture window** of Claude Code is no longer a problem; use `--log-file <path>`.
- **`duetGPT.LicenseServer` silent drop** is no longer invisible — check `reload_workspace`'s `Diagnostics` field next time you're in a duetGPT session.
- **mcpRoslyn doesn't trigger Blazor / Razor source generators.** Any analysis of `.razor.cs` files or types only emitted by the Razor compiler (`App`, generated partial classes) will be incomplete.

## Useful commands

```powershell
# Build
dotnet build mcpRoslyn.slnx -c Release

# Run all tests
dotnet test mcpRoslyn.slnx -c Release

# Run a targeted test class
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~SymbolIndexTests" -c Release
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~InvocationIndexTests" -c Release
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~FindDeadCodeCandidatesToolTests" -c Release

# Re-publish the exe (close any running mcpRoslyn.exe first)
dotnet publish src/mcpRoslyn -c Release -o bin/publish

# Run with persistent logging (captures warm-up + index-build timings)
mcpRoslyn.exe --log-file c:\users\marti\.claude\debug\mcpRoslyn.log
```

**Note:** solution file is `mcpRoslyn.slnx` (XML format), not `mcpRoslyn.sln`.

## Reference

- Acceptance report: [`docs/acceptance/2026-05-21-v1.3-acceptance.md`](docs/acceptance/2026-05-21-v1.3-acceptance.md)
- Architecture summary: [`ARCHITECTURE.md`](ARCHITECTURE.md) (includes `InvocationIndex` section and 19-tool surface table)
- Open work: [`TODO.md`](TODO.md) — v1.3 items all closed; nice-to-haves remain
- v1 design + plan + acceptance: `docs/plans/2026-05-15-*.md`, `docs/acceptance/2026-05-15-v1-acceptance.md`
- v1.1 warm-up: `docs/plans/2026-05-16-warmup-precompilation-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`
- v1.2 SymbolIndex: `docs/plans/2026-05-16-attribute-index-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`
- v1.3 feature-expansion: `docs/plans/2026-05-20-v1.3-feature-expansion-{design,implementation}.md`
