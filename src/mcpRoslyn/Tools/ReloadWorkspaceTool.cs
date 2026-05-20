using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Contracts;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record ReloadResult(
    bool Loaded,
    int ProjectCount,
    long DurationMs,
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics);

[McpServerToolType]
internal sealed class ReloadWorkspaceTool(IWorkspaceService ws, ILogger<ReloadWorkspaceTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "reload_workspace")]
    [Description("Re-runs MSBuild evaluation on the solution. Call after .csproj/.sln changes. Returned diagnostics list contains any projects that failed to load.")]
    public Task<ToolResult<ReloadResult>> InvokeAsync(
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Workspace.ReloadAsync(ct2);
            var result = new ReloadResult(
                Loaded: true,
                ProjectCount: Workspace.LoadedProjectCount,
                DurationMs: sw.ElapsedMilliseconds,
                Diagnostics: Workspace.Diagnostics);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return ToolResult<ReloadResult>.OkSummary($"reloaded {result.ProjectCount} projects");
            return ToolResult<ReloadResult>.Ok(result);
        }, ct);
}
