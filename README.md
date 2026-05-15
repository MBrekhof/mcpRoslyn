# mcpRoslyn

MCP server exposing C# symbol-level navigation (find-references, goto-definition, find-implementations, semantic-search, etc.) to AI coding agents. Wraps Roslyn's `MSBuildWorkspace` and serves over stdio.

**Status:** v1 implemented and accepted. Design at [`docs/plans/2026-05-15-mcproslyn-design.md`](docs/plans/2026-05-15-mcproslyn-design.md), implementation plan at [`docs/plans/2026-05-15-mcproslyn-implementation.md`](docs/plans/2026-05-15-mcproslyn-implementation.md), acceptance log at [`docs/acceptance/2026-05-15-v1-acceptance.md`](docs/acceptance/2026-05-15-v1-acceptance.md).

## Wiring into Claude Code

mcpRoslyn supports two modes:

### Global (user-level mcp.json) — recommended for multi-project work

Register once in `~/.claude/mcp.json`:

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

## Building

```powershell
dotnet publish c:\projects\mcpRoslyn\src\mcpRoslyn -c Release -o c:\projects\mcpRoslyn\bin\publish
```

Produces `mcpRoslyn.exe` as a single-file self-contained win-x64 binary at the publish path.
The `BuildHost-netcore\` and `BuildHost-net472\` directories alongside the exe are required — they contain
the Roslyn MSBuild host process that the workspace loader spawns separately at runtime.
