# Session Handoff

**Last updated:** 2026-06-15 (issue #1 InvocationIndex warm-up perf fix committed + pushed)

## Where things stand

- **`main` HEAD is `3313efc`** (InvocationIndex DI-verb gate, issue #1) — committed and pushed to origin.
- **v1.3 IS LIVE ON MAIN and TAGGED.** `v1.3.0` annotated tag points at merge commit `33d8ad4`. Post-tag patches (dedup fixes, downward discovery, the #1 perf fix) sit on top of the tag.
- **Published exe is STALE re: the #1 fix.** `bin/publish/mcpRoslyn.exe` was last rebuilt 2026-06-08 (carries downward discovery, NOT the #1 perf fix). **Republish needed** for live sessions to pick up the faster warm-up: `dotnet publish src/mcpRoslyn -c Release -o bin/publish` (stop running mcpRoslyn.exe instances first — they hold the file lock).
- Branch `feat/v1.3-feature-expansion` still exists on origin; safe to delete (no open issue references it).
- Working tree on main is clean.
- **Tests:** 111 passing. 1 acceptance test `[Explicit]` (not run by default).
- **Acceptance verdict: PASS-WITH-FOLLOWUPS.** Full report at `docs/acceptance/2026-05-21-v1.3-acceptance.md`.

## What the 2026-06-15 session did (issue #1)

**Fixed the InvocationIndex warm-up cost** (commit `3313efc`). Root-caused with measurement, not inference:

- `InvocationIndex.IndexDocument`'s cascade ended in an **ungated** `else if (IsServiceCollectionCall(...))`, which runs `GetSymbolInfo(inv)` (full overload-resolution bind) on every invocation that missed the cheap name checks. On duetGPT: **99,734 of 101,743 invocations (98%)** hit that bind — purely to find the few unrecognized `IServiceCollection` extension calls for the `Unclassified` bucket. `WalkTypes(GlobalNamespace)` was a red herring (133 ms).
- **Fix:** gate the bind behind a cheap syntactic `LooksLikeDiVerb()` check (`Add*`/`TryAdd*`/`Configure*`/`PostConfigure*`/`Replace`/`Decorate`). Candidate binds drop **99,734 → 1,440**.
- **Verified, drift-cancelled interleaved cold measurement:** bind work fell from ~8–11s to **~1.45s (~6–7×)**. Residual ~1.45s is intrinsic — those 1,440 generic DI calls (`AddDbContext<T>`, DevExpress/EF) are genuinely expensive to bind.
- **Tried and reverted:** a receiver-type fast-path (`GetTypeInfo` on the receiver before `GetSymbolInfo`). Interleaved testing showed no measurable gain → reverted to keep the diff minimal.
- **Behavior preserved:** `FindRegistrationsToolTests` 5/5 — `AddCustomThing` → `Unclassified` still holds (existing test is the regression guard).
- **Methodology note:** raw single cold-process warm-up readings swung 5.5s↔8.8s with SymbolIndex also moving 2× on *no* code change. The machine is noisy — only the interleaved in-process A/B comparisons were trusted. Same lesson as the v1.3 acceptance: compare like-for-like, never trust a single cold run.

## Open follow-ups for #1

- **Republish the exe** (see above) so live sessions get the faster warm-up.
- **Comment/close issue #1** referencing commit `3313efc`. Not yet done this session.
- **SymbolIndex's separate 6–10s warm-up is out of scope for #1** — different mechanism (walks all declared symbols + `ToSymbolInfo`/`GetAttributes`, no per-invocation binding). Worth its own issue if warm-up is still the dominant first-call latency after the exe republish.

## What the 2026-06-08 session did

1. **Merged `fix/downward-solution-discovery` into `main`** (fast-forward to `07eebad`). The change extracts the inline `DiscoverSolution` from `Program.cs` into a testable `SolutionDiscovery` class and adds breadth-first **downward** search (skips `bin`/`obj`/`node_modules`/`packages` + dot-dirs, ignores symlinks/junctions, depth cap 8) for when no `.sln`/`.slnx` is found walking up. 4 new `SolutionDiscoveryTests`.
2. **Verified 111 tests pass** on the merged commit, then deleted the merged branch.
3. **Pushed `main` to origin.** Required switching the active `gh` account from `MartinWLN` (no push access → 403) to `MBrekhof`. `MBrekhof` is now the active account.
4. **Cut and pushed `v1.3.0`** annotated tag at `33d8ad4`.
5. **Republished `bin/publish/mcpRoslyn.exe`** (stopped 2 running instances first to free the file lock).

## What the 2026-05-21 session did

1. **Investigated #3 (`find_implementations` 8.4x regression).** Closed as not-a-bug — methodology error. The v1.2 baseline measured `IBuiltInToolProvider` in-process; the v1.3 acceptance measured `IKnowledgeService` via the Claude Code→exe path on a cold first-call. Apples-to-apples re-measurement on v1.3 head: 288 ms vs v1.2's 321 ms baseline.
2. **Investigated #2 (`find_references` 2.8x regression).** Closed as not-a-bug — same root cause as #3 (different symbol, different transport). Apples-to-apples: 579 ms vs v1.2's 641 ms baseline.
3. **Investigated #4 (`find_references` count drift 35 vs 34).** Could not reproduce in-process — both calls return 34 consistently. Found a real correctness gap regardless: neither `find_references` nor `find_implementations` deduplicated location tuples. Shipped defensive dedup + contract tests for both. Closed.
4. **Posted full investigation comments** on all three issues; closed all three.

## Open follow-ups (GitHub issues)

| # | Title | Status |
|---|---|---|
| [#1](https://github.com/MBrekhof/mcpRoslyn/issues/1) | InvocationIndex warm-up cost ~30x over predicted budget (~13s vs +400ms) | FIX COMMITTED (`3313efc`) — DI-verb gate cut bind work ~6–7× (~8–11s → ~1.45s). Pending: exe republish + issue close |
| [#2](https://github.com/MBrekhof/mcpRoslyn/issues/2) | `find_references` cold-cache 2.8x regression | CLOSED (methodology error) |
| [#3](https://github.com/MBrekhof/mcpRoslyn/issues/3) | `find_implementations` 8.4x regression | CLOSED (methodology error) |
| [#4](https://github.com/MBrekhof/mcpRoslyn/issues/4) | `find_references` returns inconsistent counts | CLOSED (defensive dedup shipped) |

## Two additional observations (not filed — context, not bugs)

- **`find_dead_code_candidates` false-positives on Blazor `.razor.cs` files.** Event handlers and `[Inject]` properties get flagged because mcpRoslyn doesn't see Razor markup. Worth either skipping classes deriving from `Microsoft.AspNetCore.Components.ComponentBase` or down-weighting them.
- **Baseline duetGPT carries 622 diagnostics including hard errors** (`CS0234`, `CS0246`, `CS0103`, `CS0115`, `CS0117`, `CS0120`). All trace back to mcpRoslyn not running Blazor source generators. **Not a v1.3 regression** — same in v1.2. Long-term fix is deeper MSBuild integration; documented as known limitation.

## Methodology lesson from this session

The v1.3 acceptance compared v1.2 in-process timings against v1.3 published-exe timings AND switched test symbols between the two runs — three confounds at once. Resulted in two false-positive "regressions" (#2 and #3) that took an investigation session to close. **Fix locked in:** `AcceptanceTests.Acceptance_against_duetGPT` now measures both `IBuiltInToolProvider` (v1.2 baseline) and `IKnowledgeService` (v1.3 baseline) back-to-back in-process — so any future acceptance run that wants apples-to-apples timings can read both numbers off the same harness. Always compare like-for-like: same symbol, same transport, same call position in the sequence.

## What's next when you return

1. **Finish closing out #1:** republish the exe (`dotnet publish src/mcpRoslyn -c Release -o bin/publish`, stop running instances first) and comment/close issue #1 referencing `3313efc`. Then re-check warm-up against duetGPT with `--log-file` to confirm the live `Invocation index built in X ms` line dropped.
2. **Consider filing a SymbolIndex warm-up issue** (separate 6–10s cost, different mechanism — see #1 follow-ups above) if warm-up is still the dominant first-call latency after the republish.
3. **Optionally delete** `feat/v1.3-feature-expansion` from origin — no open issues reference it anymore.
4. **`gh` account gotcha:** active account is `MBrekhof` (has push access to this repo). If a push 403s, run `gh auth switch --user MBrekhof` — `MartinWLN` can't push here.

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
