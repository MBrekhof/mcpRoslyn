# TODO — mcpRoslyn

v1 is shipped and accepted (see [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md)). v1.1 warm-up shipped (see [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md)).

## v1.1 follow-ups (from acceptance log)

- [x] ~~**Expose MSBuild workspace warnings.**~~ Shipped (commit `417e86b`). `WorkspaceFailed` events now accumulate on `IWorkspaceService.Diagnostics` (cleared per load/reload) and are surfaced on `reload_workspace` output as `WorkspaceLoadDiagnostic[]`.
- [x] ~~**Warm-up / pre-compilation on load.**~~ Shipped. First-query `find_references` on duetGPT dropped from 8 400 ms to 1 874 ms (4.5×).
- [ ] **`semantic_search` attribute walk is O(symbols).** `has-attribute:` took ~11 s on duetGPT (still ~7.7 s in v1.1) because it walks every symbol in every compilation. Pre-build an attribute index (keyed by attribute full name) at load time to reduce to sub-second. Design call deferred — eager vs lazy index, when to invalidate (reload only? or on first `WithDocumentText`?), memory cost on 1000+ project solutions.
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
- [ ] **Re-measure `find_implementations` on duetGPT.** v1.1 acceptance showed an apparent regression (300 ms → 832 ms) on a single sample. One more run will tell if it's noise or real.

## Real-session validation (still to do)

- [ ] Use mcpRoslyn in one feature-sized duetGPT task and record: missing tools, wrong response shapes, cold-start friction. The acceptance logs cover correctness of canned queries; they do not cover end-to-end usefulness in an agent loop.
