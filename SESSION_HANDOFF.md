# Session Handoff

**Last updated:** 2026-05-21 (end of v1.3 feature-expansion session)

## Where things stand

- **v1.3 shipped:** 7 new tools + `format` parameter on every tool + diagnostics filter knobs. `InvocationIndex` added as a sibling to `SymbolIndex` to back `find_entrypoints` and `find_registrations`.
- Branch `feat/v1.3-feature-expansion`, working tree clean. **NOT YET merged to main.** Awaiting acceptance run (Task 13).
- **Tests:** 103 passing (was 46 at start of session, +57 new across all new tool test classes).
- **All v1.3 planned items are now closed** (see [`TODO.md`](TODO.md)).

## What shipped this session

```
6ce66a0 docs: surface diagnostic filter defaults in tool descriptions
caa0679 feat: diagnostics filter knobs (includeGenerated, excludeCodes, minimumSeverity)
67edfa7 fix: tolerate CRLF in MyAttribute.cs fixture in dirty-walk removal test
bf8f727 feat: format param on all tools (structured | summary) for context economy
d146f6f feat: find_dead_code_candidates tool with denylist + InternalsVisibleTo handling
d91f4dc feat: test_map tool — production-to-test heuristic
3edc777 fix: thread-safe Truncated collection in analyze_symbol
8feae51 feat: analyze_symbol composite tool
7d8821d feat: find_callees tool — outgoing call detection mirror of find_callers
f85bd95 feat: find_registrations tool with DI lifetime + likely consumer detection
bf7d887 feat: find_entrypoints tool (routes, middleware, hosted services)
081c45c fix: log .csproj XML parse failures instead of silent swallow
21626c1 feat: project_overview tool with solution structure and refs
e3c2896 fix: recursive nested-type walk for BackgroundService detection
6e5baef feat: InvocationIndex skeleton (routes, middleware, hosted services, DI)
5fae067 test: extend fixture with TestWeb + TestTests projects and dead-code injections
```

## Material changes in v1.3

**New class** `mcpRoslyn.Workspace.InvocationIndex` — sibling to `SymbolIndex`, built during warm-up, owned by `WorkspaceService`, exposed via `IWorkspaceService.InvocationIndex`. Walks `InvocationExpressionSyntax` in each project's syntax trees during build, classifying into four buckets: routes, middleware, hosted services, and DI registrations (with `Unclassified[]` for unrecognised `IServiceCollection` extension calls). Lifecycle and dirty-doc handling mirror `SymbolIndex`. Reconstructed on `ReloadAsync`.

**New field** `Summary` on `ToolResult<T>` — every tool now accepts `format = "structured" | "summary"` (default `structured`). In summary mode `Result` is null and `Summary` holds a one-line human description. Errors are always structured. Backwards-compatible with v1.2 callers.

**New method** `SymbolIndex.AllSymbols()` — flat enumeration across all three index dictionaries, used by `find_dead_code_candidates` to walk the full symbol population without a Roslyn compilation pass. Does not participate in the dirty-walk (stale data possible after edits; acceptable for a run-occasionally tool).

## What's next

1. **Task 13 acceptance run on duetGPT.** Re-publish exe (close any running `mcpRoslyn.exe` first), restart a Claude Code session in `c:\projects\duetgpt`, run both the v1.2 reference queries and the 7 new v1.3 queries, write `docs/acceptance/2026-05-21-v1.3-acceptance.md`.
2. **Merge `feat/v1.3-feature-expansion` to main** once acceptance passes.
3. **Remaining nice-to-haves** — see `## Nice-to-haves spotted during v1.3` in [`TODO.md`](TODO.md).

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
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~InvocationIndexTests" -c Release
dotnet test mcpRoslyn.slnx --filter "FullyQualifiedName~FindDeadCodeCandidatesToolTests" -c Release

# Re-publish the exe (close any running mcpRoslyn.exe first)
dotnet publish src/mcpRoslyn -c Release -o bin/publish

# Run with persistent logging (captures warm-up + index-build timings)
mcpRoslyn.exe --log-file c:\users\marti\.claude\debug\mcpRoslyn.log
```

**Note:** solution file is `mcpRoslyn.slnx` (XML format), not `mcpRoslyn.sln`.

## Reference

- Architecture summary: [`ARCHITECTURE.md`](ARCHITECTURE.md) (now includes `InvocationIndex` section and 19-tool surface table)
- Open work: [`TODO.md`](TODO.md)
- v1 design + plan + acceptance: `docs/plans/2026-05-15-*.md`, `docs/acceptance/2026-05-15-v1-acceptance.md`
- v1.1 warm-up: `docs/plans/2026-05-16-warmup-precompilation-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`
- v1.2 SymbolIndex: `docs/plans/2026-05-16-attribute-index-{design,implementation}.md`, `docs/acceptance/2026-05-16-v1.2-symbolindex-acceptance.md`
- v1.3 feature-expansion: `docs/plans/2026-05-20-v1.3-feature-expansion-{design,implementation}.md`
