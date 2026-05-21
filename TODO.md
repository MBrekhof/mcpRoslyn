# TODO — mcpRoslyn

v1 is shipped and accepted (see [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)). v1.1 warm-up shipped (see [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)). v1.3 feature-expansion shipped (branch `feat/v1.3-feature-expansion`; 103 tests; awaiting acceptance run).

## v1.1 follow-ups (from acceptance log)

- [x] ~~**Expose MSBuild workspace warnings.**~~ Shipped (commit `417e86b`). `WorkspaceFailed` events now accumulate on `IWorkspaceService.Diagnostics` (cleared per load/reload) and are surfaced on `reload_workspace` output as `WorkspaceLoadDiagnostic[]`.
- [x] ~~**Warm-up / pre-compilation on load.**~~ Shipped. First-query `find_references` on duetGPT dropped from 8 400 ms to 1 874 ms (4.5×).
- [x] ~~**`semantic_search` attribute walk is O(symbols).**~~ Shipped as v1.2. New `SymbolIndex` (built in parallel during warm-up) backs `has-attribute:` / `returns:` / `parameter-type:` with O(matches) lookups. Always-fresh semantics preserved via per-query dirty-doc walk (the mtime-refresh in `GetFreshSolutionAsync` calls `MarkDirty`; queries filter out cached entries whose declaring docs intersect the dirty set, then walk just the dirty docs and merge). Design: [`docs/plans/2026-05-16-attribute-index-design.md`](docs/plans/2026-05-16-attribute-index-design.md). Plan: [`docs/plans/2026-05-16-attribute-index-implementation.md`](docs/plans/2026-05-16-attribute-index-implementation.md).
- [x] ~~**`workspace_symbol` lookup hint when `find_callers` gets `SYMBOL_NOT_FOUND`.**~~ Shipped (commit `f938fb0`). Both the symbolId and cursor-position failure paths now carry contextual `hint` fields.
- [x] ~~**Project-count mismatch diagnostic.**~~ Shipped as part of the diagnostics-surface work (`417e86b`). The "X of 5 loaded, here's the failure list" answer is now derivable from `reload_workspace`'s output. Did not implement an explicit declared-vs-loaded numeric comparison — would require parsing `.sln`/`.slnx` formats; the diagnostics list carries the same information without that risk.
- [x] ~~**`--log-file <path>` flag.**~~ Shipped (commit `421cc8f`). Append-mode file logging via a custom `ILoggerProvider`; closes the "Claude Code only captures stderr until `initialize`" diagnostic gap.

## Deferred from v1 design

- [ ] **`dotnet tool` packaging.** Revisit if/when mcpRoslyn needs to be installed outside the local machine. Needs a feed; not worth it for single-user.
- [ ] **HTTP/SSE transport.** Currently stdio only. Re-evaluate cold-start-cost vs. complexity once session data shows whether multiple Claude Code sessions on the same solution would benefit from sharing one workspace process.
- [ ] **Cross-platform (Linux/Mac).** Deferred until there's a real non-Windows user. `MSBuildLocator` and path-comparison code would both need attention.
- [ ] **Wider `semantic_search` grammar.** Current 5 patterns (`derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) are a starting set. Add based on observed gaps in real sessions.
- [ ] **`ISymbolProvider` abstraction.** If we ever wrap gopls/pyright/rust-analyzer, factor `WorkspaceService` behind a more abstract provider interface. Don't build it speculatively.

## Nice-to-haves spotted along the way

- [ ] **Extract project name from `WorkspaceLoadDiagnostic.Message`.** Currently the DTO is `{ Kind, Message }`; the project filename is embedded in the message text. Adding a `ProjectName: string?` field (regex-extracted from the message) would make filtering/grouping easier for tool callers. Small, safe.
- [ ] **Fix the `Workspace.WorkspaceFailed` obsolete warning.** Roslyn 5.3 deprecates the event in favor of `RegisterWorkspaceFailedHandler`. CS0618 has been accepted since v1; a small migration would clean it up.
- [ ] **Investigate `duetGPT.LicenseServer` silent drop.** v2.0 of duetGPT's .sln declares 5 projects; MSBuildWorkspace consistently loads 4. `duetGPT.LicenseServer` is filtered out *before* MSBuild raises a `WorkspaceFailed` event, so the v1.1 diagnostics-surfacing work (`WorkspaceLoadDiagnostic`) doesn't catch it — verified in v1.2 acceptance ([`docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`](docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md)). Likely an SDK / target-framework / project-type filter applied at workspace open. Start by inspecting that project's .csproj for `<Sdk>` reference / target framework / project type GUID, then check Roslyn's `MSBuildWorkspace.OpenSolutionAsync` source for what it skips silently. May need a separate `list_solution_projects` tool that reads the .sln/.slnx directly to surface declared-but-unloaded entries.
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

- [ ] **`SymbolIndex.AllSymbols()` not in dirty-walk.** Could return stale data after edits. Low-impact for `find_dead_code_candidates` (run-occasionally tool); revisit if observed.
- [ ] **`find_dead_code_candidates` `Skipped` counters truncate when `maxResults` hits.** Minor stats inaccuracy when the result set is large; document or fix in v1.4.
- [ ] **`find_registrations` consumer detection over-broad.** Currently returns any method with the parameter type; tighten to `MethodKind == Constructor` for higher signal in v1.4.
- [ ] **`project_overview.TargetFramework` is always `null`.**~~ Add .csproj XML parsing in v1.4.
- [ ] **`find_entrypoints` hosted-service de-dup pivot.** Tool layer collapses "registered" + "subclass" entries for the same type. If duetGPT acceptance shows agents want both visible, expose a flag.
- [ ] **`HoverToolTests.cs` line 19 stale comment.** Comment references `EnglishGreeter.Greet` body that was changed in Task 6. Trivial cleanup.
- [ ] **Strengthen `find_dead_code_candidates` test 3.** `Skipped_counters_report_publicMembers_and_tests` is currently a trivial null check — replace with a real count assertion in v1.4.

## Real-session validation (still to do)

- [ ] Use mcpRoslyn in one feature-sized duetGPT task and record: missing tools, wrong response shapes, cold-start friction. The acceptance logs cover correctness of canned queries; they do not cover end-to-end usefulness in an agent loop.
