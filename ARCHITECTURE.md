# Architecture — mcpRoslyn

Windows-only .NET 10 MCP server exposing 12 Roslyn-backed code-intelligence tools over stdio. Wraps `MSBuildWorkspace` so AI coding agents can run authoritative semantic queries (find-references, goto-definition, find-implementations, semantic-search, rename) instead of text-search heuristics.

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

- `MSBuildWorkspace _workspace` — created once, kept open.
- `Solution _solution` — immutable snapshot; reassigned each refresh.
- `Dictionary<DocumentId, DateTime> _mtimeCache` — last-seen mtime per doc.
- `SemaphoreSlim _gate` — serializes load/reload/refresh.

Per-call refresh (`GetFreshSolutionAsync`): walk every known `Document`, compare disk mtime to cache, `WithDocumentText` for changed files only. Untouched files reuse existing syntax trees and semantic data. Typical cost: 5–15 ms when nothing changed; +5–20 ms per changed file.

**Not auto-detected** (require `reload_workspace`): new .cs files on disk, deleted .cs files, .csproj edits, new projects in .sln.

### SymbolIndex

A sibling `SymbolIndex` (built during warm-up, owned by `WorkspaceService`, exposed via `IWorkspaceService.SymbolIndex`) backs the `has-attribute:`, `returns:`, and `parameter-type:` patterns of `semantic_search`. Three dictionaries keyed by display string AND fully-qualified metadata name, populated by a parallel-per-project walk after `WarmupAsync`'s compilations finish.

Queries hit the dictionary in O(matches). Always-fresh semantics are preserved via a dirty-doc set populated whenever `GetFreshSolutionAsync` calls `WithDocumentText`: the query path filters out cached entries whose `DeclaringDocs` intersect the dirty set, then walks just the dirty documents fresh and merges results. `derives-from:` / `implements:` bypass the index — they use Roslyn's `FindDerivedClassesAsync` / `FindImplementationsAsync`, which are already O(matches).

`SymbolIndex` is reconstructed (dirty set discarded) on `ReloadAsync`. The class is `public sealed` because it's exposed on the public `IWorkspaceService` interface, but consumers should treat it as an implementation detail of `semantic_search`.

## Tool surface (12 tools)

Every tool returns structured JSON wrapped in `ToolResult<T>` (`Result` or `Error`). Locations use 1-based line/column. Symbol identifiers use Roslyn's `DocumentationCommentId` format. Navigation tools accept either `{ filePath, line, column }` or `{ symbolId }`.

| Category | Tools |
|---|---|
| Navigation | `find_references`, `goto_definition`, `workspace_symbol`, `hover` |
| Structure | `find_implementations`, `find_derived_types`, `list_document_symbols` |
| Callers | `find_callers` |
| Diagnostics | `get_compilation_errors`, `get_document_diagnostics` |
| Search | `semantic_search` (patterns: `derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) |
| Editing | `rename_symbol` (preview by default; `applyEdits: true` to write) |
| Lifecycle | `reload_workspace` |

`rename_symbol` is the **only** path to file writes. Default `applyEdits: false` returns a preview; the caller decides whether to apply.

## Error handling

Three layers:

1. **Protocol** — MCP SDK handles malformed JSON-RPC.
2. **Tool envelope** (`ToolBase.ExecuteAsync`) — catches `FileNotFoundException`, `InvalidOperationException`, generic `Exception`; returns `ToolError { code, message, hint? }`.
3. **Empty results** — `find_references` on an unused symbol returns `[]`, not an error. Empty is not failure.

Codes: `WORKSPACE_NOT_LOADED`, `FILE_NOT_IN_WORKSPACE`, `SYMBOL_NOT_FOUND`, `POSITION_INVALID`, `INVALID_PATTERN`, `RENAME_CONFLICT`, `INTERNAL_ERROR`.

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
