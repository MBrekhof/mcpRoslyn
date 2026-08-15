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
    IReadOnlyList<PackageRef> PackageReferences,
    bool? IsPackable = null);
public sealed record ProjectOverviewResult(
    string SolutionPath,
    IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<Contracts.WorkspaceLoadDiagnostic> Diagnostics);

[McpServerToolType]
internal sealed class ProjectOverviewTool(IWorkspaceService ws, ILogger<ProjectOverviewTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "project_overview")]
    [Description("Returns the loaded solution's projects with target frameworks, IsPackable, package references, and project references.")]
    public Task<Contracts.ToolResult<ProjectOverviewResult>> InvokeAsync(
        int maxProjects = 25,
        int maxPackagesPerProject = 8,
        int maxProjectReferencesPerProject = 8,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var projects = solution.Projects.Take(maxProjects).Select(p =>
            {
                var facts = ReadCsproj(p.FilePath, maxPackagesPerProject, log);
                return new ProjectSummary(
                    Name: p.Name,
                    TargetFramework: TfmFromProjectName(p.Name) ?? facts.TargetFramework,
                    DocumentCount: p.Documents.Count(),
                    ProjectReferences: p.ProjectReferences
                        .Select(r => solution.GetProject(r.ProjectId)?.Name)
                        .Where(n => n is not null)
                        .Take(maxProjectReferencesPerProject)
                        .ToArray()!,
                    PackageReferences: facts.Packages,
                    IsPackable: facts.IsPackable);
            }).ToArray();

            var result = new ProjectOverviewResult(
                SolutionPath: solution.FilePath ?? "",
                Projects: projects,
                Diagnostics: Workspace.Diagnostics);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<ProjectOverviewResult>.OkSummary($"{result.Projects.Count} projects");
            return Contracts.ToolResult<ProjectOverviewResult>.Ok(result);
        }, ct);

    /// <summary>
    /// MSBuildWorkspace names a multi-targeted project "Foo(net8.0)" — one Project per TFM.
    /// That suffix is the authoritative per-project framework; the .csproj only carries the
    /// whole &lt;TargetFrameworks&gt; list, which can't say which of them this Project is.
    /// </summary>
    private static string? TfmFromProjectName(string name)
    {
        var open = name.LastIndexOf('(');
        return open > 0 && name.EndsWith(')') ? name[(open + 1)..^1] : null;
    }

    private sealed record CsprojFacts(string? TargetFramework, bool? IsPackable, IReadOnlyList<PackageRef> Packages);

    /// <summary>
    /// Reads TargetFramework, IsPackable and PackageReference items directly from the .csproj XML.
    /// More reliable than inspecting MetadataReferences, which may not include
    /// packages whose TFM doesn't match the target framework (e.g., netstandard
    /// packages in a net10.0 project).
    /// </summary>
    private static CsprojFacts ReadCsproj(string? csprojPath, int maxPackages, ILogger log)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
            return new CsprojFacts(null, null, Array.Empty<PackageRef>());

        try
        {
            var doc = XDocument.Load(csprojPath);

            var tfm = (doc.Descendants("TargetFramework").FirstOrDefault()
                    ?? doc.Descendants("TargetFrameworks").FirstOrDefault())?.Value.Trim();
            // ponytail: a value still holding an MSBuild variable (inherited from Directory.Build.props,
            // or $(NetVersion)) is reported as null rather than echoed back as if it were a framework name.
            // Resolving those means evaluating MSBuild — add only if real solutions need it.
            if (tfm is not null && (tfm.Length == 0 || tfm.Contains("$("))) tfm = null;

            bool? isPackable = bool.TryParse(doc.Descendants("IsPackable").FirstOrDefault()?.Value.Trim(), out var b)
                ? b
                : null;

            var packages = doc.Descendants("PackageReference")
                .Select(e => new PackageRef(
                    Name: e.Attribute("Include")?.Value ?? "",
                    Version: e.Attribute("Version")?.Value ?? e.Element("Version")?.Value ?? ""))
                .Where(r => !string.IsNullOrEmpty(r.Name))
                .Take(maxPackages)
                .ToArray();

            return new CsprojFacts(tfm, isPackable, packages);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse {Path}", csprojPath);
            return new CsprojFacts(null, null, Array.Empty<PackageRef>());
        }
    }
}
