# TODO — mcpRoslyn

v1 is shipped and accepted (see [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)). v1.1 warm-up shipped (see [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)). v1.3 feature-expansion shipped to main (111 tests). v1.3 acceptance follow-ups: #1/#2/#3/#4 all closed. Only open issue is [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5) (SymbolIndex warm-up ~7.2 s).

**ContextBoard sync:** open items cite their card as `(ID: nnnn)` on the checkbox line, with the detail on **indented lines beneath**. That indentation is load-bearing — the checkbox line is treated as a board-owned title and is discarded, while the indented block becomes the card body. A cited one-liner with no indented block syncs an *empty* body and wipes whatever the card had.

## v1.1 follow-ups (from acceptance log)

- [x] ~~**Expose MSBuild workspace warnings.**~~ Shipped (commit `417e86b`). `WorkspaceFailed` events now accumulate on `IWorkspaceService.Diagnostics` (cleared per load/reload) and are surfaced on `reload_workspace` output as `WorkspaceLoadDiagnostic[]`.
- [x] ~~**Warm-up / pre-compilation on load.**~~ Shipped. First-query `find_references` on duetGPT dropped from 8 400 ms to 1 874 ms (4.5×).
- [x] ~~**`semantic_search` attribute walk is O(symbols).**~~ Shipped as v1.2. New `SymbolIndex` (built in parallel during warm-up) backs `has-attribute:` / `returns:` / `parameter-type:` with O(matches) lookups. Always-fresh semantics preserved via per-query dirty-doc walk (the mtime-refresh in `GetFreshSolutionAsync` calls `MarkDirty`; queries filter out cached entries whose declaring docs intersect the dirty set, then walk just the dirty docs and merge). Design: [`docs/plans/2026-05-16-attribute-index-design.md`](docs/plans/2026-05-16-attribute-index-design.md). Plan: [`docs/plans/2026-05-16-attribute-index-implementation.md`](docs/plans/2026-05-16-attribute-index-implementation.md).
- [x] ~~**`workspace_symbol` lookup hint when `find_callers` gets `SYMBOL_NOT_FOUND`.**~~ Shipped (commit `f938fb0`). Both the symbolId and cursor-position failure paths now carry contextual `hint` fields.
- [x] ~~**Project-count mismatch diagnostic.**~~ Shipped as part of the diagnostics-surface work (`417e86b`). The "X of 5 loaded, here's the failure list" answer is now derivable from `reload_workspace`'s output. Did not implement an explicit declared-vs-loaded numeric comparison — would require parsing `.sln`/`.slnx` formats; the diagnostics list carries the same information without that risk.
- [x] ~~**`--log-file <path>` flag.**~~ Shipped (commit `421cc8f`). Append-mode file logging via a custom `ILoggerProvider`; closes the "Claude Code only captures stderr until `initialize`" diagnostic gap.

## Performance

- [ ] **PERF-001: SymbolIndex warm-up ~7.2 s is the largest index cost.** (ID: 1170)
  GitHub [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5). After #1 cut `InvocationIndex` to ~4.8–7.3 s, `SymbolIndex` (~7.2 s) is the biggest single index slice of the ~23–26 s time-to-ready, and unlike `InvocationIndex` it is very consistent run-to-run.

  Different mechanism from #1: it walks all declared symbols calling `ToSymbolInfo`/`GetAttributes`, with no per-invocation overload-resolution bind, so #1's syntactic-gate fix does not transfer.

  Measure before optimizing — #1's first suspect (`WalkTypes`) turned out to be a 133 ms red herring. Split per-symbol cost across `GetAttributes` / `ToSymbolInfo` / enumeration and get a symbol count.

  Note `SymbolIndex` and `InvocationIndex` build **sequentially** in `WorkspaceService.LoadUnsafeAsync` over the same warmed compilations, so overlapping them is a possible cheap win independent of any per-symbol work — but it trades wall-clock for CPU contention, so measure rather than assume.

## Deferred from v1 design

- [ ] **DIST-001: `dotnet tool` packaging.** (ID: 1182)
  Revisit if/when mcpRoslyn needs to be installed outside the local machine. Needs a feed; not worth it for single-user.
- [ ] **DIST-002: HTTP/SSE transport.** (ID: 1183)
  Currently stdio only. Re-evaluate cold-start-cost vs. complexity once session data shows whether multiple Claude Code sessions on the same solution would benefit from sharing one workspace process.
- [ ] **DIST-003: Cross-platform (Linux/Mac).** (ID: 1184)
  Deferred until there's a real non-Windows user. `MSBuildLocator` and path-comparison code would both need attention.
