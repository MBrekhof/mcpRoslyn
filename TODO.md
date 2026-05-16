# TODO — mcpRoslyn

v1 is shipped and accepted (see `docs/acceptance/2026-05-15-v1-acceptance.md`). Open items below.

## v1.1 follow-ups (from acceptance log)

- [ ] **Expose MSBuild workspace warnings.** `WorkspaceFailed` events currently go through `ILogger.LogWarning`, but a NullLogger in unit-test paths hides them. Add either a `--verbose` flag or a `list_workspace_diagnostics` tool so callers can see which projects failed to load and why. Driven by the `duetGPT.LicenseServer` silent-drop observation (4/5 projects loaded with no surfaced reason).
- [x] ~~**Warm-up / pre-compilation on load.**~~ Shipped — see [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md). First-query `find_references` on duetGPT dropped from 8 400 ms to 1 874 ms (4.5×).
- [ ] **`semantic_search` attribute walk is O(symbols).** `has-attribute:` took ~11s on duetGPT because it walks every symbol in every compilation. Pre-build an attribute index (keyed by attribute full name) at load time to reduce to sub-second.
- [ ] **`workspace_symbol` lookup hint when `find_callers` gets `SYMBOL_NOT_FOUND`.** `DocumentationCommentId`s are brittle across signature changes. When a `symbolId`-based call fails, surface a hint suggesting `workspace_symbol` to re-resolve the current ID.
- [ ] **Project-count mismatch diagnostic.** When MSBuildWorkspace silently skips a project (SDK/target mismatch, missing restore), emit a clear error rather than a count discrepancy the user has to notice.

- [ ] **`--log-file <path>` flag.** Tee `ILogger` output to a file so warm-up timings, MSBuild warnings, and per-call diagnostics are inspectable after the session. Claude Code's `--debug mcp` only captures stderr until the MCP `initialize` handler completes — anything async (like warm-up's per-project log lines) is lost. Small change in `Program.cs` and `McpRoslynOptions`.

## Deferred from v1 design

- [ ] **`dotnet tool` packaging.** Revisit if/when mcpRoslyn needs to be installed outside the local machine. Needs a feed; not worth it for single-user.
- [ ] **HTTP/SSE transport.** Currently stdio only. Re-evaluate cold-start-cost vs. complexity once session data shows whether multiple Claude Code sessions on the same solution would benefit from sharing one workspace process.
- [ ] **Cross-platform (Linux/Mac).** Deferred until there's a real non-Windows user. `MSBuildLocator` and path-comparison code would both need attention.
- [ ] **Wider `semantic_search` grammar.** Current 5 patterns (`derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) are a starting set. Add based on observed gaps in real sessions.
- [ ] **`ISymbolProvider` abstraction.** If we ever wrap gopls/pyright/rust-analyzer, factor `WorkspaceService` behind a more abstract provider interface. Don't build it speculatively.

## Real-session validation (still to do)

- [ ] Use mcpRoslyn in one feature-sized duetGPT task and record: missing tools, wrong response shapes, cold-start friction. Feed into v1.1 prioritization. The acceptance log covers correctness of 4 queries; it does not cover end-to-end usefulness in an agent loop.
