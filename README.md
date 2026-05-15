# mcpRoslyn

MCP server exposing C# symbol-level navigation (find-references, goto-definition, find-implementations, semantic-search, etc.) to AI coding agents. Wraps Roslyn's `MSBuildWorkspace` and serves over stdio.

**Status:** pre-implementation. Design is approved and lives at [`docs/plans/2026-05-15-mcproslyn-design.md`](docs/plans/2026-05-15-mcproslyn-design.md).

## Wiring into Claude Code

After running `dotnet publish` (see below), add to your project's `.mcp.json`:

````json
{
  "mcpServers": {
    "mcpRoslyn": {
      "command": "c:\\projects\\mcpRoslyn\\bin\\publish\\mcpRoslyn.exe",
      "args": ["--solution", "c:\\path\\to\\your.sln"]
    }
  }
}
````

## Building

```powershell
dotnet publish c:\projects\mcpRoslyn\src\mcpRoslyn -c Release -o c:\projects\mcpRoslyn\bin\publish
```

Produces `mcpRoslyn.exe` as a single-file self-contained win-x64 binary at the publish path.
The `BuildHost-netcore\` and `BuildHost-net472\` directories alongside the exe are required — they contain
the Roslyn MSBuild host process that the workspace loader spawns separately at runtime.
