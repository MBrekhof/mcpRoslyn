using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

/// <param name="LoadFailures">Workspace load failures still standing after reclassification — projects that
/// did not load, plus failures naming no project. What failed to load contributes no diagnostics, so a
/// clean result with this above zero is not a clean build (DIAG-002).</param>
public sealed record GetCompilationErrorsResult(IReadOnlyList<Contracts.DiagnosticInfo> Diagnostics, int LoadFailures = 0);

[McpServerToolType]
internal sealed class GetCompilationErrorsTool(IWorkspaceService ws, ILogger<GetCompilationErrorsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "get_compilation_errors")]
    [Description("Compiler diagnostics for every project that loaded — close to 'would dotnet build succeed?' without invoking MSBuild, but whatever failed to load contributes nothing, so check LoadFailures before trusting a clean result (reload_workspace lists them). Defaults: minimumSeverity=\"Warning\" (Info/Hidden hidden), includeGenerated=true; pass minimumSeverity=\"All\" to see everything, or an exact severity (it overrides the minimum). excludeDiagnosticCodes accepts a string array. projectName matches a project's name — a multi-targeted project also by its name without the \"(tfm)\" suffix; an unknown name is PROJECT_NOT_FOUND. includeAnalyzers=true also runs the project's own analyzers (NetAnalyzers, StyleCop, …) at their configured .editorconfig severities — 'does it pass this project's bar', not just 'does it compile'; off by default because it re-runs every analyzer on the whole solution (seconds). For one file, get_document_diagnostics runs analyzers by default.")]
    public Task<Contracts.ToolResult<GetCompilationErrorsResult>> InvokeAsync(
        string? severity, string? projectName,
        bool includeGenerated = true,
        string? minimumSeverity = "Warning",
        string[]? excludeDiagnosticCodes = null,
        bool includeAnalyzers = false,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            // Solution and load diagnostics from one generation: a reload landing while this compiles must
            // not pair these diagnostics with another load's failure count.
            var loaded = await Workspace.GetFreshSolutionWithDiagnosticsAsync(ct2);
            var solution = loaded.Solution;
            var exactSeverity = ParseSeverity(severity);
            var results = new List<Contracts.DiagnosticInfo>();

            var projects = solution.Projects
                .Where(p => projectName is null || MatchesProjectName(p.Name, projectName))
                .ToList();
            if (projectName is not null && projects.Count == 0)
                return Contracts.ToolResult<GetCompilationErrorsResult>.Fail(
                    "PROJECT_NOT_FOUND", $"No loaded project is named '{projectName}'.",
                    "project_overview lists the loaded projects; one that failed to load appears in reload_workspace's diagnostics.");

            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync(ct2);
                if (compilation is null) continue;

                var withAnalyzers = includeAnalyzers ? RoslynHelpers.WithProjectAnalyzers(project, compilation) : null;
                var diagnostics = withAnalyzers is null
                    ? compilation.GetDiagnostics(ct2)
                    : await withAnalyzers.GetAllDiagnosticsAsync(ct2); // compiler + analyzer diagnostics

                foreach (var d in diagnostics)
                {
                    if (exactSeverity is not null && d.Severity != exactSeverity.Value) continue;
                    results.Add(new Contracts.DiagnosticInfo(
                        Severity: d.Severity.ToString(),
                        Code: d.Id,
                        Message: d.GetMessage(),
                        Location: RoslynHelpers.ToLocation(d.Location) ?? new Contracts.SymbolLocation(d.Location.SourceTree?.FilePath ?? "", 1, 1, 1, 1)));
                }
            }

            // Post-collection filters (applied in order; do not affect index construction). An exact
            // severity asked for explicitly wins over the default minimum, which would otherwise hide
            // every Info/Hidden diagnostic it selected (DIAG-002).
            var filtered = ApplyFilters(results, includeGenerated,
                exactSeverity is not null ? "All" : minimumSeverity, excludeDiagnosticCodes);

            var loadFailures = CountLoadFailures(loaded.LoadDiagnostics);

            var result = new GetCompilationErrorsResult(filtered, loadFailures);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var errCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                var warnCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
                var failureNote = loadFailures > 0 ? $", {loadFailures} load failures" : "";
                return Contracts.ToolResult<GetCompilationErrorsResult>.OkSummary($"{errCount} errors, {warnCount} warnings{failureNote}");
            }
            return Contracts.ToolResult<GetCompilationErrorsResult>.Ok(result);
        }, ct);

    /// <summary>
    /// Failures still standing after WorkspaceService's reclassification: one per project named, and one
    /// per distinct message for failures that name no project — those must not vanish either.
    /// </summary>
    internal static int CountLoadFailures(IEnumerable<Contracts.WorkspaceLoadDiagnostic> diagnostics)
        => diagnostics
            .Where(d => d.Kind == "Failure")
            .Select(d => d.ProjectName is not null ? "project:" + d.ProjectName.ToUpperInvariant() : "message:" + d.Message)
            .Distinct()
            .Count();

    private static readonly System.Text.RegularExpressions.Regex TfmSuffix =
        new(@"^\((net\d+(\.\d+)*(-[\w.]+)?|netstandard\d+(\.\d+)*|netcoreapp\d+(\.\d+)*)\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A multi-targeted project loads as "Foo(net8.0)", "Foo(net10.0)"; "Foo" selects them all. Only a
    /// target-framework suffix counts — "Foo" must not select an unrelated project named "Foo(Bar)".
    /// </summary>
    internal static bool MatchesProjectName(string loadedName, string wanted)
        => string.Equals(loadedName, wanted, StringComparison.OrdinalIgnoreCase)
           || (loadedName.Length > wanted.Length
               && loadedName.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
               && TfmSuffix.IsMatch(loadedName[wanted.Length..]));

    private static DiagnosticSeverity? ParseSeverity(string? s)
        => s is null ? null
            : Enum.TryParse<DiagnosticSeverity>(s, ignoreCase: true, out var sev) ? sev
            : null;

    internal static IReadOnlyList<Contracts.DiagnosticInfo> ApplyFilters(
        IEnumerable<Contracts.DiagnosticInfo> diagnostics,
        bool includeGenerated,
        string? minimumSeverity,
        string[]? excludeDiagnosticCodes)
    {
        var q = diagnostics.AsEnumerable();

        // 1. Exclude by diagnostic code (case-insensitive)
        if (excludeDiagnosticCodes is { Length: > 0 })
        {
            var codes = new HashSet<string>(excludeDiagnosticCodes, StringComparer.OrdinalIgnoreCase);
            q = q.Where(d => !codes.Contains(d.Code));
        }

        // 2. Minimum severity threshold (Error > Warning > Info > Hidden; "All" = no filter)
        if (minimumSeverity is not null &&
            !string.Equals(minimumSeverity, "All", StringComparison.OrdinalIgnoreCase))
        {
            var minRank = SeverityRank(minimumSeverity);
            q = q.Where(d => SeverityRank(d.Severity) >= minRank);
        }

        // 3. Exclude generated files when includeGenerated == false
        if (!includeGenerated)
        {
            q = q.Where(d =>
                d.Location is null ||
                (!d.Location.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                 !d.Location.FilePath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) &&
                 !d.Location.FilePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)));
        }

        return q.ToList();
    }

    private static int SeverityRank(string? severity) => severity?.ToUpperInvariant() switch
    {
        "ERROR"   => 3,
        "WARNING" => 2,
        "INFO"    => 1,
        "HIDDEN"  => 0,
        _         => 0,
    };
}
