# Session Handoff

**Last updated:** 2026-08-02 (issue #1 closed out, ContextBoard wired up, TEST-001/002 shipped)

## Where things stand

- **`main` HEAD is `104331e`** (TEST-001/TEST-002) — committed and pushed to origin. Four commits this session: `b088afe` (#1 close-out + measurement), `d698555` (board wiring), `759e89d` (AREA-NNN prefixes), `104331e` (the two test fixes).
- **v1.3 IS LIVE ON MAIN and TAGGED.** `v1.3.0` annotated tag points at merge commit `33d8ad4`. Post-tag patches (dedup fixes, downward discovery, the #1 perf fix) sit on top of the tag.
- **Published exe is CURRENT.** Republished 2026-08-02 22:09 — `bin/publish/mcpRoslyn.exe` now carries the #1 perf fix. (Republish command: `dotnet publish src/mcpRoslyn -c Release -o bin/publish`; stop running mcpRoslyn.exe instances first — they hold the file lock.)
- **Issue #1 is CLOSED.** Commented with root cause + live measurement.
- Branch `feat/v1.3-feature-expansion` no longer exists on origin (delete attempt returned "remote ref does not exist"). Nothing to clean up.
- Working tree on main is clean.

## What the 2026-08-02 session did

Finished the #1 close-out that the previous session left pending.

1. **Republished the exe** (killed the running instance holding the lock first). Verified timestamp.
2. **Commented on and closed issue #1**, then **filed [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5)** for SymbolIndex.
3. **Measured the live warm-up** — and found the end-to-end gain is smaller than the issue's headline implied.

**The 6–7× in `3313efc`'s commit message is bind work only, not the log line.** `Invocation index built in X ms` times all of `BuildAsync`: syntax root + `DescendantNodes()` per document, a semantic model per document, and `WalkTypes(GlobalNamespace)`. The gate touches none of that. Three cold runs against duetGPT, using **SymbolIndex as a control** (unaffected by the fix, builds sequentially just before it):

| run | solution load | warm-up | symbol index (control) | invocation index |
|---|---|---|---|---|
| 1 | 16471 ms | 19266 ms | 16215 ms | 9772 ms |
| 2 | 2418 ms | 11384 ms | 7276 ms | 4764 ms |
| 3 | 2337 ms | 11175 ms | 7170 ms | 7323 ms |

- **Run 1 is a cold-everything outlier** taken right after `dotnet publish` — 16.5 s solution load vs ~2.4 s, control 2× the others. Discard it. Had I stopped at run 1 I would have wrongly concluded the fix barely worked.
- **Steady state ~4.8–7.3 s vs ~13 s pre-fix ≈ 2× end-to-end**, not 6–7×. Reconciles with bind-only: ~3.3 s non-bind + ~1.45 s residual bind ≈ 4.8 s.
- **Raw readings are unreliable to ±35% even at constant machine speed.** Runs 2 and 3 have controls 1.5% apart (7276/7170 ms) but invocation times 1.5× apart (4764/7323 ms). **Use the control ratio, not raw ms.** This is the sharpened version of the previous session's noise lesson: a sibling-index control catches drift that repeated cold runs alone do not.

**Method note that generalizes:** the two indexes build sequentially in `WorkspaceService.LoadUnsafeAsync`, which is what makes SymbolIndex a valid control — same machine state, same moment, independent of the change. Look for an in-process control like this before trusting any timing on this machine.

### ContextBoard wiring (this repo was never actually syncing)

The board project existed with **zero cards**, so the sync hook had been a silent no-op for the life of the repo: sync only touches cards a file cites via `(ID: nnnn)`, and nothing here cited one. Minted **17 cards** for the open backlog, cited them, and gave them `AREA-NNN` prefixes matching ContextBoard's own convention — PERF, IDX, TOOL, WS, TEST, DIST, ARCH, VAL.

**The trap, hit for real:** a cited checkbox's card body comes from **indented lines beneath it**, not the checkbox line. The parser treats the checkbox line as a board-owned title and discards it; `FileSyncService` then assigns the parsed body **unconditionally**, so a cited one-liner writes `null` straight over the card. The first sync pass blanked all 17 bodies I'd just written; re-indenting the file and re-syncing restored them. TODO.md now carries a note about this at the top — **keep the indentation when editing**. Filed against ContextBoard as **SYNC-016** (card 1187) with repro and suggested fixes.

Two operational notes: prefixes were set on the **board** (`update_card`), since the board owns Title and sync never writes it — the TODO.md copies are for humans only. And the sync hook exits 0 silently on success *and* on most failures, so verify with `get_card` rather than trusting it. Run it manually with `CLAUDE_PROJECT_DIR="C:\Projects\mcpRoslyn" powershell -NoProfile -ExecutionPolicy Bypass -File C:/Projects/ContextBoard/hooks/sync-files.ps1 < /dev/null` — without that env var it exits at line 34 having done nothing.

### TEST-001 / TEST-002 shipped (`104331e`)

- **TEST-001** — `Skipped_counters_report_publicMembers_and_tests` asserted only `Skipped.Should().NotBeNull()`, with the real assertion commented out as "omit if fragile"; it passed even with both counters at zero. Now asserts both counters are non-zero **and** that what they claim to exclude is absent from `Candidates` (no `Public`/`Protected`, nothing from TestTests). **Verified non-vacuous by inverting each assertion to `Be(0)` and confirming failure** — fixture yields `PublicMembers` 148, `Tests` 6. Exact counts left unasserted; the original brittleness worry was right, the fix was to pick a robust assertion, not drop it.
- **TEST-002** — comment quoted `$"Hello, {name}!"`; fixture has read `$"Hello, {name.Trim()}!"` since Task 6. The column-19 arithmetic in the same comment was re-checked against the fixture and is correct.

- **Tests:** 111 passing, 0 failing (full suite run on `104331e`). 1 acceptance test `[Explicit]` (not run by default).
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

All closed out on 2026-08-02: exe republished, issue commented + closed, SymbolIndex filed as **[#5](https://github.com/MBrekhof/mcpRoslyn/issues/5)** (~7.2 s, ~30% of the ~23–26 s time-to-ready; very consistent run-to-run, unlike InvocationIndex). #5 suggests measuring first — split per-symbol cost across `GetAttributes`/`ToSymbolInfo`/enumeration — since #1's first suspect (`WalkTypes`) was a 133 ms red herring. It also notes the two indexes build sequentially, so overlapping them is a possible cheap win worth measuring.

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
| [#1](https://github.com/MBrekhof/mcpRoslyn/issues/1) | InvocationIndex warm-up cost ~30x over predicted budget (~13s vs +400ms) | CLOSED (`3313efc`) — DI-verb gate. Bind work ~6–7× (~8–11s → ~1.45s); **live end-to-end ~2×** (~13s → ~4.8–7.3s). Exe republished 2026-08-02 |
| [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5) | SymbolIndex warm-up ~7.2s now the largest index cost | OPEN — filed 2026-08-02 with measurements |
| [#2](https://github.com/MBrekhof/mcpRoslyn/issues/2) | `find_references` cold-cache 2.8x regression | CLOSED (methodology error) |
| [#3](https://github.com/MBrekhof/mcpRoslyn/issues/3) | `find_implementations` 8.4x regression | CLOSED (methodology error) |
| [#4](https://github.com/MBrekhof/mcpRoslyn/issues/4) | `find_references` returns inconsistent counts | CLOSED (defensive dedup shipped) |

## Two additional observations (not filed — context, not bugs)

- **`find_dead_code_candidates` false-positives on Blazor `.razor.cs` files.** Event handlers and `[Inject]` properties get flagged because mcpRoslyn doesn't see Razor markup. Worth either skipping classes deriving from `Microsoft.AspNetCore.Components.ComponentBase` or down-weighting them.
- **Baseline duetGPT carries 622 diagnostics including hard errors** (`CS0234`, `CS0246`, `CS0103`, `CS0115`, `CS0117`, `CS0120`). All trace back to mcpRoslyn not running Blazor source generators. **Not a v1.3 regression** — same in v1.2. Long-term fix is deeper MSBuild integration; documented as known limitation.

## Methodology lesson from this session

The v1.3 acceptance compared v1.2 in-process timings against v1.3 published-exe timings AND switched test symbols between the two runs — three confounds at once. Resulted in two false-positive "regressions" (#2 and #3) that took an investigation session to close. **Fix locked in:** `AcceptanceTests.Acceptance_against_duetGPT` now measures both `IBuiltInToolProvider` (v1.2 baseline) and `IKnowledgeService` (v1.3 baseline) back-to-back in-process — so any future acceptance run that wants apples-to-apples timings can read both numbers off the same harness. Always compare like-for-like: same symbol, same transport, same call position in the sequence.

## What's next when you return

**Everything is in Backlog except the two closed TEST cards** — the board was deliberately cleared to Backlog, so pick one and move it to Todo rather than assuming a queue exists.

1. **VAL-001 (card 1171) — real-session validation.** Recommended first, because **three other cards are explicitly gated on it** and can't be answered without it: TOOL-004 (*"if duetGPT acceptance shows agents want both visible"*), TOOL-005 (*"add based on observed gaps in real sessions"*), DIST-002 (*"re-evaluate once session data shows…"*). Doing it first either unblocks those with evidence or kills them. Needs a real duetGPT session with the MCP server connected, and its deliverable is written findings, not code — worth deciding the output shape up front so it doesn't drift into unstructured poking. Consider recording those three as real `add_dependency` links so the ordering isn't buried in card prose.
2. **PERF-001 (card 1170) — SymbolIndex warm-up (~7.2 s), GitHub [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5).** The only open GitHub issue. Deliberately *not* recommended first: it's ~7.2 s of a ~24 s once-per-session cost, so even a 2× win saves ~3.6 s, and we don't yet know whether startup latency is what actually hurts in practice — VAL-001 is what would tell us. Measure before optimizing.
3. **WS-002 (card 1173)** is the best quick win at ~0.5 h: removes the CS0618 `WorkspaceFailed` warning that fires on every single build.
4. Remaining v1.4 items are all carded in Backlog with bodies — see the board or `TODO.md`.
4. **`gh` account gotcha:** active account is `MBrekhof` (has push access to this repo). If a push 403s, run `gh auth switch --user MBrekhof` — `MartinWLN` can't push here.
5. **Republish the exe after any src change** — live Claude Code sessions run `bin/publish/mcpRoslyn.exe`, not your build output, so a fix is invisible to them until republished. Stop running instances first (they hold the lock); this also drops the MCP server out of the current session until it restarts.

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
- Open work: the **ContextBoard** project `mcpRoslyn` (17 cards, all Backlog bar the two closed TEST ones) — [`TODO.md`](TODO.md) mirrors it and cites each card id
- v1 design + plan + acceptance: `docs/plans/2026-05-15-*.md`, `docs/acceptance/2026-05-15-v1-acceptance.md`
- v1.1 warm-up: `docs/plans/2026-05-16-warmup-precompilation-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`
- v1.2 SymbolIndex: `docs/plans/2026-05-16-attribute-index-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`
- v1.3 feature-expansion: `docs/plans/2026-05-20-v1.3-feature-expansion-{design,implementation}.md`
