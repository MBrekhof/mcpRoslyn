# mcpRoslyn — Design

- **Date:** 2026-05-15
- **Status:** Approved, pre-implementation
- **Author:** brainstormed with Claude Code (Opus 4.7)

## Goal

Provide symbol-level navigation over C# solutions to AI coding agents (Claude Code in particular), replacing text-search heuristics (Grep/Glob) with authoritative Roslyn semantic queries for refactor planning, reference auditing, and structural analysis across large .NET codebases.

Generic and reusable across any .sln/.csproj on the local machine. First validation target is `duetGPT.sln` (598 .cs files, 5 projects).

## Non-goals

- Not a code formatter, linter, or LSP-language-server replacement for IDE use.
- Not multi-tenant or networked. Local stdio MCP only.
- Not cross-language. C# (and incidentally F#/VB.NET via Roslyn) only — no plans to wrap gopls, pyright, etc.
- Not for editing files autonomously. `rename_symbol` returns a preview by default; the only path to writing files is an explicit `applyEdits: true` flag.
- Not cross-platform in v1. Windows only.

## Architecture

```
┌─────────────────┐    stdio JSON-RPC    ┌──────────────────────────┐
│  Claude Code    │ ───────────────────► │  mcpRoslyn (dotnet exe)  │
│  (any session)  │ ◄─────────────────── │                          │
└─────────────────┘                      │ ┌──────────────────────┐ │
                                         │ │ ModelContextProtocol │ │
                                         │ │ SDK (stdio host)     │ │
                                         │ └──────────┬───────────┘ │
                                         │            │             │
                                         │ ┌──────────▼───────────┐ │
                                         │ │ Tool handlers        │ │
                                         │ │ (one class per tool) │ │
                                         │ └──────────┬───────────┘ │
                                         │            │             │
                                         │ ┌──────────▼───────────┐ │
                                         │ │ WorkspaceService     │ │
                                         │ │ (singleton, holds    │ │
                                         │ │  MSBuildWorkspace)   │ │
                                         │ └──────────┬───────────┘ │
                                         │            │             │
                                         │ ┌──────────▼───────────┐ │
                                         │ │ Roslyn               │ │
                                         │ │ (MSBuildWorkspace,   │ │
                                         │ │  Solution, Compila-  │ │
                                         │ │  tion, SemanticModel)│ │
                                         │ └──────────────────────┘ │
                                         └──────────────────────────┘
```

**Flow per request:**

1. Claude Code spawns `mcpRoslyn.exe --solution <path>` once per session via `.mcp.json`.
2. Startup: `MSBuildLocator.RegisterDefaults()` → `MSBuildWorkspace.OpenSolutionAsync` → workspace cached in DI singleton. Cold start logged to stderr (expected 5–30s on a duetGPT-sized solution).
3. Tool call arrives → SDK routes to handler → handler asks `WorkspaceService` for the current `Solution`.
4. `WorkspaceService.GetFreshSolutionAsync()` walks loaded `Document`s, compares file mtimes against its cached mtime, and `WithDocumentText`-replaces any document whose underlying .cs has changed on disk. Returns the refreshed `Solution`.
5. Handler runs Roslyn API calls against the semantic model and returns structured JSON results.

**Key invariants:**

- One MSBuild evaluation per process lifetime (the expensive step).
- Per-call refresh touches only changed files via mtime check — typically 0–3 files; full re-parse cost ~5–20 ms per changed file. Untouched files reuse their existing syntax trees and semantic data.
- Project-file changes (.csproj edits, new files added on disk, deleted files, new projects) require `reload_workspace`; the mtime walk does not auto-detect them.

## Stack

