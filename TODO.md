# TODO — mcpRoslyn

v1 is shipped and accepted (see [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)). v1.1 warm-up shipped (see [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)). v1.3 feature-expansion shipped to main. v1.3 acceptance follow-ups #1–#4 are closed, and [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5) (SymbolIndex warm-up) was closed by PERF-001. Every card from the 2026-09-12 Codex review is done; what remains open is deferred or blocked on VAL-001.

**ContextBoard sync:** open items cite their card as `(ID: nnnn)` on the checkbox line, with the detail on **indented lines beneath**. That indentation is load-bearing — the checkbox line is treated as a board-owned title and is discarded, while the indented block becomes the card body. A cited one-liner with no indented block syncs an *empty* body and wipes whatever the card had.

## v1.1 follow-ups (from acceptance log)

- [x] ~~**Expose MSBuild workspace warnings.**~~ Shipped (commit `417e86b`). `WorkspaceFailed` events now accumulate on `IWorkspaceService.Diagnostics` (cleared per load/reload) and are surfaced on `reload_workspace` output as `WorkspaceLoadDiagnostic[]`.
- [x] ~~**Warm-up / pre-compilation on load.**~~ Shipped. First-query `find_references` on duetGPT dropped from 8 400 ms to 1 874 ms (4.5×).
- [x] ~~**`semantic_search` attribute walk is O(symbols).**~~ Shipped as v1.2. New `SymbolIndex` (built in parallel during warm-up) backs `has-attribute:` / `returns:` / `parameter-type:` with O(matches) lookups. Always-fresh semantics preserved via per-query dirty-doc walk (the mtime-refresh in `GetFreshSolutionAsync` calls `MarkDirty`; queries filter out cached entries whose declaring docs intersect the dirty set, then walk just the dirty docs and merge). Design: [`docs/plans/2026-05-16-attribute-index-design.md`](docs/plans/2026-05-16-attribute-index-design.md). Plan: [`docs/plans/2026-05-16-attribute-index-implementation.md`](docs/plans/2026-05-16-attribute-index-implementation.md).
- [x] ~~**`workspace_symbol` lookup hint when `find_callers` gets `SYMBOL_NOT_FOUND`.**~~ Shipped (commit `f938fb0`). Both the symbolId and cursor-position failure paths now carry contextual `hint` fields.
- [x] ~~**Project-count mismatch diagnostic.**~~ Shipped as part of the diagnostics-surface work (`417e86b`). The "X of 5 loaded, here's the failure list" answer is now derivable from `reload_workspace`'s output. Did not implement an explicit declared-vs-loaded numeric comparison — would require parsing `.sln`/`.slnx` formats; the diagnostics list carries the same information without that risk.
- [x] ~~**`--log-file <path>` flag.**~~ Shipped (commit `421cc8f`). Append-mode file logging via a custom `ILoggerProvider`; closes the "Claude Code only captures stderr until `initialize`" diagnostic gap.

## Performance

