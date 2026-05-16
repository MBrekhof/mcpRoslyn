# mcpRoslyn

MCP server exposing C# symbol-level navigation (find-references, goto-definition, find-implementations, semantic-search, rename, etc.) to AI coding agents. Wraps Roslyn's `MSBuildWorkspace` and serves over stdio.

**Status:** v1.1 — warm-up pre-compilation shipped (4.5× faster first query on production-sized solutions). See [`docs/acceptance/`](docs/acceptance/) for measured timings. v1 design at [`docs/plans/2026-05-15-mcproslyn-design.md`](docs/plans/2026-05-15-mcproslyn-design.md). High-level architecture summary at [`ARCHITECTURE.md`](ARCHITECTURE.md). Open work tracked in [`TODO.md`](TODO.md).

## Why

AI coding agents (Claude Code, Cursor, etc.) typically navigate codebases with text search (`grep`, ripgrep). For C# this loses precision: text search can't distinguish a usage from a definition, can't find implementations of an interface, can't resolve overloads, and can't follow renames. mcpRoslyn gives agents authoritative semantic queries backed by the same engine the C# compiler uses.

## Requirements

- **Windows.** v1 is Windows-only — `MSBuildLocator` and path-comparison code would need work for Linux/Mac.
- **.NET 10 SDK** (for build) — runtime is bundled into the published self-contained exe.
- A Claude Code installation (or any MCP-compatible client speaking stdio JSON-RPC).

## Tools (13)

| Category | Tools |
|---|---|
| Navigation | `find_references`, `goto_definition`, `workspace_symbol`, `hover` |
| Structure | `find_implementations`, `find_derived_types`, `list_document_symbols` |
| Callers | `find_callers` |
| Diagnostics | `get_compilation_errors`, `get_document_diagnostics` |
| Search | `semantic_search` (patterns: `derives-from:`, `implements:`, `has-attribute:`, `returns:`, `parameter-type:`) |
| Editing | `rename_symbol` (preview by default; `applyEdits: true` to write) |
| Lifecycle | `reload_workspace` |
| Sanity | `echo` |

Every tool returns structured JSON. Navigation tools accept either `{ filePath, line, column }` (cursor style) or `{ symbolId }` (Roslyn's `DocumentationCommentId` format). `rename_symbol` is the **only** path to file writes — default `applyEdits: false` returns a preview.

## Wiring into Claude Code

mcpRoslyn supports two modes:

### Global (user-level `~/.claude/mcp.json`) — recommended for multi-project work

Register once:

```json
{
  "mcpServers": {
    "mcpRoslyn": {
      "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe"
    }
  }
}
```

mcpRoslyn discovers the solution by walking up from Claude Code's CWD looking for `*.sln` or `*.slnx`. The first one found is loaded.

### Per-project (`.mcp.json` in the project root) — pin to one solution

Useful when a project contains multiple `.sln` files and you want to force a specific one:

```json
{
  "mcpServers": {
    "mcpRoslyn": {
      "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe",
      "args": ["--solution", "c:\\path\\to\\specific.sln"]
    }
  }
}
```

### CLI arguments

| Flag | Required | Description |
|---|---|---|
| `--solution <path>` | No | Path to a `.sln` or `.slnx`. When omitted, mcpRoslyn walks up from CWD looking for one. |
| `--log-level <level>` | No | `Debug`, `Information` (default), `Warning`, `Error`. |
| `--log-file <path>` | No | Tee `ILogger` output to a file (append mode). Useful because Claude Code only surfaces MCP stderr during the `initialize` handler — anything after that (warm-up timings, per-tool diagnostics) is otherwise lost. |

## Building

```powershell
dotnet publish c:\projects\mcpRoslyn\src\mcpRoslyn -c Release -o c:\projects\mcpRoslyn\bin\publish
```

Produces `mcpRoslyn.exe` as a single-file self-contained win-x64 binary at the publish path. The `BuildHost-netcore\` and `BuildHost-net472\` directories alongside the exe are required — they contain the Roslyn MSBuild host process that the workspace loader spawns separately at runtime.

## Performance

On `duetGPT.sln` (4 loaded projects, 598 .cs files), measured cold-start and query times:

| Metric | v1 | v1.1 | v1.2 |
|---|---|---|---|
| Solution load (`LoadAsync` return) | ~8.5 s | ~2 s | ~2–11 s (env variance; same code path) |
| Warm-up + index build (background) | n/a | ~8 s | ~23 s |
| First `find_references` | ~8.4 s | ~1.9 s | **~640 ms** |
| `find_implementations` | ~300 ms | ~830 ms | ~320 ms |
| `semantic_search has-attribute:` | ~11 s | ~7.7 s | **~11 ms** (1000× faster) |

All warm-up cost stays in the background — `LoadAsync` returns at the same time. Full detail in [`docs/acceptance/`](docs/acceptance/).

## License

MIT — see [`LICENSE`](LICENSE).