- **.NET 10**, C# console exe.
- **`Microsoft.CodeAnalysis.Workspaces.MSBuild`** (Roslyn) for code analysis.
- **`ModelContextProtocol`** NuGet (Microsoft's official MCP C# SDK) for protocol + stdio host.
- **`Microsoft.Extensions.Hosting`** for DI + lifetime.
- **NUnit + FluentAssertions** for tests (matches duetGPT convention).

**Approach chosen:** SDK + DI hosted service. Project scaffold bootstrapped via the `csharp-mcp-server-generator` skill for csproj/Program.cs/stdio boilerplate; Roslyn tool handlers hand-written on top.

## Project structure

```
c:\projects\mcpRoslyn\
├── mcpRoslyn.sln
├── README.md
├── .gitignore
├── .editorconfig
├── docs\
│   └── plans\
│       └── 2026-05-15-mcproslyn-design.md   (this file)
├── src\
│   └── mcpRoslyn\
│       ├── mcpRoslyn.csproj
│       ├── Program.cs
│       ├── Options\
│       │   └── McpRoslynOptions.cs
│       ├── Workspace\
│       │   ├── IWorkspaceService.cs
│       │   ├── WorkspaceService.cs
│       │   └── MtimeCache.cs
│       ├── Tools\
│       │   ├── ToolBase.cs
│       │   ├── FindReferencesTool.cs
│       │   ├── GotoDefinitionTool.cs
│       │   ├── WorkspaceSymbolTool.cs
│       │   ├── HoverTool.cs
│       │   ├── FindImplementationsTool.cs
│       │   ├── FindDerivedTypesTool.cs
│       │   ├── ListDocumentSymbolsTool.cs
│       │   ├── GetCompilationErrorsTool.cs
│       │   ├── GetDocumentDiagnosticsTool.cs
│       │   ├── FindCallersTool.cs
│       │   ├── SemanticSearchTool.cs
│       │   ├── RenameSymbolTool.cs
│       │   └── ReloadWorkspaceTool.cs
│       └── Contracts\
│           ├── SymbolLocation.cs
│           ├── SymbolInfo.cs
│           └── DiagnosticInfo.cs
└── tests\
    └── mcpRoslyn.Tests\
        ├── mcpRoslyn.Tests.csproj
        ├── Fixtures\
        │   └── TestSolution\
        ├── WorkspaceServiceTests.cs
        └── ToolTests\
```

Single-project layout under `src\`. No premature split into Core/Server/Contracts assemblies — refactor only if a future caller wants to consume `WorkspaceService` as a library.

## Tool surface (12 tools)

Every tool returns structured JSON. Locations use the shared `SymbolLocation` contract: `{ filePath, line, column, endLine, endColumn, snippet }`. `line`/`column` are 1-based (editor convention, not Roslyn's 0-based). Symbol identifiers use Roslyn's `DocumentationCommentId` format (e.g. `M:Namespace.Class.Method(System.String)`).

Every navigation tool accepts EITHER `{ filePath, line, column }` (cursor-style) OR `{ symbolId }` (doc-comment ID). The first form is how Claude Code uses it after reading a file; the second lets a follow-up call chain off a prior tool's result without re-resolving by position.

### Navigation

| Tool | Purpose | Input | Output | Roslyn API |
|---|---|---|---|---|
| `find_references` | Every reference site for a symbol | `{ filePath, line, column }` OR `{ symbolId }` | `{ symbol: SymbolInfo, references: SymbolLocation[] }` | `SymbolFinder.FindReferencesAsync` |
| `goto_definition` | Where a symbol is declared | `{ filePath, line, column }` | `{ definitions: SymbolLocation[] }` (array — partial classes) | `SymbolFinder.FindSourceDeclarationsAsync` |
| `workspace_symbol` | Fuzzy name search across solution | `{ query, kinds?, maxResults? }` | `{ symbols: SymbolInfo[] }` | `SymbolFinder.FindSourceDeclarationsWithPatternAsync` |
| `hover` | Type/signature info at cursor | `{ filePath, line, column }` | `{ symbol: SymbolInfo, xmlDocSummary?, signature }` | `SemanticModel.GetSymbolInfo` + `ToDisplayString` |

### Structure

| Tool | Purpose | Input | Output | Roslyn API |
|---|---|---|---|---|
| `find_implementations` | Implementations of an interface/abstract member | `{ filePath, line, column }` OR `{ symbolId }` | `{ implementations: SymbolLocation[] }` | `SymbolFinder.FindImplementationsAsync` |
| `find_derived_types` | Subclasses / interface implementers | `{ ..., transitive? }` | `{ derivedTypes: SymbolInfo[] }` | `SymbolFinder.FindDerivedClassesAsync` / `FindDerivedInterfacesAsync` |
| `list_document_symbols` | Outline of one file | `{ filePath }` | `{ symbols: SymbolInfo[] }` with nesting via `containingType` | Syntax-tree walk |

### Callers

| Tool | Purpose | Input | Output | Roslyn API |
|---|---|---|---|---|
| `find_callers` | Methods invoking a given method | `{ ..., transitive? }` | `{ callers: { caller: SymbolInfo, callSite: SymbolLocation }[] }` | `SymbolFinder.FindCallersAsync` |

### Diagnostics

| Tool | Purpose | Input | Output | Roslyn API |
|---|---|---|---|---|
| `get_compilation_errors` | Solution-wide error list | `{ severity?, projectName? }` | `{ diagnostics: DiagnosticInfo[] }` | `Compilation.GetDiagnostics()` |
| `get_document_diagnostics` | Errors/warnings in one file | `{ filePath, severity? }` | `{ diagnostics: DiagnosticInfo[] }` | `SemanticModel.GetDiagnostics()` |

### Search

| Tool | Purpose | Input | Output |
|---|---|---|---|
| `semantic_search` | Pattern queries Roslyn can answer but Grep cannot | `{ pattern }` | `{ matches: SymbolInfo[] }` |

Supported `pattern` grammar (v1):

- `derives-from:Namespace.BaseClass` — all types deriving from BaseClass
- `implements:Namespace.IInterface` — all types implementing IInterface
- `has-attribute:Namespace.MyAttribute` — types/members with the attribute
- `returns:Namespace.Type` — methods returning Type
- `parameter-type:Namespace.Type` — methods taking Type as parameter

### Editing

| Tool | Purpose | Input | Output | Roslyn API |
|---|---|---|---|---|
| `rename_symbol` | Rename across solution | `{ filePath, line, column, newName, applyEdits?: false }` | `{ edits: [...], conflicts?: string[] }` | `Renamer.RenameSymbolAsync` |

`applyEdits` defaults to `false` — returns the would-be edits as a preview. The caller (Claude Code) reviews then either applies via its own `Edit` tool or sets `applyEdits: true` for in-place write. Destructive boundary stays explicit: mcpRoslyn never writes files unless asked.

### Lifecycle

| Tool | Purpose | Input | Output |
|---|---|---|---|
| `reload_workspace` | Full MSBuild re-evaluation after .csproj/.sln changes | `{}` | `{ loaded: true, projectCount, durationMs, warnings? }` |

## Workspace lifecycle & freshness

### Startup sequence (`Program.cs`)

1. Parse args. Required: `--solution <path>`. Optional: `--log-level <Debug|Info|Warning|Error>` (default `Info`, written to stderr; stdout is reserved for MCP traffic), `--msbuild-path <path>` (default: `MSBuildLocator.RegisterDefaults()`).
2. `MSBuildLocator.RegisterDefaults()` — picks the .NET 10 SDK on PATH. Must run before any Roslyn type touches the AppDomain.
3. Build DI host, register `IWorkspaceService` as singleton.
4. `WorkspaceService.LoadAsync(solutionPath)` runs eagerly (not lazy on first tool call) — fail fast if the solution doesn't load. Log timing + per-project warnings.
5. Hand off to `ModelContextProtocol` SDK to serve stdio.

### `WorkspaceService` state

```csharp
MSBuildWorkspace _workspace;         // created once, kept open
Solution _solution;                  // replaced each call to GetFreshSolutionAsync
Dictionary<DocumentId, DateTime> _mtimeCache;
SemaphoreSlim _refreshLock;          // single-flight per call
```

### Per-call refresh

```
acquire _refreshLock
  for each Document in _solution.Projects.SelectMany(p => p.Documents):
    skip if Document.FilePath is null (generated / in-memory)
    statedMtime = File.GetLastWriteTimeUtc(Document.FilePath)
    cachedMtime = _mtimeCache[Document.Id]
    if statedMtime > cachedMtime:
      newText = SourceText.From(await File.ReadAllTextAsync(Document.FilePath))
      _solution = _solution.WithDocumentText(Document.Id, newText)
      _mtimeCache[Document.Id] = statedMtime
  return _solution
release lock
```

### Cost model

| Phase | Expected cost |
|---|---|
| Cold start (`OpenSolutionAsync`) | 5–30 s for duetGPT-sized solutions |
| Per-call refresh, no files changed | 5–15 ms (mtime stats only) |
| Per-call refresh, N changed | 5–15 ms + ~5–20 ms × N for re-parse |
| Semantic re-bind | Lazy, scoped to what the query touches |

### What we do not auto-detect

- New .cs files added on disk (not yet in any Document)
- Deleted .cs files
- .csproj changes (references, package versions, target framework)
- New projects added to .sln

All four require `reload_workspace`. The mtime walk operates only on already-known Documents.

### Project-load warnings

`MSBuildWorkspace.Diagnostics` is inspected after every load. Errors trigger a stderr log line per project; the count of successfully loaded projects vs. total is surfaced in `reload_workspace` output. Tool calls still serve from whatever loaded — one broken project does not fail the whole server.

## Configuration / startup

### Distribution

`dotnet publish -r win-x64 --self-contained` produces a portable exe at `c:\projects\mcpRoslyn\bin\publish\mcpRoslyn.exe`. One-file, works across all repos, easy to upgrade by re-running publish.

(`dotnet tool` packaging considered and rejected for v1 — needs a feed setup that isn't worth the indirection.)

### Per-project wiring

Each consuming repo adds an `.mcp.json` entry:

```json
{
  "mcpServers": {
    "mcpRoslyn": {
      "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe",
      "args": ["--solution", "c:\\projects\\duetgpt\\duetGPT\\duetGPT.sln"]
    }
  }
}
```

## Error handling

Three layers:

1. **Protocol layer** — handled by the MCP SDK. Malformed JSON-RPC, unknown method names return standard MCP errors.
2. **Tool envelope (`ToolBase`)** — catches `OperationCanceledException` (request abort), `FileNotFoundException`, `InvalidOperationException` from the Roslyn workspace, and any unhandled `Exception`. Returns `{ error: { code, message, hint? } }`.
3. **Empty-result handling** — when Roslyn legitimately returns no results (e.g. `find_references` on an unused symbol), the tool returns an empty array, not an error. Empty is not failure.

### Error codes

- `WORKSPACE_NOT_LOADED` — startup hasn't completed (rare; eager load makes this nearly impossible)
- `FILE_NOT_IN_WORKSPACE` — caller passed a `filePath` outside the loaded solution
- `SYMBOL_NOT_FOUND` — position doesn't resolve to a symbol
- `POSITION_INVALID` — line/column out of bounds for the file
- `INVALID_PATTERN` — `semantic_search` got a malformed pattern string
- `RENAME_CONFLICT` — Roslyn renamer reports unresolvable conflicts
- `INTERNAL_ERROR` — anything else; full stack trace logged to stderr

The `hint` field carries actionable suggestions, e.g. `"Did you mean to call reload_workspace? The file isn't in the loaded solution."`

### stdout discipline

stdout is sacred — only MCP JSON-RPC frames. Any rogue `Console.WriteLine` would corrupt the protocol stream. All diagnostic output goes through an `ILogger` wired explicitly to `Console.Error` in `Program.cs`.

## Testing

### Fixture solution

`tests\mcpRoslyn.Tests\Fixtures\TestSolution\` — a hand-crafted solution committed to the repo:

- `TestSolution.sln` referencing `TestLib.csproj` (library) and `TestApp.csproj` (console).
- ~6 .cs files covering: interface + 2 implementations, abstract class + 2 derived, partial class across 2 files, generic method, attribute with multi-target usage, a class with deliberate compile errors (for diagnostics tests).

Stable, version-controlled — tests assert exact reference counts and symbol IDs against it.

### Test classes (NUnit + FluentAssertions)

- `WorkspaceServiceTests` — load fixture, modify a fixture file on disk between calls, verify refresh picks it up; verify project-file change is NOT auto-detected; verify `reload_workspace` re-evaluates.
- One file per tool under `ToolTests\`:
  - `FindReferencesToolTests` — known references count = expected, including occurrences in XML doc comments
  - `GotoDefinitionToolTests`
  - `WorkspaceSymbolToolTests`
  - `HoverToolTests`
  - `FindImplementationsToolTests`
  - `FindDerivedTypesToolTests`
  - `ListDocumentSymbolsToolTests`
  - `GetCompilationErrorsToolTests`
  - `GetDocumentDiagnosticsToolTests`
  - `FindCallersToolTests`
  - `SemanticSearchToolTests` — one happy case per pattern type + one invalid-pattern returning structured error
  - `RenameSymbolToolTests` — preview returns expected edit set; `applyEdits: true` writes files (test cleans up after); rename to existing member name surfaces `RENAME_CONFLICT`
- `ReloadWorkspaceToolTests` — verifies a fresh MSBuild evaluation occurs and project count is reported.

Roslyn is **not mocked**. Its API surface is too large to mock usefully; tests exercise the real workspace against the fixture solution.

### Not in scope for v1 tests

- Performance benchmarks
- Real-world `duetGPT.sln` integration (it's a moving target — manual smoke after each release instead)
- Cross-platform (Windows only)

### Manual acceptance per release

Run mcpRoslyn against `duetGPT.sln`, verify four representative queries return correct results:

1. `find_references` on `IGroupPermissionResolver`
2. `find_implementations` on `IBuiltInToolProvider`
3. `find_callers` on `KnowledgeService.ExpandQueryAsync`
4. `semantic_search has-attribute:McpServerToolType`

## Open questions / deferred

- **`dotnet tool` packaging** — revisit if/when we want to share mcpRoslyn outside the local machine.
- **HTTP/SSE transport** — deferred; rerun the cost-of-cold-start vs. complexity tradeoff once we have real session data.
- **Cross-platform** — Linux/Mac CI deferred until there's a real user.
- **Wider semantic_search grammar** — current 5 patterns are a starting set; expand based on observed gaps in real sessions.
- **LSP-bridge factor-out** — if a future need to wrap gopls/pyright/rust-analyzer surfaces, the `WorkspaceService` interface can be re-shaped behind a more abstract `ISymbolProvider`. Not building that abstraction now.

## Validation plan

After implementation (separate writing-plans phase will detail steps):

1. **Synthetic** — fixture-based unit tests, ~12 test classes, NUnit. All green before manual.
2. **Manual** — point at `duetGPT.sln`, run the 4 acceptance queries above. Compare results against ground truth from a manual Grep + IDE F12.
3. **Real-session** — use mcpRoslyn in a real duetGPT session for one feature-sized task. Note any missing tool / wrong shape / cold-start friction. Feed back into v1.1.
