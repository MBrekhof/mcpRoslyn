using System.ComponentModel;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record PackageRef(string Name, string Version);
public sealed record ProjectSummary(
    string Name,
    string? TargetFramework,
    int DocumentCount,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageRef> PackageReferences);
public sealed record ProjectOverviewResult(
    string SolutionPath,
    IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<Contracts.WorkspaceLoadDiagnostic> Diagnostics);

[McpServerToolType]
internal sealed class ProjectOverviewTool(IWorkspaceService ws, ILogger<ProjectOverviewTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "project_overview")]
    [Description("Returns the loaded solution's projects with target frameworks, package references, and project references.")]
    public Task<Contracts.ToolResult<ProjectOverviewResult>> InvokeAsync(
        int maxProjects = 25,
        int maxPackagesPerProject = 8,
        int maxProjectReferencesPerProject = 8,
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var projects = solution.Projects.Take(maxProjects).Select(p => new ProjectSummary(
                Name: p.Name,
                // TODO(v1.4): extract TargetFramework from .csproj
                TargetFramework: null,
                DocumentCount: p.Documents.Count(),
                ProjectReferences: p.ProjectReferences
                    .Select(r => solution.GetProject(r.ProjectId)?.Name)
                    .Where(n => n is not null)
                    .Take(maxProjectReferencesPerProject)
                    .ToArray()!,
                PackageReferences: ReadPackageRefsFromCsproj(p.FilePath, maxPackagesPerProject)
            )).ToArray();

            return Contracts.ToolResult<ProjectOverviewResult>.Ok(new ProjectOverviewResult(
                SolutionPath: solution.FilePath ?? "",
                Projects: projects,
                Diagnostics: Workspace.Diagnostics));
        }, ct);

    /// <summary>
    /// Reads PackageReference items directly from the .csproj XML.
    /// More reliable than inspecting MetadataReferences, which may not include
    /// packages whose TFM doesn't match the target framework (e.g., netstandard
    /// packages in a net10.0 project).
    /// </summary>
    private static IReadOnlyList<PackageRef> ReadPackageRefsFromCsproj(string? csprojPath, int max)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
            return Array.Empty<PackageRef>();

        try
        {
            var doc = XDocument.Load(csprojPath);
            return doc.Descendants("PackageReference")
                .Select(e => new PackageRef(
                    Name: e.Attribute("Include")?.Value ?? "",
                    Version: e.Attribute("Version")?.Value ?? e.Element("Version")?.Value ?? ""))
                .Where(r => !string.IsNullOrEmpty(r.Name))
                .Take(max)
                .ToArray();
        }
        catch
        {
            return Array.Empty<PackageRef>();
        }
    }
}
