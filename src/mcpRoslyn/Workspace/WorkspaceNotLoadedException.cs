namespace mcpRoslyn.Workspace;

/// <summary>No solution is loaded, e.g. the startup load failed (WS-008). Tools report it as WORKSPACE_NOT_LOADED.</summary>
public sealed class WorkspaceNotLoadedException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