- [x] ~~**PERF-001: SymbolIndex warm-up ~7.2 s is the largest index cost.**~~ (ID: 1170)
  Done 2026-08-15. GitHub [#5](https://github.com/MBrekhof/mcpRoslyn/issues/5). **One-line fix**: `WalkAllSymbols`
  started at `Compilation.GlobalNamespace`, which merges the source assembly with **every referenced assembly**, so each
  project walked the entire BCL and every package — only to throw the results away, since a symbol with no
  source-declaring document is dropped a few lines later. It now walks `compilation.Assembly.GlobalNamespace`.

  The card said measure first, and measuring is also what proves it loses nothing. Interleaved in-process A/B over the
  same warmed compilations, with `InvocationIndex` as the control (untouched by this change, so its drift calibrates
  the rest):

  | solution | `GlobalNamespace` | `Assembly.GlobalNamespace` | entries (both) | ratio to control |
  |---|---|---|---|---|
  | BPG | 2323 / 1852 ms | 117 / 96 ms | 4 910 | 2.45–4.00 → 0.07–0.12 |
  | duetGPT | 2214 ms | 76 / 82 ms | 12 620 | 0.83 → 0.03 |

  **Identical entry counts on both solutions** is the load-bearing number — the walk got ~20–27× cheaper without
  dropping a single symbol. Confirmed cold through the published exe: `Symbol index built in 128 ms` on BPG (8
  projects), `198 ms` on duetGPT. It is no longer a meaningful slice of time-to-ready.

  Two notes for whoever reads #5 next. The issue's headline "~7.2 s" was measured when `duetGPT.sln` declared 4–5
  projects; after the 2026-07-30 repo flatten it declares **1**, so that figure can't be reproduced as stated —
  BPG is the better benchmark now. And `InvocationIndex` is now clearly the dominant index cost (1715 ms on BPG,
  5993 ms on duetGPT), i.e. the residual bind work from #1 rather than anything in this card.

  Not done, and no longer worth doing for this reason: overlapping the two index builds. They still run sequentially in
  `WorkspaceService.LoadUnsafeAsync`, but with `SymbolIndex` at ~0.1 s there is nothing left to overlap.

## Deferred from v1 design

- [ ] **DIST-001: `dotnet tool` packaging.** (ID: 1182)
  Revisit if/when mcpRoslyn needs to be installed outside the local machine. Needs a feed; not worth it for single-user.
- [ ] **DIST-002: HTTP/SSE transport.** (ID: 1183)
  Currently stdio only. Re-evaluate cold-start-cost vs. complexity once session data shows whether multiple Claude Code sessions on the same solution would benefit from sharing one workspace process.

  **Reference impl (2026-08-21):** roslynk (`C:\Projects\roslynk`, MIT) is exactly this shape — streamable-HTTP
  daemon bound to loopback `:6502`, holding N solutions (`get_solution_status` is daemon-wide), and a `stdio` verb
  that is a self-launching bridge: the MCP client spawns it, it starts the daemon if none is listening, and pipes the
  session through. Clients keep the plain stdio registration, so the transport change is invisible to `.mcp.json`.
  Also cross-platform (DIST-003) and installable as a Windows service (`installer/`). The bridge-spawns-daemon
  pattern is the part worth copying; it removes the "who starts the daemon" question that made this card a deferral.
- [ ] **DIST-003: Cross-platform (Linux/Mac).** (ID: 1184)
  Deferred until there's a real non-Windows user. `MSBuildLocator` and path-comparison code would both need attention.
- [ ] **TOOL-005: Wider `semantic_search` grammar.** (ID: 1185)
  Current 5 patterns (`derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) are a starting set. Add based on observed gaps in real sessions rather than speculatively.

  Reviewed 2026-08-15 while clearing the other TOOL cards and deliberately left closed. Nothing in the BPG work wanted
  a pattern that isn't there — the gaps that did show up were in `find_dead_code_candidates` (see TOOL-006), not in the
  search grammar. Now formally blocked on VAL-001 as a board dependency rather than a note in prose.

  **Direction, from comparing against NDepend (2026-08-15):** their equivalent is CQLinq — one query language over a
  code model, not N fixed patterns. The transferable lesson is about the *shape of the answer*, not about copying
  CQLinq: when this card finally has evidence, if it names three or four missing patterns rather than one, the right
  response is probably a general query mechanism, not pattern six. One missing pattern still just means add the pattern.
- [ ] **ARCH-001: `ISymbolProvider` abstraction.** (ID: 1186)
  If we ever wrap gopls/pyright/rust-analyzer, factor `WorkspaceService` behind a more abstract provider interface. Don't build it speculatively — one implementation needs no interface.

## Nice-to-haves spotted along the way

- [x] ~~**WS-003: Extract project name from `WorkspaceLoadDiagnostic.Message`.**~~ (ID: 1174)
  Done 2026-08-15. The DTO is now `{ Kind, Message, ProjectName }`. MSBuild quotes the offending project's full path in
  both wordings we actually see — `Cannot open project '…\bpg-frontend.esproj' because…` and `Msbuild failed when
  processing the file '…\duetGPT.csproj' with message: …` — so one regex covers both; the name is returned without
  extension, matching `project_overview`'s `Name` so callers can join the two. Table-driven test uses both real
  messages plus a no-path case; the `.esproj` message also quotes the bare extension `'.esproj'`, which must not win
  over the full path, and the test pins that.
- [x] ~~**WS-002: Fix the `Workspace.WorkspaceFailed` obsolete warning.**~~ (ID: 1173)
  Done 2026-08-15. Migrated to `RegisterWorkspaceFailedHandler(Action<WorkspaceDiagnosticEventArgs>)`; the src project
  now builds with **0 warnings**. The risk with this change is that it compiles clean and silently never fires, so it
  was checked behaviourally, not just by the warning disappearing: `LoadAsync_broken_solution_captures_diagnostics`
  still captures diagnostics, and a new test loads a solution naming a `.esproj` and asserts the diagnostic arrives
  classified as `SkippedUnsupportedProject` (which also gives TOOL-006's `.esproj` change its first in-repo coverage).
- [x] ~~**WS-001: Investigate `duetGPT.LicenseServer` silent drop.**~~ (ID: 1172)
  Closed 2026-08-15 — **does not reproduce; the premise is gone.** The original observation was against
  `duetGPT\duetGPT.sln`, which after the 2026-07-30 repo flatten declares exactly **one** project. The live solution is
  now the repo root `C:\Projects\duetgpt\duetGPT.sln`, which declares 4, and mcpRoslyn loads **4 of 4** —
  `duetGPT.LicenseServer` among them, and it is in fact the first project to finish warming (2293 ms, 22 diagnostics).
  Its .csproj is unremarkable: `Microsoft.NET.Sdk.Web`, `net10.0`, four package references.

  So there is nothing left to investigate here, and no evidence for the suspected pre-`WorkspaceFailed` project filter.
  The speculative `list_solution_projects` tool is not worth building for a symptom that no longer exists; if a
  declared-but-unloaded project turns up again, reopen with the new solution as the repro.

  What the same run *did* surface is a different reporting problem, filed separately as WS-004.
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

## Spotted while closing WS-001 (2026-08-15)

- [x] ~~**WS-004: MSBuild non-fatal messages are reported at kind `Failure` on projects that load fine.**~~ (ID: 1310)
  Filed and fixed 2026-08-15. Loading `C:\Projects\duetgpt\duetGPT.sln` (4 projects, **all 4 loaded**) emitted three
  `Failure` diagnostics — two package-pruning suggestions on `duetGPT.csproj` and a vulnerability advisory on
  `duetGPT.Tests.csproj`. None stopped anything loading, but `Kind` was `Failure` and the text opens with "Msbuild
  failed", so the diagnostics list read as a broken solution when nothing was broken. Same readability problem
  TOOL-006 raised for `.esproj`.

  Cheap because WS-003 had just shipped `ProjectName`: a diagnostic naming a project that IS in `solution.Projects`
  demonstrably did not prevent it loading, so it is re-kinded `ProjectLoadedWithWarnings`. One naming an absent project
  keeps `Failure` — which is the honest signal for the declared-but-unloaded case WS-001 was originally chasing.
  Matching is on the `.csproj` file name rather than `Project.Name`, because a multi-targeted project is named
  `Foo(net8.0)` while the message quotes the path to `Foo.csproj`.

  It runs as a pass at the end of `LoadUnsafeAsync`, not inside the handler: diagnostics arrive during the load, before
  there is a project list to check them against. Verified end-to-end on the solution that produced the false failures —
  all three now report `ProjectLoadedWithWarnings` with the right `ProjectName`, with 4 projects loaded.

## From the NDepend comparison (2026-08-15)

NDepend open-sourced an [MCP server](https://github.com/ndepend/NDepend.MCP.Server) on 2026-02-26 (14 tools) and an
[AI Issue Fix](https://www.ndepend.com/docs/ai-issue-fix) feature on 2026-01-30, so it is now an agent-loop tool and
not only a human-report tool. Comparing the two surfaces produced three items worth keeping. **Its structural
advantage over us stays put:** it analyses build output, so it needs a green build and an analysis run, while
mcpRoslyn answers on a branch with compile errors on the floor.

- [x] ~~**DIAG-001: Surface Roslyn analyzer diagnostics, not just compiler diagnostics.**~~ (ID: 1311)
  Done 2026-09-13 (`5a64d28`). `get_document_diagnostics` runs the project's analyzers **by default**, scoped to one tree (syntax + semantic analyzer diagnostics); `get_compilation_errors` takes **`includeAnalyzers: true`**. The defaults come from BPG numbers, as this card asked: per file ~180-225 ms median with analyzers vs 5-9 ms compiler-only; solution-wide ~2.4 s vs ~45 ms (548 → 1216 diagnostics). So roslynk's default-on-with-cache wasn't needed per file, and solution-wide isn't affordable by default without a cache we don't have. Configured `.editorconfig` severities and suppressions apply — the fixture raises CA1822 (off by default) and pragma-suppresses a second copy.

  Three Codex review rounds added: AD0001 analyzer-failure reporting on the per-file path; a suppressor-only whole-compilation pass so `DiagnosticSuppressor`s (EF Core's CS8618-on-DbSet) hide compiler diagnostics per file, run only when the file has a diagnostic one claims; additional-location filtering in that pass; and a guard against a throwing `SupportedSuppressions` getter. Compilation-end rules don't run per file (documented in the tool description). Nothing cached, nothing in warm-up. `BenchmarkTests.Diagnostics_analyzer_cost` (manual) holds the measurement; design in ARCHITECTURE.md "Analyzer diagnostics (DIAG-001)".

  `get_compilation_errors` / `get_document_diagnostics` call `compilation.GetDiagnostics()` and
  `semantic.GetDiagnostics()` only — there is no `CompilationWithAnalyzers` in src. So `.editorconfig` severities,
  StyleCop, Roslynator, NetAnalyzers and any DevExpress/XAF analyzers are invisible. For an agent handing code back
  that is the difference between **"it compiles"** and **"it passes this project's own bar"**.

  Roslyn-native, nothing new to invent: `Project.AnalyzerReferences` is already populated by MSBuild and the
  compilations are already warmed. **But `CompilationWithAnalyzers` is expensive** — a large share of real build time —
  so scope it per-document and on-demand, opt-in, and keep it out of warm-up entirely. PERF-001 was precisely the
  lesson that unbounded startup work is the wrong trade, and `InvocationIndex` already costs ~12 s on duetGPT.
  Respect configured severities, or it will report rules the project has deliberately switched off.

  Soft-sequenced behind VAL-001, deliberately **not** a blocking dependency: the gap is real either way, but VAL-001
  would show how much it is worth.

  **Reference impl (2026-08-21):** roslynk (`C:\Projects\roslynk`, MIT) ships this as `get_diagnostics` —
  `includeAnalyzers` default **true**, `targetFramework` pins a multi-TFM project, results cached per
  `(targetFramework, includeAnalyzers)` and invalidated on any write; the header always carries all four counts so the
  include flags are never silent. Measured on BPG: 5.3 s cold with analyzers (xUnit analyzers included), 0.0 s cached.
  So "expensive" above is the *first* call, and a per-(TFM, analyzers) cache is what makes default-on affordable —
  worth reading `Source/App/Morris.Roslynk/Features/Diagnostics/` before deciding on opt-in vs default-on here.

- [x] ~~**PERF-002: Measure per-tool response size — token cost is a design metric.**~~ (ID: 1312)
  Done 2026-09-13 (`9e313ac`). Measured all 20 tools against BPG through the real stdio MCP client (`BenchmarkTests.Tool_response_sizes`); results and the not-changed list are in [`docs/acceptance/2026-09-13-perf-002-response-sizes.md`](docs/acceptance/2026-09-13-perf-002-response-sizes.md). One representative call each: **261 207 → 88 101 chars** (~65k → ~22k tokens).

  The big one was a bug, not a default: `rename_symbol`'s preview carried every changed file whole, twice — 142 606 chars for a 16-occurrence rename, now 3 606 (`Document.GetTextChangesAsync` instead of `SourceText.GetTextChanges`). `workspace_symbol`'s default cap went 100 → 25 (43 066 → 8 942) and `semantic_search` got a cap (50); both report `Truncated`. Not changed, with reasons in the doc: JSON escaping (4%), `find_registrations`, `SymbolInfo` verbosity (a ~10-tool contract change that wants VAL-001 evidence), and `analyze_symbol` (884 tokens — the capsule shape below isn't justified yet). Two Codex review rounds (summary-mode truncation, harness failure detection and readiness probe, negative cap); final pass clean.

  Measurement, not a feature. Every response is spent from the agent's context budget and we have never measured any
  of them. Serialize a representative call to each of the 20 tools against BPG, record bytes and approximate tokens,
  and change *defaults* where the numbers justify it — `analyze_symbol` returns five things at once, and
  `find_dead_code_candidates` returns full signatures plus locations. Prefer a better default over a new parameter.

  Prompted by NDepend's pitch of workspace facts "without sending source code to the LLM and without consuming
  tokens". The transferable idea is not their feature — it is treating tokens as a first-class metric the way this
  repo already treats milliseconds. Pairs with VAL-001's "which response shape was awkward to consume", and unlike
  that question this half needs no live session.

  **Target response shape (from lurp, evaluated 2026-08-21 — https://github.com/t-macabee/lurp, MIT, a Roslyn+MCP
  sibling of this tool; skipped as a whole, ~80% overlap):** once measured, the shape worth copying is its "capsule".
  Every response carries two fields — `estimated_tokens` (the content the budget was spent on) and
  `estimated_artifact_tokens` (the whole emitted payload, i.e. what actually lands in the context window) — and when
  a budget is exhausted the response *names the tier it dropped and how to fetch it* instead of truncating silently:
  `omitted: direct_callers (budget_exhausted) — fetch with tier=direct_callers`. `analyze_symbol` is the obvious
  first taker: five sections in one call, so a budget + named-omission is a better default than returning all five.

  **Method (2026-08-21):** roslynk's `Source/TestFixtures/Benchmarks/Benchmarks.md` (`C:\Projects\roslynk`) is a
  ready-made spec for this measurement — per tool, median-of-3 warm ms **and** `len(text)/4` tokens, against a
  grep/sed/`dotnet build` baseline row, one GFM table. Reuse it as-is against BPG for our 20 tools. Data point: its
  bare compile check returns 11 tokens (four count headers); its output is `key=value` + tab-indented tree rather
  than JSON, which is where most of the gap to our responses will come from.

- [ ] **TOOL-007: Semantic diff against a baseline.** (ID: 1313) — Backlog, **blocked on VAL-001**
  "What public API did this branch change" — added/removed/re-signatured members vs the branch point. Carded so it
  isn't lost, **not** because it is justified. `git diff` already answers "which lines changed" far more cheaply; this
  is only worth building for the part git can't do — telling a signature change from a comment reflow, or noticing a
  public member vanished. If VAL-001 shows the agent just reads the git diff, close this unbuilt.

  **Prior art (2026-08-21):** lurp (https://github.com/t-macabee/lurp, MIT) has this built — `index` persists
  Roslyn snapshots to SQLite (3-snapshot retention, `pin-snapshot` to keep one) and `diff --from-snapshot --to-snapshot`
  reports semantic changes between two. Its existence does not change the gate above; it is a reference to read
  before writing ours if VAL-001 ever unblocks this.

### Evaluated and deliberately not building

Recorded so it isn't re-litigated. From NDepend's surface, these are the wrong shape for this tool: **code metrics**
(cyclomatic complexity, coupling, LCOM), **quality gates**, **trend charts**, and **dependency SVG diagrams**. All are
governance-shaped — built for a human reviewing a team's work over time, which is neither our consumer nor our
situation.

**Layering/dependency rules are also a deliberate skip**, despite the tempting `BPG.Core` case (a project that
violated its own documented "dependency-free" rule). `project_overview` already returns each project's reference list,
and it already caught that violation on the first call. The data is exposed; wrapping a rules engine around it is
rebuilding NDepend badly.

## Spotted in real-session use (Electron.NET, 2026-09-01)

- [x] ~~**WS-005: Allow selecting which solution to load — workspace pinned to one sln misses sibling projects.**~~ (ID: 1451)
  Done 2026-09-13 (`cf3b81c`). Both asks. (b) Startup: within one directory, discovery ranks solutions by the C#/VB projects they declare — whole-line `.sln` declarations matched by shape (GUIDs validated, solution folders excluded by type GUID), `.slnx` `Project` paths by extension; folders, malformed lines and `.esproj`/`.sqlproj` don't count — then `.sln` before `.slnx`, then name, so ElectronNET.sln beats ElectronNET.Lean.sln. Narrowed from "transitively covers the most projects" to declared projects on purpose (following references isn't worth a parser); the chosen path is reported by `project_overview` and the startup log. The upward-first rule is unchanged: an enclosing solution still wins over nested ones. (a) Mid-session: `reload_workspace(solutionPath)` switches to the full path of an existing `.sln`/`.slnx` (`SOLUTION_NOT_FOUND` otherwise, checked before any load, the current solution left serving); without it the served solution is re-evaluated. The result's path, project count and diagnostics are read under the reload's own gate, so overlapping reloads can't mix two solutions. Acceptance on the Electron.NET repo itself not run (needs the published exe republished). Five Codex review rounds (three on discovery, two on the switch).

  Observed 2026-09-01 in an Electron.NET session (repo C:\Projects\Electron.NET). The repo has three solutions: `src/ElectronNET.Lean.sln` (4 library projects), `src/ElectronNET.sln` (full: + IntegrationTests, WebApp, ConsoleApp, samples), and a standalone sample sln. The server auto-loaded the Lean sln, so every symbol query (workspace_symbol, find_references, ...) was blind to the test and app projects for the whole session.

  Verified there is no escape hatch: `reload_workspace` takes no parameters — it re-evaluates the already-chosen solution. Which sln gets picked at startup is not controllable per session.

  Wanted:
  - a) `reload_workspace(solutionPath)` (or a dedicated `load_solution`) to switch mid-session, and/or
  - b) startup selection: prefer the sln that transitively covers the most projects when several exist, and report which one was chosen (project_overview already returns solutionPath — good, keep that).

  Acceptance: in the Electron.NET repo, a session can get `workspace_symbol` hits in ElectronNET.IntegrationTests without restarting the server.

  Non-goal noted for the record: Razor/Blazor semantic support was discussed the same session and deliberately NOT carded — no measured pain yet, and it would be a large build (Razor generated-document mapping). Card it only when a session is actually bitten.

## Spotted in real-session use (XafLayoutBuilder, 2026-09-12)

- [x] ~~**WS-007: Workspace goes stale silently — files and project references added after load give incomplete answers with no warning.**~~ (ID: 1654)
  Done 2026-09-13 (`c24ad80`). Detected and reported, not auto-reloaded. Each generation stamps the solution, project files and the `Directory.Build.*`/`Directory.Packages.props` paths MSBuild would import (missing ones included, so one appearing counts); watchers on the solution and project directories collect `*.cs` files and directories created or moved in — a moved-in directory raises one event, so its files are walked by hand, pruning `bin`/`obj`/`node_modules`/dot-folders and junctions before descent. Deletions come from the per-call refresh, which already checks every document's file — linked files outside the watched directories included. `IWorkspaceService.StaleReasons` judges added paths when read (exists and is no document), so backup-rename saves stay silent. `ToolBase` reads the reasons after the call and adds its own when `LoadCount` moved mid-call (the answer may predate the reload); every result carries them as `ToolResult.Warnings`. A watcher that can't start marks the generation instead of failing the load, and an unpublished generation is disposed whole if anything after opening the solution throws. Known limit: a generated or compile-excluded `.cs` written outside `bin`/`obj` reads as added until reload. Four Codex review rounds.

  Reported 2026-09-12 from a XafLayoutBuilder session. `find_references` on `LayoutRegistry.Register<T>` (`XafLayoutBuilder.Module\LayoutRegistry.cs:22:24`) returned 2 references and missed `XafLayoutBuilder.Tests\SpecValidationTests.cs:86`, which a text search finds and `dotnet test` runs. The agent had no way to tell the answer was incomplete.

  Cause (**confirmed**: `reload_workspace` in that session, then the same call, found the missing reference): `XafLayoutBuilder.Tests.csproj` gained its `ProjectReference` to Module at 20:07:34 and `SpecValidationTests.cs` was written at 20:10:53, both after the server loaded. `GetFreshSolutionAsync` (`WorkspaceService.cs:62`) only re-reads documents that were already in the solution; ARCHITECTURE.md lists new .cs files, .csproj edits and new projects as needing `reload_workspace`. Without the reference the Tests compilation can't bind the call to Module's `Register<T>`, so there is nothing to find. Suspects 2 and 3 in the report (Tests loaded broken; generic method inside a lambda) are ruled out — the reload fixed it.

  The documented limit is fine. The defect is that it's **silent**: the result looks complete.

  Fix, cheapest first:
  - a) Per call, `GetFreshSolutionAsync` already stats every document; also stat the solution file and each project file (plus `Directory.Build.props`/`.targets` it can see) and compare to load time. Catches the `ProjectReference` case above at near-zero cost.
  - b) New or deleted .cs files in SDK-style projects don't touch the .csproj, so (a) misses them. A `FileSystemWatcher` on the project directories (`*.cs` created/deleted/renamed, excluding bin/obj) sets a stale flag.
  - c) Report, don't auto-reload, for now: a stale workspace adds a warning to every tool result naming what changed and saying to call `reload_workspace` (needs an optional warnings field on `ToolResult<T>`). Auto-reload costs seconds of warm-up and is unsafe until WS-006 makes reloads atomic and IDX-002 stops indexed tools answering empty during warm-up — revisit after those.

  Test: load the fixture, add a `ProjectReference` or a new .cs file on disk, assert the next tool result carries the stale warning; after `reload_workspace` it doesn't.

## From the Codex review (2026-09-12)

Whole-project read-only review by Codex, verbatim in [`docs/reviews/2026-09-12-codex-review.md`](docs/reviews/2026-09-12-codex-review.md).
All 24 findings were checked against the source and held; grouped into ten cards by shared fix. WS-006 + IDX-002
are the pair to do first — together they are "indexed tools can answer wrong, as success".

- [x] ~~**WS-006: Reload is not atomic — a running warm-up or a failed reload corrupts the index generation.**~~ (ID: 1641)
  Done 2026-09-13 (`bc089c9`, with IDX-002). A load builds a `Generation` — workspace, both indexes, warm-up task and its own cancellation — in locals and publishes it in one step, under the gate, only after `OpenSolutionAsync` succeeds. A failed reload leaves the previous generation serving; a warm-up only builds into its own generation's indexes; a replaced generation's warm-up is cancelled and its workspace disposed after a 30 s grace (the ceiling — a query still running on the retired solution — is commented), and `DisposeAsync` disposes retiring generations too. The failed-reload test surfaced a masking effect worth knowing: the old `ReloadAsync` also cleared the mtime cache, so the empty index was hidden behind a full dirty re-walk on every query rather than returning nothing. Race tests are deterministic via an internal `BeforeIndexBuild` hook. Three Codex review rounds.

  From the 2026-09-12 Codex review (`docs/reviews/2026-09-12-codex-review.md`, findings 3, 5, 6), verified against the code.

  `LoadUnsafeAsync` publishes new state piecemeal into mutable fields, and `WarmupAsync` reads those fields rather than the instances it was started with:
  - **Reload during warm-up** (`WorkspaceService.cs:216-238`): an outgoing warm-up that reaches its index step after `reload_workspace` swapped `_symbolIndex`/`_invocationIndex` builds the *new* indexes from the *old* solution, and then the new warm-up builds into them as well. `InvocationIndex` has no de-dup, so routes/registrations appear twice; stale `SymbolIndex` entries carry old-workspace `DocumentId`s that never intersect the dirty set, so nothing evicts them.
  - **Failed reload** (`:147-166`): indexes and `_workspace` are replaced before `OpenSolutionAsync`. If it throws or is cancelled, `_solution` is still the old solution but the indexes are empty and no warm-up runs — every indexed tool returns nothing until a reload succeeds.
  - **Leak** (`:151`): each reload overwrites `_workspace` without disposing the old `MSBuildWorkspace` (only `CloseSolution()`), and nothing cancels the outgoing warm-up, which keeps compiling a retired solution.

  Fix: build the generation (workspace, solution, both indexes, mtime cache, warm-up CTS) in locals, publish it in one assignment only after the load succeeds, pass the index instances into `WarmupAsync`, then cancel and dispose the outgoing generation. Do together with IDX-002 — its readiness signal belongs to the generation.
  Test: reload while the first warm-up is still running; `find_registrations` counts must match a single clean load.
- [x] ~~**IDX-002: Indexed tools silently return empty results until warm-up finishes.**~~ (ID: 1642)
  Done 2026-09-13 (`bc089c9`, with WS-006). Indexed tools go through `IWorkspaceService.GetIndexedSolutionAsync`, which waits for the current generation's warm-up and returns the refreshed solution with that same generation's indexes (re-targeting a successor if a reload lands mid-wait), so no tool pairs one load's solution with another's index. A failed index build is recorded on the generation and surfaces as `INDEX_UNAVAILABLE` (`IndexedSolution`'s index properties throw). The v1.2 acceptance doc's claim is corrected in place, and the PERF-002 benchmark's readiness probe is gone. The first indexed query after start or reload now blocks until the index is built — that wait is the honest cost the old code hid.

  From the 2026-09-12 Codex review (finding 4), verified against the code.

  No production code awaits `IWorkspaceService.WarmupTask` — only tests do, and `TestHost.cs:56-60` waits it away with a comment describing exactly this race. `semantic_search` (has-attribute/returns/parameter-type), `find_registrations`, `find_entrypoints` and `find_dead_code_candidates` read `SymbolIndex`/`InvocationIndex` directly, so a call in the first seconds after start or reload gets a partial or empty list **reported as success**. `InvocationIndex` takes ~1.7 s on BPG and 6-12 s on duetGPT, so "no DI registrations" is a plausible wrong answer to an agent's very first question. If an index build throws, it is logged and the empty index is served for the rest of the session.

  The v1.2 acceptance doc (`docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md:46`) says a query "will await the in-flight WarmupTask" — it does not. Correct the doc with the fix.

  Fix (smallest honest version): await the warm-up with the request's CancellationToken in the indexed tools before querying, and return `INDEX_UNAVAILABLE` if the build faulted (needs a faulted flag — `WarmupAsync` swallows build exceptions, so `WarmupTask` itself completes successfully). Pairs with WS-006.
  Test: query before warm-up completes and assert the full result, not an empty one.
- [x] ~~**TOOL-008: `rename_symbol` applyEdits can overwrite concurrent edits and leave a half-applied rename.**~~ (ID: 1643)
  Done 2026-09-13 (`10dc91b`). `applyEdits` opens every target exclusively (`FileShare.None`, brief retries for transient readers) and checks it through that handle before writing anything: `FILE_READ_ONLY`, `FILE_LOCKED`, `UNSUPPORTED_ENCODING` (text invalid in the encoding its BOM declares — a legacy code page would have been corrupted by replacement-character decoding), `STALE_FILE` (text differs from the snapshot, or the file was deleted; hint: `reload_workspace`), `LINKED_FILE_CONFLICT` (one file linked into several projects with differing renamed texts). Files are written back through the same handles in their detected encoding (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE BOMs kept). The write phase is not cancellable and has no rollback; an IO failure mid-apply returns `PARTIAL_WRITE` naming what was written. A process-wide lock serialises renames, released even if handle disposal fails. Four Codex review rounds.

  From the 2026-09-12 Codex review (findings 1, 2), verified against the code. Codex rated it high; for a single-user server the race window is narrow, so medium — but the failure mode is lost work.

  `RenameSymbolTool.cs:84-91` writes each changed file's *entire* text from the snapshot taken by `GetFreshSolutionAsync` at the start of the call. No lock spans compute → write, and nothing checks the file is still the version that was renamed:
  - a file saved by the editor (or another tool call) in that window is silently overwritten with the pre-edit text plus the rename;
  - a failure part-way (read-only file, cancellation) leaves earlier files renamed and later ones not, and the error result doesn't say which were written;
  - `File.WriteAllTextAsync` writes UTF-8 without BOM, so a file that had a BOM or another encoding changes encoding on every rename.

  Fix: before writing, re-read each target and compare it to the snapshot text — reject with `STALE_FILE` naming the file if it changed; write only after every check passes; on a write failure report the files already written. Preserve the original file's encoding/BOM.
- [x] ~~**IDX-003: InvocationIndex refresh drops BackgroundService subclasses; build still walks referenced assemblies.**~~ (ID: 1644)
  Done 2026-09-13 (`89d220a`). Subclasses are found in `IndexDocument` from `ClassDeclarationSyntax` declared symbols and their base chain — the same per-document pass the dirty re-walk reuses — and the per-project `compilation.GlobalNamespace` walk is gone. A partial subclass is recorded by every declaring file and reported once per project, so adding the base list to any part is picked up while same-named types in different projects stay distinct. In-process A/B on BPG (warmed compilations, `SymbolIndex` build as control): old walk 347–919 ms = 3–11× control, per-document walk 34–155 ms = 0.7–1.6× control. Neither BPG nor duetGPT declares a `BackgroundService` subclass, so equivalence rests on the fixture (single-file, refresh, and partial A/B/both cases), not a real solution. Accepted ceiling, commented: removing the base from one part leaves the other parts' entries until those files change or a reload (IDX-004's per-document dirtiness). Three Codex review rounds.

  From the 2026-09-12 Codex review (findings 8, 19), verified against the code.

  `RefreshDirty` (`InvocationIndex.cs:168`) removes *all* hosted-service entries for a dirty document, including `Kind: "subclass"`, but re-indexes through `IndexDocument`, which only sees `AddHostedService<T>` calls — subclass detection lives only in `BuildAsync` (`:76`). Touching a worker's file (a comment is enough) makes it vanish from `find_entrypoints` until `reload_workspace`.

  The same `BuildAsync` loop walks `compilation.GlobalNamespace` — every referenced assembly — per project: the waste PERF-001 removed from `SymbolIndex`, in the index that is now the dominant warm-up cost.

  Fix (one change covers both): move subclass detection into `IndexDocument` — for each `ClassDeclarationSyntax`, `GetDeclaredSymbol` + `IsBackgroundServiceSubclass`, de-duped for partial classes — and delete the namespace walk. Measure with an interleaved in-process A/B on BPG, as PERF-001 did.
  Test: bump `PollingWorker.cs`'s mtime, query, assert the subclass entry survives.
- [x] ~~**IDX-004: Dirty-document refresh — SymbolIndex never clears its dirty set; InvocationIndex refresh is not atomic.**~~ (ID: 1645)
  Done 2026-09-13 (`11dd09f`). The first query after a change folds it in once: both indexes snapshot markers + solution, build replacement entries into locals, swap them in under the lock, and clear the markers only if no newer solution arrived meanwhile — a parallel query sees old or new entries, a throw or cancellation leaves everything for the next query. `WorkspaceService` hands the changed documents and their solution to both indexes in one `Invalidate` call, in a `finally` so files read before a later read fails are not lost; any different mtime is a change. Review surfaced two more: the refresh walked declaration by declaration, dropping members with no syntax (implicit constructors, record plumbing, expression-bodied getters) — it now uses the build's walk per touched type; and walking the newest solution let `find_dead_code_candidates` resolve entries against an older snapshot — `AllSymbols` now returns the solution its entries reflect. Walks stay synchronous (the query API is). Cross-document ceiling recorded in ARCHITECTURE. Not done (Codex, non-blocking): deterministic overlap/mid-walk-cancel test hooks. Two Codex review rounds.

  From the 2026-09-12 Codex review (findings 7, 9, 10, 18), verified against the code.

  - `SymbolIndex._dirty` is only ever added to (`SymbolIndex.cs:63-66`). Every `semantic_search`/dead-code/`find_registrations` query re-walks *every* document edited since load, synchronously (`GetSemanticModelAsync(...).GetAwaiter().GetResult()`, `:172`), and `find_registrations` does it twice per registration (`FindRegistrationsTool.cs:88-93`). Query cost grows with the session's edit history — worst in exactly the edit-heavy sessions VAL-001 is about.
  - `InvocationIndex.RefreshDirty` (`InvocationIndex.cs:151-181`) clears the dirty set, removes the doc's entries, *then* re-binds, outside `GetFreshSolutionAsync`'s gate. Two parallel tool calls: the second sees neither old nor new entries. If the re-bind throws, the entries are gone and the dirty marker already discarded, so they don't return until reload.
  - Freshness is `cachedMtime >= diskMtime` (`WorkspaceService.cs:66`): restoring a file with an older timestamp (a copy or unpack that preserves times) is treated as unchanged. `!=` fixes it.
  - Known ceiling, record rather than fix: dirtiness is per document, but semantics cross documents — changing a `global using` alias or a type's namespace in file A changes the index keys of unchanged file B. Note it in ARCHITECTURE; `reload_workspace` clears it.

  Fix: rebuild changed-doc entries into locals and swap them in under the lock, clearing the dirty marker only after a successful swap; have `SymbolIndex` fold re-walked entries back into its buckets and clear the marker; make the walks async with the request's token.
- [x] ~~**TOOL-009: `find_entrypoints` treats any method named Map/MapGet/AddHostedService as an endpoint.**~~ (ID: 1646)
  Done 2026-09-13 (`911c449`). A route now has to resolve to an `IEndpointRouteBuilder` extension that takes a handler (`Delegate`/`RequestDelegate`), called as an extension or statically; `AddHostedService<T>` and DI calls to an `IServiceCollection` extension or member — `IsServiceCollectionCall` lost its "type name contains ServiceCollection" shortcut. Route arguments are read by parameter (named, out-of-order and `@`-escaped included), so `MapMethods` reports `GET,HEAD` and its real handler, and a partly computed method list reads `ANY` instead of a wrong literal subset. When overload resolution fails, the candidate whose parameter names fit the named arguments is used. Fixture: six routes and a `FakeMapper.cs` of look-alikes (mapper `Map<T>`/`MapGet`/`AddHostedService<T>`, `FakeServiceCollection`, a handler-less `IEndpointRouteBuilder.Map<T>`). Two Codex review rounds (six findings, all fixed). Untested by design: the failed-overload fallback — reproducing it needs compile errors in the fixture.

  From the 2026-09-12 Codex review (findings 14, 15), verified against the code.

  `IndexDocument` classifies routes by method *name* (`InvocationIndex.cs:194`), and `TryBuildRoute` (`:246`) takes a `SemanticModel` it never uses. `Map` is also AutoMapper's API — every `_mapper.Map<Dto>(entity)` in a real app is reported as an `ANY` route with the entity as its "handler". `TryBuildHostedService` (`:280`) likewise accepts any generic method named `AddHostedService`.

  `MapMethods("/x", new[] { "GET", "HEAD" }, Handler)` reports verb `ANY` and the method array as the handler, because the handler is assumed to be argument 2 (`:262`).

  Fix: bind the invocation and require an extension on `IEndpointRouteBuilder` — the same approach `IsServiceCollectionCall` already takes for DI (reuse it for `AddHostedService`). For `MapMethods` take the handler from argument 3 and report the literal methods. Binding only runs for the seven route names, so warm-up impact should be small — confirm on BPG.
  Test: a negative fixture in TestWeb with an AutoMapper-style `Map<T>(x)` call.
- [x] ~~**IDX-005: Symbol de-dup by documentation-comment ID collapses distinct symbols from different projects.**~~ (ID: 1647)
  Done 2026-09-13 (`60a3ae6`). The card's key (id + file) was not enough, and review took it through seven rounds. `SymbolIndex` keys on **(id, declaration file, declaring assembly name)** — the assembly name is what separates a file *linked* into two projects (same path, two symbols) from one assembly built for several target frameworks or a referenced project's source (one symbol) — and merges declaring documents when one declaration is seen through several projects. The dirty walk folds partial members to their definition part; the build records a partial member's implementation file as declaring it, and an invalidated entry's other declaring files are re-walked, so neither editing nor deleting one part loses the other. `ResolveSymbolByIdAsync` uses the same (file, assembly) key and throws `AMBIGUOUS_SYMBOL_ID`, naming each declaration's assembly. `find_dead_code_candidates` works per declaration and leans towards keeping code when `#if` makes a linked or multi-targeted declaration's copies differ: the most exposed copy sets accessibility, and a reference, denylisted attribute or framework reachability on any copy spares it — checking copies separately had called a linked file dead while one assembly still used it. `workspace_symbol` and `semantic_search`'s `derives-from:`/`implements:` also stopped de-duplicating on id alone. Known limits, in ARCHITECTURE.md: unrelated projects sharing an assembly name and a linked file still collapse; a partial type whose other parts differ between the projects linking one part groups per primary file; position-based tools on a linked file resolve in the first project compiling it.

  From the 2026-09-12 Codex review (finding 13), verified against the code.

  `MergeWithDirtyWalk` de-dups on `SymbolId` (`SymbolIndex.cs:164`), which is `DocumentationCommentId.CreateDeclarationId` — no assembly identity. Two projects declaring the same fully-qualified name collapse to one entry. The common case is not exotic: every top-level-statements project has a global `Program` (`T:Program`). `find_dead_code_candidates` then sees only one of them, and `ResolveSymbolByIdAsync` (`RoslynHelpers.cs:43`) returns whichever project enumerates first.

  The de-dup exists for a real reason (IDX-001/TOOL-006: a referenced project's source symbols appear once per referencing compilation), so the new key must still merge those.

  Fix: de-dup on `SymbolId` + primary declaration file path — the same symbol seen through two compilations shares its source location; two distinct symbols don't. In `ResolveSymbolByIdAsync`, return `AMBIGUOUS_SYMBOL_ID` listing candidate locations when an id resolves to symbols with different source locations.
  Test: fixture with two projects that each declare `Program`.
- [x] ~~**TOOL-010: Line/column are not validated — an out-of-range column resolves a symbol on a later line.**~~ (ID: 1648)
  Done 2026-09-13 (`0a989bd`). `ResolveSymbolAtPositionAsync` — the one helper all ten position-taking tools route through — now requires line `1..Lines.Count` and column `1..lineLength + 1` (the cursor just past the last character stays valid; line spans exclude CRLF, so empty lines and an unterminated last line behave) and throws `PositionInvalidException`, which `ToolBase` maps to `POSITION_INVALID`. No call site changed. Codex review: no findings; its one coverage note (the end-of-line case also accepted `INTERNAL_ERROR`) was tightened.

  From the 2026-09-12 Codex review (finding 12), verified against the code.

  `ResolveSymbolAtPositionAsync` (`RoslynHelpers.cs:25`) computes `text.Lines[line - 1].Start + (column - 1)` with no bounds check. A column past the end of the line silently lands on a following line, so `goto_definition`/`hover`/`find_callers` answer about the wrong symbol — and `rename_symbol` with `applyEdits=true` renames it. A bad line throws `ArgumentOutOfRangeException`, surfaced as `INTERNAL_ERROR` instead of `POSITION_INVALID` (a code the tools already use). Agents do produce off-by-some columns, so this matters more than it looks.

  Fix: validate `1 <= line <= Lines.Count` and `1 <= column <= lineLength + 1` in the helper that every position-taking tool routes through, and return `POSITION_INVALID`.
- [x] ~~**DIAG-002: `get_compilation_errors` over-promises and silently ignores some inputs.**~~ (ID: 1649)
  Done 2026-09-13 (`484c676`). The result carries **`LoadFailures`** — workspace load failures still standing after reclassification, one per project named plus one per distinct message naming none — and the summary appends it, so a clean list with failures above zero no longer reads as a clean build. Review surfaced three things underneath: WS-004's reclassification compared project *file names*, so a failed `B\Foo.csproj` read as loaded because `A\Foo.csproj` did (it now compares the quoted path, resolving relative ones against the solution directory); load diagnostics were cleared before a reload opened its solution, so a failed reload left the serving solution with no failures on record (they now live on the workspace generation); and the solution and its diagnostics were read separately, so a reload mid-call could pair two generations (`IWorkspaceService.GetFreshSolutionWithDiagnosticsAsync` reads both under one gate, without waiting for index warm-up). An unknown `projectName` is `PROJECT_NOT_FOUND`, matching only an exact name or a real target-framework suffix; `excludeDiagnosticSources` is gone from both tools; an exact `severity` overrides the default minimum. Five Codex review rounds.

  From the 2026-09-12 Codex review (findings 17, 21, 22), verified against the code.

  - The description says "equivalent to 'would dotnet build succeed?'", but only loaded projects are compiled. A project that failed to load (kind `Failure` in the workspace diagnostics) contributes nothing, so a broken solution can report `0 errors, 0 warnings`. Carry a not-loaded count in the result and reword the description.
  - An unknown `projectName` returns an empty success (`GetCompilationErrorsTool.cs:33`) — return `PROJECT_NOT_FOUND`.
  - `excludeDiagnosticSources` is advertised on both diagnostic tools and discarded (`:85-87`). Remove it; re-add if DIAG-001 introduces a real source field.
  - `severity: "Info"` or `"Hidden"` is always filtered out by the default `minimumSeverity: "Warning"` (`:52`). An explicit exact severity should bypass the threshold.

  Touches the same filters as DIAG-001 — do this first or together.
- [x] ~~**TOOL-011: Low-severity sweep from the 2026-09-12 Codex review.**~~ (ID: 1650)
  Done 2026-09-13 (`07223b3`). `analyze_symbol` fills implementations for members; review found that Roslyn's `FindImplementationsAsync` answers only for types and interface members, so abstract/virtual class members came back empty there *and* in `find_implementations` (whose description promises them) — a shared `RoslynHelpers.FindImplementationsOrOverridesAsync` uses `FindOverridesAsync` for class members, skipping abstract intermediate overrides. Left documented: an interface event's `add`/`remove` accessor queried directly finds nothing (Roslyn excludes those accessors). `list_document_symbols` lists indexers, delegates and enum members. `SolutionDiscovery` skips an unreadable or vanished directory. The SDK claim held: a failed call now sets MCP `isError` (ToolBase marks it, a call-tool filter sets the flag; payload unchanged), proven through the real stdio transport in `McpProtocolTests`. Every test `WorkspaceService` is disposed and `ToolHost` disposes its `ServiceProvider`. Three Codex review rounds.

  From the 2026-09-12 Codex review (findings 11, 16, 20, 23, 24), verified against the code. Each is small; batched.

  - `analyze_symbol` returns `Implementations: null` for interface/abstract **members** — `AnalyzeSymbolTool.cs:151` gates on `INamedTypeSymbol`, while `find_implementations` handles members. Drop the gate for implementable members.
  - `list_document_symbols` omits delegates, indexers and enum members (`ListDocumentSymbolsTool.cs:43`: `IndexerDeclarationSyntax` is not a `PropertyDeclarationSyntax`, `DelegateDeclarationSyntax` not a `BaseTypeDeclarationSyntax`). Match `BasePropertyDeclarationSyntax`, `DelegateDeclarationSyntax`, `EnumMemberDeclarationSyntax`.
  - `SolutionDiscovery.SolutionsIn` calls `GetFiles` outside the `UnauthorizedAccessException`/`DirectoryNotFoundException` guard (`SolutionDiscovery.cs:28`, `:43`), so an unreadable directory aborts discovery instead of being skipped.
  - Tool failures come back as a normal `ToolResult` payload, so the MCP response's `isError` stays false (`ToolBase.cs`, `ToolError.cs`). Agents read the in-band `Error` fine; a client keyed on `isError` doesn't. **Unverified SDK claim** — confirm against ModelContextProtocol 1.3.0 with one stdio-level test first; if it holds, return `CallToolResult { IsError = true }` carrying the same payload.
  - Tests: `WorkspaceServiceTests` constructs `WorkspaceService` without `await using` (e.g. `:25`, `:36`), some tests return with warm-up still running, and `TestHost` never disposes its `ServiceProvider` (`TestHost.cs:67`).

## Real-session validation (still to do)

- [ ] **VAL-001: Use mcpRoslyn in one feature-sized task.** (ID: 1171) — **in Todo, est. 4 h**
  Do one feature-sized task in a real repo with the MCP server connected, and write down how the tools actually behaved
  in the agent loop. The acceptance logs cover correctness of canned queries; they do not cover end-to-end usefulness.
  Highest-value remaining item — it is the only thing that can unblock TOOL-004 and TOOL-005, both of which say in
  their own bodies not to build without this evidence.

  **The finding to design around, from the BPG session (2026-08-15):** the tools were available the whole session and
  went unused until explicitly prompted, **because grep is the reflex**. That is an adoption problem, not a tool-surface
  problem, and it is probably the most valuable thing to measure. A run that only asks "did the tools return the right
  answer" will miss it entirely.

  Suggested shape so it doesn't drift into unstructured poking:
  - Pick a task that requires *changing* code, not just reading it — the reflex only shows up under real pressure.
  - Keep a running log, one line per tool call: what was asked, what came back, whether grep/Read was reached for
    first and why.
  - Answer four questions explicitly at the end: (1) which tool call replaced several greps; (2) where grep was
    reached for when a tool would have been better, and what made grep feel cheaper; (3) which response shape was
    awkward to consume (too big, wrong nesting, missing a field that forced a follow-up call); (4) did cold start
    actually hurt.

  Two things changed on 2026-08-15 that should shape the run. **Cold-start friction is largely gone for `SymbolIndex`**
  (PERF-001): ~0.1–0.4 s, down from seconds — but `InvocationIndex` is now the dominant cost (1715 ms BPG, 12 205 ms
  duetGPT), so if cold start still hurts, that is the thing to point at, and this run is what would justify carding it.
  And **`find_dead_code_candidates` is worth re-testing** (TOOL-006): it is the one tool the BPG session judged not to
  have earned its keep, and it has since been substantially fixed, so that verdict is stale.

  Deliverable is written findings, not code — a short report in `docs/acceptance/`. Prior data point worth carrying in:
  `find_registrations` and `project_overview` both clearly earned their keep on BPG — `find_registrations "Hangfire"`
  would have surfaced a bug that instead cost a `dotnet-stack` dump on a hung process, and `project_overview` caught
  `BPG.Core` violating its own documented "dependency-free" rule on the first call.

  **Run roslynk alongside (added 2026-08-21).** https://github.com/mrpmorris/roslynk (Peter Morris, MIT, beta) is
  cloned at `C:\Projects\roslynk`, built Release, and registered **project-scoped in BPG's `.mcp.json`** next to
  NDepend — so the BPG session has both servers and this run answers a fifth question for free: **(5) which server did
  the agent reach for, per task shape?** roslynk covers the *edit loop* we don't — `apply_patch` (content-anchored
  diff, stale-guarded, folds into the in-memory model), `get_code_actions`/`apply_code_fix` (real Roslyn fixes),
  `change_signature`, `remove_unused_usings`, Razor-aware rename — plus analyzer diagnostics by default and a
  persistent loopback daemon (`:6502`, auto-spawned by its `stdio` bridge) that keeps solutions warm across sessions.
  We cover the *orientation layer* it doesn't — `project_overview`, `find_registrations`, `find_entrypoints`,
  `test_map`, `semantic_search`, DI-aware dead-code filtering. Smoke-tested on BPG 2026-08-21: 8/8 projects Ready in
  15 s, `get_diagnostics` 5.3 s cold / 0.0 s cached, bare compile check = **11 tokens**. If the agent uses roslynk's
  write tools under pressure, mcpRoslyn's lane is the architecture layer; if it ignores them, nothing changes.

  Any repo with a real pending task works; BPG is now the better-understood benchmark of the two.

  **Run design, decided 2026-09-13.** The session measures the setup as it ships, not raw adoption: the global
  CLAUDE.md "reach for mcpRoslyn" section and the PreToolUse hook blocking bare-identifier Grep on C# both stay on
  (unprompted adoption was already observed on 2026-08-15). The BPG session gets only a real code-changing task —
  suggested: ProblemDetails middleware + one error format across controllers — and is told nothing about VAL-001,
  mcpRoslyn, roslynk or logging. The per-call log and the five questions are reconstructed afterwards from its
  transcript, against the exe republished 2026-09-13 07:01 (`a5cdf08`). Read question (2) in that light: a grep the
  hook denied is not the agent choosing the tool.
