using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Contracts;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record ReloadResult(bool Loaded, int ProjectCount, long DurationMs);

[McpServerToolType]
internal sealed class ReloadWorkspaceTool(IWorkspaceService ws, ILogger<ReloadWorkspaceTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "reload_workspace")]
    [Description("Re-runs MSBuild evaluation on the solution. Call after .csproj/.sln changes.")]
    public Task<ToolResult<ReloadResult>> InvokeAsync(CancellationToken ct)
        => ExecuteAsync(async ct2 =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Workspace.ReloadAsync(ct2);
            return ToolResult<ReloadResult>.Ok(
                new ReloadResult(true, Workspace.LoadedProjectCount, sw.ElapsedMilliseconds));
        }, ct);
}
