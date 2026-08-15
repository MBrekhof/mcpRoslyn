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
/// <param name="ProjectName">
/// The project the message is about, without extension (e.g. <c>duetGPT.LicenseServer</c>), pulled
/// out of the quoted path MSBuild embeds in the text. Null when no project path is quoted.
/// Note this does NOT imply the project failed to load — MSBuild reports package-pruning and
/// vulnerability advisories through the same channel, at kind <c>Failure</c>, for projects that
/// load perfectly well.
/// </param>
public sealed record WorkspaceLoadDiagnostic(string Kind, string Message, string? ProjectName = null);
