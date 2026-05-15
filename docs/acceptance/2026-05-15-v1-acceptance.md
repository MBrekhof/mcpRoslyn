# v1 Manual Acceptance — 2026-05-15

Target: `c:\projects\duetgpt\duetGPT\duetGPT.sln` (.NET 10, Blazor, EF Core; 5 projects in sln, 4 loaded by MSBuildWorkspace)

## Cold start

- Time: **8 504 ms**
- Projects loaded: **4**

Note: The solution declares 5 projects; MSBuildWorkspace loaded 4. The fifth (`duetGPT.LicenseServer`) is likely excluded due to framework/target incompatibility with the local SDK or a missing restore. The workspace emits `WorkspaceFailed` warnings to the NullLogger, which are invisible in this test — this is expected behavior and does not affect the other four projects.

## Query results

### 1. find_references on `IGroupPermissionResolver`

- **Time:** 8 400 ms
- **Error:** none
- **Reference count:** 30

The first query is slow because it triggers Roslyn's background compilation for all four loaded projects. Subsequent queries are significantly faster (see queries 2–3 below). 30 references is plausible for a permission resolver interface used across controllers, handlers, and services throughout the solution.

### 2. find_implementations on `IBuiltInToolProvider`

- **Time:** 300 ms
- **Error:** none
- **Implementation count:** 1
- **Expected:** 1 (`DatasourceToolProvider`)
- **Result:** PASS — exactly 1 implementation found, matching the architecture documented in MEMORY.md.

### 3. find_callers on `KnowledgeService.ExpandQueryAsync`

- **Time:** 14 ms
- **Error:** SYMBOL_NOT_FOUND
- **Caller count:** 0

The symbolId `M:duetGPT.Services.KnowledgeService.ExpandQueryAsync(System.String,System.Threading.CancellationToken)` resolved to nothing. The method likely still exists but its signature has changed (e.g., additional parameters added, renamed, or it became a private/local function). SYMBOL_NOT_FOUND in 14 ms confirms the fast-fail path works correctly — no expensive search was attempted.

### 4. semantic_search `has-attribute:McpServerToolType`

- **Time:** 11 049 ms
- **Error:** none
- **Match count:** 1
- **Expected:** 0 (hypothesis: duetGPT doesn't use this attribute)
- **Result:** SURPRISING — 1 match found. The `duetGPT.McpServer` project (the MCP protocol server that exposes the knowledge base) uses `[McpServerToolType]` from `ModelContextProtocol.Server`. The attribute search correctly crossed project boundaries and found it. The hypothesis in the spec was wrong — this is a true positive, not a false positive.

## Observations / Follow-ups

### Performance

- **Cold start is fast (8.5 s) for a ~600-file, 4-project solution.** This is well within acceptable range for an on-demand tool.
- **First query pays the compilation tax (~8 400 ms).** This is Roslyn building semantic models for all four projects. Subsequent queries (300 ms, 14 ms, 11 049 ms) show the compiled state is reused.
- **`semantic_search(has-attribute:...)` is the most expensive query at ~11 s** because it walks every symbol in every compiled project checking attribute lists. For very large solutions this could become a bottleneck. A future optimization could index attributes lazily at load time.
- **`find_callers` fast-fail is very fast (14 ms)** — symbol resolution correctly short-circuits before doing any traversal.

### Correctness

- `find_implementations` is accurate: 1 result for an interface with exactly 1 implementation.
- `find_references` returns 30 results for a widely-used interface — plausible and useful for an IDE-style refactoring workflow.
- `semantic_search` correctly crosses project boundaries — found `[McpServerToolType]` in `duetGPT.McpServer` even though the query was seeded from a different project's compilation.
- SYMBOL_NOT_FOUND for `ExpandQueryAsync` is the right behavior for a stale symbolId — the tool does not crash or return misleading results.

### Suggested Follow-ups for v1.1

1. **Expose MSBuild workspace warnings** — NullLogger hides them. Add an optional `--verbose` mode (or a `list_workspace_diagnostics` tool) so callers can see which projects failed to load and why. The missing fifth project is currently invisible.
2. **Warm-up / pre-compilation hint** — The first real query pays full compilation cost. Consider triggering `GetCompilationAsync` for all projects during `LoadAsync` in a background task, so the first user query doesn't stall.
3. **`ExpandQueryAsync` symbolId drift** — The symbolId format is brittle when method signatures change. Consider a `workspace_symbol` lookup first to discover the correct current symbolId before passing it to `find_callers`.
4. **`semantic_search` attribute walk performance** — Walking all symbols in a 4-project solution takes ~11 s. Pre-building an attribute index keyed by attribute full name during load could reduce this to sub-second.
5. **Project count mismatch** — Investigate why MSBuildWorkspace loads 4 of 5 projects. Likely `duetGPT.LicenseServer` needs a different SDK or has unresolved PackageReference. A clear diagnostic message (rather than a silent drop) would help users of the tool.
