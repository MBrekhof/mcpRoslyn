# Session Handoff

**Last updated:** 2026-05-21 (end of v1.3 acceptance follow-up: #2, #3, #4 closed)

## Where things stand

- **v1.3 IS LIVE ON MAIN.** Merge commit `33d8ad4`. Tag not yet cut.
- Branch `feat/v1.3-feature-expansion` still exists on origin; safe to delete now that #1 is the only open v1.3 follow-up.
- Working tree on main is clean. Three commits added today on top of the v1.3 merge: `d6b08ba` (paired acceptance symbols), `743d6e7` (find_references dedup + tests), `85a5420` (find_implementations dedup + tests).
- **Tests:** 107 passing (103 inherited + 4 new contract tests).
- **Acceptance verdict: PASS-WITH-FOLLOWUPS.** Full report at `docs/acceptance/2026-05-21-v1.3-acceptance.md`.

## What this session did

1. **Investigated #3 (`find_implementations` 8.4x regression).** Closed as not-a-bug — methodology error. The v1.2 baseline measured `IBuiltInToolProvider` in-process; the v1.3 acceptance measured `IKnowledgeService` via the Claude Code→exe path on a cold first-call. Apples-to-apples re-measurement on v1.3 head: 288 ms vs v1.2's 321 ms baseline.
2. **Investigated #2 (`find_references` 2.8x regression).** Closed as not-a-bug — same root cause as #3 (different symbol, different transport). Apples-to-apples: 579 ms vs v1.2's 641 ms baseline.
3. **Investigated #4 (`find_references` count drift 35 vs 34).** Could not reproduce in-process — both calls return 34 consistently. Found a real correctness gap regardless: neither `find_references` nor `find_implementations` deduplicated location tuples. Shipped defensive dedup + contract tests for both. Closed.
4. **Posted full investigation comments** on all three issues; closed all three.

## Open follow-ups (GitHub issues)

| # | Title | Status |
|---|---|---|
| [#1](https://github.com/MBrekhof/mcpRoslyn/issues/1) | InvocationIndex warm-up cost ~30x over predicted budget (~13s vs +400ms) | OPEN — environment-dependent; in-process total warm-up unchanged from v1.2 |
| [#2](https://github.com/MBrekhof/mcpRoslyn/issues/2) | `find_references` cold-cache 2.8x regression | CLOSED (methodology error) |
| [#3](https://github.com/MBrekhof/mcpRoslyn/issues/3) | `find_implementations` 8.4x regression | CLOSED (methodology error) |
| [#4](https://github.com/MBrekhof/mcpRoslyn/issues/4) | `find_references` returns inconsistent counts | CLOSED (defensive dedup shipped) |

## Two additional observations (not filed — context, not bugs)

- **`find_dead_code_candidates` false-positives on Blazor `.razor.cs` files.** Event handlers and `[Inject]` properties get flagged because mcpRoslyn doesn't see Razor markup. Worth either skipping classes deriving from `Microsoft.AspNetCore.Components.ComponentBase` or down-weighting them.
- **Baseline duetGPT carries 622 diagnostics including hard errors** (`CS0234`, `CS0246`, `CS0103`, `CS0115`, `CS0117`, `CS0120`). All trace back to mcpRoslyn not running Blazor source generators. **Not a v1.3 regression** — same in v1.2. Long-term fix is deeper MSBuild integration; documented as known limitation.

## Methodology lesson from this session

The v1.3 acceptance compared v1.2 in-process timings against v1.3 published-exe timings AND switched test symbols between the two runs — three confounds at once. Resulted in two false-positive "regressions" (#2 and #3) that took an investigation session to close. **Fix locked in:** `AcceptanceTests.Acceptance_against_duetGPT` now measures both `IBuiltInToolProvider` (v1.2 baseline) and `IKnowledgeService` (v1.3 baseline) back-to-back in-process — so any future acceptance run that wants apples-to-apples timings can read both numbers off the same harness. Always compare like-for-like: same symbol, same transport, same call position in the sequence.

## What's next when you return

1. **Tag `v1.3.0` on main** if you want it addressable: `git tag -a v1.3.0 33d8ad4 -m "v1.3 — 7 new tools, format-summary, filter knobs" && git push origin v1.3.0`.
2. **Pick up issue #1** (InvocationIndex warm-up cost) — only remaining v1.3 follow-up. The 13 s figure is from one published-exe run; in-process total warm-up is unchanged from v1.2 (22.3 s vs 22.8 s), so the cost may be environment-dependent. Worth instrumenting per-project to identify the slow project before architectural changes.
3. **Optionally delete** `feat/v1.3-feature-expansion` from origin — no open issues reference it anymore.

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
