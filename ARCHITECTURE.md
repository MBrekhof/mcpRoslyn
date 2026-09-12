# Architecture — mcpRoslyn

Windows-only .NET 10 MCP server exposing 19 Roslyn-backed code-intelligence tools over stdio. Wraps `MSBuildWorkspace` so AI coding agents can run authoritative semantic queries (find-references, goto-definition, find-implementations, semantic-search, rename) instead of text-search heuristics.

## Topology

```
Claude Code  ──stdio JSON-RPC──►  mcpRoslyn.exe
                                    │
                                    ├─ ModelContextProtocol SDK (stdio host)
                                    ├─ Tool handlers (one class per tool, [McpServerToolType])
                                    ├─ WorkspaceService (singleton)
                                    └─ Roslyn (MSBuildWorkspace, Solution, Compilation, SemanticModel)
```

One process per Claude Code session, spawned via `mcp.json`. Solution loaded once at startup; kept live for the session.

## Process lifecycle

1. `MSBuildLocator.RegisterDefaults()` **first**, before any `Microsoft.CodeAnalysis.*` type is touched (otherwise MSBuild assembly resolution fails).
2. Parse args: `--solution <path>` (optional — falls back to walking up from CWD for `*.sln`/`*.slnx`); `--log-level <Debug|Info|Warning|Error>`.
3. DI host built; `IWorkspaceService` registered as singleton.
4. `WorkspaceLoaderHostedService` triggers eager `WorkspaceService.LoadAsync` — fail-fast if the solution doesn't load.
5. MCP SDK takes over stdio.

stdout is reserved for MCP frames. All logging goes to **stderr** via `ILogger` (`LogToStandardErrorThreshold = LogLevel.Trace`). A rogue `Console.WriteLine` anywhere will corrupt the protocol stream.

## WorkspaceService

State:

- `Generation _current` — one load's `MSBuildWorkspace`, `SymbolIndex`, `InvocationIndex`, warm-up task and its cancellation. Built locally and published in one step only after the solution opened (WS-006): a failed reload leaves the previous generation serving, and a warm-up only ever builds into its own generation's indexes. A replaced generation's warm-up is cancelled and its workspace disposed after a 30 s grace.
- `Solution _solution` — immutable snapshot of the current generation; reassigned each refresh.
- `Dictionary<DocumentId, DateTime> _mtimeCache` — last-seen mtime per doc, rebuilt with each generation.
- `SemaphoreSlim _gate` — serializes load/reload/refresh.

Indexed tools go through `GetIndexedSolutionAsync`, which waits for the current generation's warm-up (IDX-002) and returns the refreshed solution together with that same generation's indexes, re-targeting a successor if a reload lands mid-wait — so a tool never pairs one load's solution with another's index. The first indexed query after start or reload blocks until the index is built instead of answering from a partial one, and a failed build surfaces as `INDEX_UNAVAILABLE`. A disposed service disposes every generation, including retired ones still in their grace period. The `SymbolIndex` / `InvocationIndex` properties return the possibly-unfinished index and exist for tests.

Per-call refresh (`GetFreshSolutionAsync`): walk every known `Document`, compare disk mtime to cache, `WithDocumentText` for changed files only. Untouched files reuse existing syntax trees and semantic data. Typical cost: 5–15 ms when nothing changed; +5–20 ms per changed file.

**Not auto-detected** (require `reload_workspace`): new .cs files on disk, deleted .cs files, .csproj edits, new projects in .sln.

### SymbolIndex

A sibling `SymbolIndex` (built during warm-up, owned by `WorkspaceService`, exposed via `IWorkspaceService.SymbolIndex`) backs the `has-attribute:`, `returns:`, and `parameter-type:` patterns of `semantic_search`. Three dictionaries keyed by display string AND fully-qualified metadata name, populated by a parallel-per-project walk after `WarmupAsync`'s compilations finish.

Queries hit the dictionary in O(matches). Always-fresh semantics are preserved via a dirty-doc set populated whenever `GetFreshSolutionAsync` calls `WithDocumentText`: the query path filters out cached entries whose `DeclaringDocs` intersect the dirty set, then walks just the dirty documents fresh and merges results. `derives-from:` / `implements:` bypass the index — they use Roslyn's `FindDerivedClassesAsync` / `FindImplementationsAsync`, which are already O(matches).

`SymbolIndex` is reconstructed (dirty set discarded) on `ReloadAsync`. The class is `public sealed` because it's exposed on the public `IWorkspaceService` interface, but consumers should treat it as an implementation detail of `semantic_search`.

The build walks **`compilation.Assembly.GlobalNamespace`, not `compilation.GlobalNamespace`** — the latter merges every referenced assembly, so the walk covered the entire BCL and every package before discarding the results (a symbol with no source-declaring document is dropped). Using the wrong root cost ~20–27× for an identical index; see PERF-001. It also meant each symbol was indexed once per *referencing* project, since a project reference brings the referenced project's source symbols into the referencing compilation.

`AllSymbols(solution, ct)` — the flat enumeration behind `find_dead_code_candidates` — goes through the same `MergeWithDirtyWalk` as the pattern queries, so it sees post-build edits and returns symbol-id-deduplicated results. It takes the current `Solution` for that reason; there is no parameterless overload.

