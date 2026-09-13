using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Contracts;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record ReloadResult(
    bool Loaded,
    string SolutionPath,
    int ProjectCount,
    long DurationMs,
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics);

[McpServerToolType]
internal sealed class ReloadWorkspaceTool(IWorkspaceService ws, ILogger<ReloadWorkspaceTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "reload_workspace")]
    [Description("Re-runs MSBuild evaluation on the solution. Call after .csproj/.sln changes, or when results warn the workspace may be stale. Pass solutionPath (the full path of a .sln or .slnx) to switch to another solution; without it the solution being served is reloaded. Returns the loaded solution's path, and diagnostics for any projects that failed to load.")]
    public Task<ToolResult<ReloadResult>> InvokeAsync(
        string? solutionPath = null,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            // Checked here, not left to MSBuild: a failed open keeps the current solution serving, but the error
            // it throws for a relative, missing or non-solution path is neither clear nor a stable code (WS-005).
            if (solutionPath is not null && !IsExistingSolution(solutionPath))
                return ToolResult<ReloadResult>.Fail("SOLUTION_NOT_FOUND",
                    $"Not an existing .sln or .slnx file: {solutionPath}",
                    "Pass the full path of an existing .sln or .slnx file. The solution being served is unchanged.");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var loaded = await Workspace.ReloadAsync(solutionPath, ct2);
            var result = new ReloadResult(
                Loaded: true,
                SolutionPath: loaded.SolutionPath,
                ProjectCount: loaded.ProjectCount,
                DurationMs: sw.ElapsedMilliseconds,
                Diagnostics: loaded.Diagnostics);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return ToolResult<ReloadResult>.OkSummary(
                    $"reloaded {result.ProjectCount} projects from {Path.GetFileName(result.SolutionPath)}");
            return ToolResult<ReloadResult>.Ok(result);
        }, ct, reloadsWorkspace: true);

    private static bool IsExistingSolution(string path)
        => Path.IsPathFullyQualified(path)
           && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
           && File.Exists(path);
}
