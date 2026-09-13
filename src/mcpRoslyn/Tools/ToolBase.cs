using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using mcpRoslyn.Contracts;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

/// <summary>
/// Carries "this tool call failed" from <see cref="ToolBase"/> out to the call-tool filter, which sets the
/// MCP result's <c>isError</c>: to the SDK a failed <see cref="ToolResult{T}"/> is an ordinary return value,
/// so a client keyed on <c>isError</c> read every failure as success (TOOL-011). The filter installs the box
/// before invoking the tool, because an AsyncLocal assigned inside the tool never flows back out.
/// </summary>
internal static class ToolCallOutcome
{
    private static readonly AsyncLocal<StrongBox<bool>?> Failed = new();

    public static async ValueTask<CallToolResult> TrackAsync(Func<ValueTask<CallToolResult>> invoke)
    {
        var failed = new StrongBox<bool>();
        Failed.Value = failed;
        var result = await invoke();
        if (failed.Value) result.IsError = true;
        return result;
    }

    public static void MarkFailed()
    {
        if (Failed.Value is { } failed) failed.Value = true;
    }
}

internal abstract class ToolBase(IWorkspaceService workspace, ILogger logger)
{
    protected IWorkspaceService Workspace => workspace;
    protected ILogger Log => logger;

    /// <param name="reloadsWorkspace">The body itself replaces the workspace, so a reload during it is expected.</param>
    protected async Task<ToolResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ToolResult<T>>> body,
        CancellationToken ct,
        bool reloadsWorkspace = false) where T : class
    {
        var loadsBefore = workspace.LoadCount;
        var result = await ExecuteCoreAsync(body, ct);
        if (result.Error is not null) ToolCallOutcome.MarkFailed();

        // Read after the body, so they reflect the refresh it made (WS-007). A reload landing mid-call publishes a
        // clean generation while the answer came from the one it replaced, which is a reason of its own.
        var stale = StaleReasons().ToList();
        if (!reloadsWorkspace && workspace.LoadCount != loadsBefore)
            stale.Add("the workspace was reloaded while this call ran, so the answer may predate the reload — repeat the call");
        return stale.Count == 0 ? result : result with { Warnings = [StaleWarning(stale)] };
    }

    private IReadOnlyList<string> StaleReasons()
    {
        try { return workspace.StaleReasons; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Checking the workspace for staleness failed"); // a caveat must not fail the answer
            return [];
        }
    }

    private static string StaleWarning(IReadOnlyList<string> reasons)
        => $"The workspace may be stale: {string.Join("; ", reasons.Take(5))}"
           + (reasons.Count > 5 ? $"; and {reasons.Count - 5} more" : "")
           + ". Answers may miss these changes; call reload_workspace to load them.";

    private async Task<ToolResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<ToolResult<T>>> body,
        CancellationToken ct) where T : class
    {
        try { return await body(ct); }
        catch (OperationCanceledException) { throw; }
        catch (RoslynHelpers.AmbiguousSymbolIdException ex)
        {
            return ToolResult<T>.Fail("AMBIGUOUS_SYMBOL_ID", ex.Message,
                "Pass filePath/line/column on the declaration you mean instead of symbolId; a file linked into several projects resolves in the first project that compiles it.");
        }
        catch (RoslynHelpers.PositionInvalidException ex)
        {
            return ToolResult<T>.Fail("POSITION_INVALID", ex.Message);
        }
        catch (IndexUnavailableException ex)
        {
            return ToolResult<T>.Fail("INDEX_UNAVAILABLE", ex.Message, "Call reload_workspace to rebuild the indexes.");
        }
        catch (FileNotFoundException ex)
        {
            return ToolResult<T>.Fail("FILE_NOT_IN_WORKSPACE", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult<T>.Fail("WORKSPACE_NOT_LOADED", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool failure");
            return ToolResult<T>.Fail("INTERNAL_ERROR", ex.Message);
        }
    }
}