- [ ] **TOOL-005: Wider `semantic_search` grammar.** (ID: 1185)
  Current 5 patterns (`derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) are a starting set. Add based on observed gaps in real sessions rather than speculatively.

  Reviewed 2026-08-15 while clearing the other TOOL cards and deliberately left closed. Nothing in the BPG work wanted
  a pattern that isn't there — the gaps that did show up were in `find_dead_code_candidates` (see TOOL-006), not in the
  search grammar. Reassess after VAL-001.
- [ ] **ARCH-001: `ISymbolProvider` abstraction.** (ID: 1186)
  If we ever wrap gopls/pyright/rust-analyzer, factor `WorkspaceService` behind a more abstract provider interface. Don't build it speculatively — one implementation needs no interface.

## Nice-to-haves spotted along the way

- [ ] **WS-003: Extract project name from `WorkspaceLoadDiagnostic.Message`.** (ID: 1174)
  Currently the DTO is `{ Kind, Message }`; the project filename is embedded in the message text. Adding a `ProjectName: string?` field (regex-extracted from the message) would make filtering/grouping easier for tool callers. Small, safe.
- [x] ~~**WS-002: Fix the `Workspace.WorkspaceFailed` obsolete warning.**~~ (ID: 1173)
  Done 2026-08-15. Migrated to `RegisterWorkspaceFailedHandler(Action<WorkspaceDiagnosticEventArgs>)`; the src project
  now builds with **0 warnings**. The risk with this change is that it compiles clean and silently never fires, so it
  was checked behaviourally, not just by the warning disappearing: `LoadAsync_broken_solution_captures_diagnostics`
  still captures diagnostics, and a new test loads a solution naming a `.esproj` and asserts the diagnostic arrives
  classified as `SkippedUnsupportedProject` (which also gives TOOL-006's `.esproj` change its first in-repo coverage).
- [ ] **WS-001: Investigate `duetGPT.LicenseServer` silent drop.** (ID: 1172)
  v2.0 of duetGPT's .sln declares 5 projects; MSBuildWorkspace consistently loads 4. `duetGPT.LicenseServer` is filtered out *before* MSBuild raises a `WorkspaceFailed` event, so the v1.1 diagnostics-surfacing work (`WorkspaceLoadDiagnostic`) doesn't catch it — verified in v1.2 acceptance ([`docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`](docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md)).

  Likely an SDK / target-framework / project-type filter applied at workspace open. Start by inspecting that project's .csproj for `<Sdk>` reference / target framework / project type GUID, then check Roslyn's `MSBuildWorkspace.OpenSolutionAsync` source for what it skips silently. May need a separate `list_solution_projects` tool that reads the .sln/.slnx directly to surface declared-but-unloaded entries.
- [x] ~~**Re-measure `find_implementations` on duetGPT.**~~ Resolved in v1.2 acceptance ([`docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`](docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md)): 321 ms in v1.2, matches v1 (~300 ms). v1.1's 832 ms was sampling noise.

## v1.3 items (all closed)

- [x] ~~**`project_overview` tool.**~~ Shipped (`21626c1`/`081c45c`). Solution structure: projects, package refs, project refs. `TargetFramework` is always null (see nice-to-haves below).
- [x] ~~**`find_entrypoints` tool.**~~ Shipped (`bf7d887`). ASP.NET routes / middleware / hosted services via `InvocationIndex`.
- [x] ~~**`find_registrations` tool.**~~ Shipped (`f85bd95`). DI registrations + likely consumers via `InvocationIndex`.
- [x] ~~**`find_callees` tool.**~~ Shipped (`7d8821d`). Outgoing call detection, mirror of `find_callers`.
- [x] ~~**`analyze_symbol` composite tool.**~~ Shipped (`8feae51`). Hover + refs + impls + derived + callers in one call.
- [x] ~~**`test_map` tool.**~~ Shipped (`d91f4dc`). Production → test heuristic.
- [x] ~~**`find_dead_code_candidates` tool.**~~ Shipped (`d146f6f`). Private/internal members with confidence scoring + denylist + `InternalsVisibleTo` handling.
- [x] ~~**`format` parameter on all tools.**~~ Shipped (`bf8f727`). `structured` (default) | `summary`. `ToolResult<T>` carries optional `Summary` field. Backwards-compatible.
- [x] ~~**Diagnostics filter knobs.**~~ Shipped (`caa0679`/`6ce66a0`). `includeGenerated`, `minimumSeverity`, `excludeDiagnosticCodes`, `excludeDiagnosticSources` on `get_compilation_errors` and `get_document_diagnostics`.

## Nice-to-haves spotted during v1.3

- [x] ~~**IDX-001: `SymbolIndex.AllSymbols()` not in dirty-walk.**~~ (ID: 1178)
  Done 2026-08-15. `AllSymbols` now takes the current `Solution` and goes through the same `MergeWithDirtyWalk` the
  pattern queries use, so a symbol added after the index was built is visible without an explicit `reload_workspace`.
  `MergeWithDirtyWalk` returns `IndexedSymbol` instead of `SymbolInfo` (the three `Query*` methods project `.Info`),
  which keeps it as the single merge path rather than a copy of it for the flat enumeration.

  This also puts de-duplication in one place. The build indexes a symbol once per *referencing* project, because a
  project reference pulls the referenced project's source symbols into the referencing compilation; the merge already
  de-duplicated by symbol id for the pattern queries, and the flat enumeration now gets that for free. The temporary
  de-dup added in `find_dead_code_candidates` for TOOL-006 was removed in favour of it — its regression test still
  fails if the merge is bypassed.
- [x] ~~**TOOL-003: `find_dead_code_candidates` `Skipped` counters truncate when `maxResults` hits.**~~ (ID: 1177)
  Done 2026-08-15. The scan used to `break` at `maxResults`, so the counters described only the prefix it had walked.
  It now keeps classifying and stops only the expensive reference scans, and the result carries a new `Truncated: bool`
  so a caller can tell a complete sweep from a capped one. Cost was the worry and it was unfounded: a complete scan of
  `BPG.sln` runs in ~1.5–2.5 s. Regression test compares `maxResults: 1` against `maxResults: 1000` and requires every
  counter to match.
- [x] ~~**TOOL-002: `find_registrations` consumer detection over-broad.**~~ (ID: 1176)
  Done 2026-08-15. `LikelyConsumers` now lists constructors only — `ISymbol.Name == ".ctor"`, which also covers primary
  constructors. Any method with a parameter of the service type used to qualify. New fixture `TestWeb/FooHelper.cs`
  declares `Use(IFoo)` as a plain method; the test asserts it is absent while `BarController` is still listed.
- [x] ~~**TOOL-001: `project_overview.TargetFramework` is always `null`.**~~ (ID: 1175)
  Done 2026-08-15. `project_overview` reads `<TargetFramework>` (falling back to `<TargetFrameworks>`) and
  `<IsPackable>` from the .csproj it already parses for package references — one `XDocument.Load` instead of two.
  A multi-targeted project takes its TFM from the `Foo(net8.0)` suffix MSBuildWorkspace puts on the project name,
  which is the only place a per-`Project` TFM exists. A value still holding an MSBuild variable reports as `null`
  rather than being echoed back as if it were a framework name; resolving those means evaluating MSBuild, which no
  real solution has yet needed. Verified on `BPG.sln`: all 8 projects report `net10.0`, the two test projects report
  `IsPackable=false`, the rest `null` (the tool reports what the file says and does not guess SDK defaults).
- [ ] **TOOL-004: `find_entrypoints` hosted-service de-dup pivot.** (ID: 1181)
  Tool layer collapses "registered" + "subclass" entries for the same type. If duetGPT acceptance shows agents want both visible, expose a flag. Conditional on real-session feedback — don't build speculatively.

  Reviewed 2026-08-15 while clearing the other TOOL cards and deliberately left closed. The BPG work produced no case
  where the collapsed entry misled anyone, so there is still nothing to build against. Reassess after VAL-001.
- [x] ~~**TEST-002: `HoverToolTests.cs` line 19 stale comment.**~~ (ID: 1180) Done 2026-08-02. The comment quoted `$"Hello, {name}!"` while the fixture reads `$"Hello, {name.Trim()}!"`. The column-19 arithmetic in the same comment was re-checked and is correct.
- [x] ~~**TEST-001: Strengthen `find_dead_code_candidates` test 3.**~~ (ID: 1179) Done 2026-08-02. `Skipped_counters_report_publicMembers_and_tests` now asserts both counters are non-zero (fixture yields `PublicMembers` 148, `Tests` 6) and that what they claim to exclude is absent from `Candidates`. Verified non-vacuous by inverting each assertion to `Be(0)` and confirming it fails — the old `Should().NotBeNull()` passed even with both counters stuck at zero.

## Spotted in real-session use (BPG, 2026-08-15)

- [x] ~~**TOOL-006: `find_dead_code_candidates` misses unreferenced public types — the case that actually matters.**~~ (ID: 1309)
  Done 2026-08-15. Shipped as `includePublicTypes` (default `false`, so nothing changes for existing callers). When set,
  public **types** — never public members — are reported when they have no reference outside their own declaration.
  "Outside their own declaration" is span-level, not file-level, so two types sharing a file don't mask each other, and
  `BPGDbInitializer`'s self-reference (`ILogger<BPGDbInitializer>` inside its own body) doesn't count as use.

  Verified end-to-end against `C:\Projects\BPG` — the solution the card was filed from. Both known-dead classes,
  `CodeGenerationService` and `CodeGenerationServiceV2`, are now reported. The complete public sweep returns 11
  candidates: those two, six `Class1` template leftovers, `BPGDbInitializer` (confirmed dead by grep — its only mention
  is inside itself), and three BPG.Api types worth a look. No migrations, no controllers, no record plumbing.

  Four suppressions make that signal-to-noise possible, and three of them were only found by running against BPG:
  - **DI cross-reference**, as the card suggested — registrations, hosted services, and the raw text of `unclassified`
    calls. It suppressed 45 types on BPG. One trap: matching raw call text by substring is wrong, because
    `CodeGenerationService` occurs inside every mention of `ICodeGenerationService` — the registered interface would
    have exonerated the dead class named after it, defeating the card's own headline case. Matching is whole-identifier.
  - **Compiler-generated members are now skipped entirely** (`ISymbol.IsImplicitlyDeclared`). This is the direct fix for
    the card's complaint that every result was record plumbing: `EqualityContract`, `PrintMembers`, copy-constructors,
    backing fields. On BPG that is 715 members that no one can delete.
  - **Framework-reached types**: `Controller`/`Hub`/`Middleware`/`Migration`/`Startup`/`Program` suffixes, plus static
    classes declaring extension methods (reached through the method, never the class). EF migrations are matched by
    `[Migration]`/`[DbContext]` instead — BPG's are named `AddMessageEmbeddings`, so no suffix rule would catch them.
  - **De-duplication by symbol id.** A project reference pulls the referenced project's source symbols into the
    referencing compilation, so `SymbolIndex` holds one entry per referencing project. Every candidate was reported
    once per referencing project and every `Skipped` counter was inflated — on BPG, `publicMembers` 17817 vs the real
    3113. This is the same class of bug as the v1.3 `find_references`/`find_implementations` dedup fix.

  Public-type findings are reported at **medium** confidence with reason
  `public-type-no-references-outside-own-declaration-and-not-di-registered`. The residual risk is unchanged: assembly
  scanning (Scrutor) and config-named types remain invisible to both a reference scan and the DI index.

  **`IsPackable` is reported, not acted on** (see TOOL-001). The card suggested skipping public types in packable
  projects; that would have broken the feature on the very solution it was filed from. BPG declares `IsPackable` only
  on its two test projects, so the libraries — including `BPG.CodeGeneration`, which holds both dead classes — default
  to packable and would all have been skipped. Surfacing the value and letting the caller decide is the honest version.

  Also done here: `project_overview`'s `.esproj` diagnostic no longer reads as a `Failure`. `WorkspaceService` classifies
  "…is not associated with a language" as kind `SkippedUnsupportedProject`, which is what a polyglot solution actually
  means. Confirmed on BPG's `bpg-frontend.esproj`.

  Original report (2026-08-15, BPG session): with `maxResults: 18, includeTests: false` every result was
  compiler-generated record plumbing or a private backing field, reported alongside `skipped: { publicMembers: 214 }`,
  while two entirely dead public classes — `CodeGenerationService` and `CodeGenerationServiceV2` — were invisible to the
  tool and were eventually found with `grep`, by accident, with `CodeGenerationServiceV2` still being edited as if live.
  Skipping the public surface is right for a packable library, where that surface is the product; it is wrong for an
  application solution, which is most of what this server gets pointed at.

## Real-session validation (still to do)

- [ ] **VAL-001: Use mcpRoslyn in one feature-sized duetGPT task.** (ID: 1171)
  Record: missing tools, wrong response shapes, cold-start friction. The acceptance logs cover correctness of canned queries; they do not cover end-to-end usefulness in an agent loop. Highest-value non-perf item.

  Partial data point already in hand from BPG (2026-08-15), see TOOL-006: `find_registrations` and `project_overview` both earned their keep — `find_registrations "Hangfire"` would have shown a bug that instead took a `dotnet-stack` dump on a hung process, and `project_overview` caught `BPG.Core` violating its own documented "dependency-free" rule on the first call. `find_dead_code_candidates` did not. The agent-loop friction worth recording: the tools were available all session and went unused until prompted, because grep is the reflex.
