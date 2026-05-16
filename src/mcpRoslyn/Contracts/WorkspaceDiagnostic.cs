namespace mcpRoslyn.Contracts;

/// <summary>
/// A diagnostic raised by MSBuildWorkspace during solution load or reload —
/// typically a project that failed to evaluate (missing SDK, missing referenced
/// project, malformed csproj, etc.). Surfaced via <see cref="Workspace.IWorkspaceService.Diagnostics"/>
/// and on <c>reload_workspace</c> output so callers can see why a project may have been silently skipped.
///
/// Distinct from <see cref="Microsoft.CodeAnalysis.WorkspaceDiagnostic"/> (which is Roslyn's internal type) —
/// this is the serializable DTO the MCP server returns to callers.
/// </summary>
public sealed record WorkspaceLoadDiagnostic(string Kind, string Message);
