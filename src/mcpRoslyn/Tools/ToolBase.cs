using Microsoft.Extensions.Logging;
using mcpRoslyn.Contracts;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

internal abstract class ToolBase(IWorkspaceService workspace, ILogger logger)
{
    protected IWorkspaceService Workspace => workspace;
    protected ILogger Log => logger;

    protected async Task<ToolResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ToolResult<T>>> body,
        CancellationToken ct) where T : class
    {
        try { return await body(ct); }
        catch (OperationCanceledException) { throw; }
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