### InvocationIndex

A sibling `InvocationIndex` (built during warm-up, owned by `WorkspaceService`, exposed via `IWorkspaceService.InvocationIndex`) backs `find_entrypoints` and `find_registrations`. It walks `InvocationExpressionSyntax` in each project's syntax trees, classifying calls into four buckets: routes (`MapGet`/`MapPost`/...), middleware (`Use*` on `IApplicationBuilder`/`WebApplication`), hosted services (`AddHostedService<T>` + `BackgroundService` subclasses), DI registrations (`AddSingleton`/`AddTransient`/`AddScoped` + an `Unclassified[]` bucket for `IServiceCollection` extension calls that don't match the known forms).

Calls are found syntactically by method name, then confirmed against the semantic model: DI registrations and `AddHostedService<T>` must be `IServiceCollection` extensions (called as extensions or statically) or members called on an `IServiceCollection`; middleware must be called on `IApplicationBuilder`/`WebApplication`; routes must be `IEndpointRouteBuilder` extensions that take a handler (`Delegate`/`RequestDelegate`), called either way — so a look-alike such as AutoMapper's `Map<T>(source)` is not an endpoint (TOOL-009). Route arguments are read by parameter, not position, including escaped named arguments: `MapMethods` reports its HTTP methods as the verb (`GET,HEAD`) only when every one is a string literal, otherwise `ANY`. `BackgroundService` subclasses are found per document from `ClassDeclarationSyntax` declared symbols and their base chain, in the same per-document pass the dirty re-walk reuses, so a refresh re-finds them (IDX-003); a partial subclass is recorded by each declaring file and reported once. Agents stay informed of unrecognised DI surface via the `Unclassified[]` array. Lifecycle and dirty-doc handling mirror `SymbolIndex`. Reconstructed on `ReloadAsync`.

## Tool surface (20 tools, plus `echo`)

Every tool returns structured JSON wrapped in `ToolResult<T>` (`Result` or `Error`). Locations use 1-based line/column. Symbol identifiers use Roslyn's `DocumentationCommentId` format. Navigation tools accept either `{ filePath, line, column }` or `{ symbolId }`.

| Category | Tools |
|---|---|
| Navigation | `find_references`, `goto_definition`, `workspace_symbol`, `hover` |
| Structure | `find_implementations`, `find_derived_types`, `list_document_symbols` |
| Callers / Callees | `find_callers`, `find_callees` |
| Diagnostics | `get_compilation_errors`, `get_document_diagnostics` |
| Search | `semantic_search` (patterns: `derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) |
| Composite | `analyze_symbol` (hover + refs + impls + derived + callers in one call) |
| Architecture | `project_overview`, `find_entrypoints`, `find_registrations` |
| Tests | `test_map` (production → test heuristic) |
| Cleanup | `find_dead_code_candidates` (private/internal members with confidence + denylist; `includePublicTypes: true` adds unreferenced public **types**, suppressing DI-registered and framework-reached ones) |
| Editing | `rename_symbol` (preview by default; `applyEdits: true` to write) |
| Lifecycle | `reload_workspace` |

`rename_symbol` is the **only** path to file writes. Default `applyEdits: false` returns a preview; the caller decides whether to apply.

## Cross-cutting conventions

### `format` parameter

Every tool accepts `format = "structured" | "summary"` (default `structured`). `ToolResult<T>` carries an optional `Summary` field; in summary mode `Result` is null and `Summary` holds a one-line human description. Errors are always structured. Backwards-compatible with v1.2 callers.

### Response size (PERF-002)

Every response is spent from the agent's context budget, so response size is measured like latency: `BenchmarkTests.Tool_response_sizes` calls all 20 tools against a real solution over the stdio transport and records chars/tokens per tool ([2026-09-13 results](docs/acceptance/2026-09-13-perf-002-response-sizes.md)). List-returning tools cap by default and say so: `workspace_symbol` (`maxResults` 25) and `semantic_search` (`maxResults` 50) return `Truncated: true` only when a further match existed; the architecture tools carry a `Truncated` section list. `rename_symbol`'s preview diffs the documents' syntax trees (`Document.GetTextChangesAsync`), so edits are the changed spans — one per occurrence on BPG — rather than whole-file text.

### Diagnostics filter knobs

`get_compilation_errors` and `get_document_diagnostics` accept `includeGenerated`, `minimumSeverity` (default `"Warning"`), `excludeDiagnosticCodes`, `excludeDiagnosticSources`. Pure post-filter at collection time, never affects how diagnostics are read from Roslyn.

### Analyzer diagnostics (DIAG-001)

Both diagnostics tools can also run the project's own analyzers — `Project.AnalyzerReferences` as MSBuild resolved them (NetAnalyzers, StyleCop, Roslynator, …) — through `CompilationWithAnalyzers` (`RoslynHelpers.WithProjectAnalyzers`). Severities are the ones the project's `.editorconfig`/globalconfig set, and pragma- or `SuppressMessage`-suppressed diagnostics are not reported, so the answer is "does it pass this project's bar", not the rules' defaults.

The default differs by scope, set from measurements on BPG (8 projects, 2026-09-12, `BenchmarkTests.Diagnostics_analyzer_cost`):

| Tool | Analyzers | Compiler-only | With analyzers |
|---|---|---|---|
| `get_document_diagnostics` | **on** (`includeAnalyzers: false` to skip) | median 9 ms | median 225 ms, max 780 ms; first call ~0.9 s (analyzer assembly load) |
| `get_compilation_errors` | **opt-in** (`includeAnalyzers: true`) | ~45 ms, 548 diagnostics | ~2.4 s (~52x), 1216 diagnostics |

Per file, analysis is scoped to one syntax tree (syntax + semantic analyzer diagnostics), so compilation-end rules — whole-project checks — are not run there; the solution-wide call runs them. Nothing is cached: each call builds a fresh `CompilationWithAnalyzers`, and nothing runs during warm-up.
When analyzers are enabled and a loaded `DiagnosticSuppressor` supports suppressing one of the file's compiler diagnostic IDs, a whole-compilation pass containing only the project's suppressors runs with the same analyzer options and suppressed diagnostics excluded, replacing the file's compiler diagnostics with that pass's results for its syntax tree.

Analyzer execution failures are returned as Roslyn's `AD0001` diagnostics alongside the other results. Per-file calls collect them through the analyzer exception callback because the tree-scoped APIs omit these non-local diagnostics; their configured severity and suppression still apply. Analyzer assembly load failures are separate (DIAG-002).

## Error handling

Three layers:

1. **Protocol** — MCP SDK handles malformed JSON-RPC.
2. **Tool envelope** (`ToolBase.ExecuteAsync`) — catches `FileNotFoundException`, `InvalidOperationException`, generic `Exception`; returns `ToolError { code, message, hint? }`.
3. **Empty results** — `find_references` on an unused symbol returns `[]`, not an error. Empty is not failure.

Codes: `WORKSPACE_NOT_LOADED`, `FILE_NOT_IN_WORKSPACE`, `SYMBOL_NOT_FOUND`, `POSITION_INVALID`, `INVALID_PATTERN`, `RENAME_CONFLICT`, `INDEX_UNAVAILABLE`, `INTERNAL_ERROR`.

## Project layout

```
src/mcpRoslyn/
  Program.cs              # MSBuildLocator + DI + hosted-service eager-load
  Options/                # McpRoslynOptions
  Contracts/              # SymbolLocation, SymbolInfo, DiagnosticInfo, ToolError, ToolResult<T>
  Workspace/              # IWorkspaceService, WorkspaceService
  Tools/                  # one class per tool + ToolBase + RoslynHelpers
tests/mcpRoslyn.Tests/
  Fixtures/TestSolution/  # 2-project hand-crafted .sln — exact-count assertions
  TestHelpers/            # FixturePaths, TestHost (DI helper for tool tests)
  ToolTests/              # one test class per tool
  WorkspaceServiceTests, AcceptanceTests, SolutionDiscoveryTests
docs/
  plans/                  # design + implementation plan
  acceptance/             # manual acceptance logs
bin/publish/              # self-contained win-x64 exe + BuildHost dirs
```

Single-project layout under `src/`. No Core/Server/Contracts split — refactor only if a future caller wants to consume `WorkspaceService` as a library.

## Packaging

`dotnet publish src/mcpRoslyn -c Release -o bin/publish` produces a self-contained, single-file `mcpRoslyn.exe` (win-x64). The `BuildHost-net472/` and `BuildHost-netcore/` directories alongside the exe are **required** — they host the out-of-process MSBuild evaluator that Roslyn's workspace loader spawns.

## Wiring (consumer side)

Global (`~/.claude/mcp.json`) — discovers `.sln`/`.slnx` by walking up from CWD:

```json
{ "mcpServers": { "mcpRoslyn": { "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe" } } }
```

Per-project (`.mcp.json`) — pin to a specific solution when the repo has multiple:

```json
{ "mcpServers": { "mcpRoslyn": { "command": "...mcpRoslyn.exe", "args": ["--solution", "c:\\path\\to\\specific.sln"] } } }
```

## Testing

- NUnit + FluentAssertions. Roslyn is **not mocked** — tests exercise the real workspace against the fixture solution.
- Fixture solution lives in `tests/.../Fixtures/TestSolution/` and is copied to test output via `<None Include CopyToOutputDirectory="PreserveNewest">`. It is **not** built by the test project's MSBuild.
- One test class per tool under `ToolTests/`. `TestHost.CreateAsync<T>` builds a minimal DI graph around `WorkspaceService` pointed at the fixture.
- Tests that mutate fixture files MUST restore them in `finally` blocks.
- Manual acceptance runs against `duetGPT.sln` (see `docs/acceptance/`).

## Non-goals

- Not an LSP server, formatter, or linter.
- Not multi-tenant or networked — local stdio only.
- Not cross-language (C# / VB / F# via Roslyn only).
- Not cross-platform in v1 — Windows only.
- Never writes files except via explicit `rename_symbol applyEdits: true`.
