using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record GetCompilationErrorsResult(IReadOnlyList<Contracts.DiagnosticInfo> Diagnostics);

[McpServerToolType]
internal sealed class GetCompilationErrorsTool(IWorkspaceService ws, ILogger<GetCompilationErrorsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "get_compilation_errors")]
    [Description("Solution-wide diagnostic list — equivalent to 'would dotnet build succeed?' without invoking MSBuild. Defaults: minimumSeverity=\"Warning\" (Info/Hidden hidden), includeGenerated=true; pass minimumSeverity=\"All\" to see everything. excludeDiagnosticCodes and excludeDiagnosticSources accept string arrays.")]
    public Task<Contracts.ToolResult<GetCompilationErrorsResult>> InvokeAsync(
        string? severity, string? projectName,
        bool includeGenerated = true,
        string? minimumSeverity = "Warning",
        string[]? excludeDiagnosticCodes = null,
        string[]? excludeDiagnosticSources = null,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var exactSeverity = ParseSeverity(severity);
            var results = new List<Contracts.DiagnosticInfo>();

            foreach (var project in solution.Projects)
            {
                if (projectName is not null &&
                    !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var compilation = await project.GetCompilationAsync(ct2);
                if (compilation is null) continue;

                foreach (var d in compilation.GetDiagnostics(ct2))
                {
                    if (exactSeverity is not null && d.Severity != exactSeverity.Value) continue;
                    results.Add(new Contracts.DiagnosticInfo(
                        Severity: d.Severity.ToString(),
                        Code: d.Id,
                        Message: d.GetMessage(),
                        Location: RoslynHelpers.ToLocation(d.Location) ?? new Contracts.SymbolLocation(d.Location.SourceTree?.FilePath ?? "", 1, 1, 1, 1)));
                }
            }

            // Post-collection filters (applied in order; do not affect index construction)
            var filtered = ApplyFilters(results, includeGenerated, minimumSeverity, excludeDiagnosticCodes, excludeDiagnosticSources);

            var result = new GetCompilationErrorsResult(filtered);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var errCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                var warnCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
                return Contracts.ToolResult<GetCompilationErrorsResult>.OkSummary($"{errCount} errors, {warnCount} warnings");
            }
            return Contracts.ToolResult<GetCompilationErrorsResult>.Ok(result);
        }, ct);

    private static DiagnosticSeverity? ParseSeverity(string? s)
        => s is null ? null
            : Enum.TryParse<DiagnosticSeverity>(s, ignoreCase: true, out var sev) ? sev
            : null;

    internal static IReadOnlyList<Contracts.DiagnosticInfo> ApplyFilters(
        IEnumerable<Contracts.DiagnosticInfo> diagnostics,
        bool includeGenerated,
        string? minimumSeverity,
        string[]? excludeDiagnosticCodes,
        string[]? excludeDiagnosticSources)
    {
        var q = diagnostics.AsEnumerable();

        // 1. Exclude by diagnostic code (case-insensitive)
        if (excludeDiagnosticCodes is { Length: > 0 })
        {
            var codes = new HashSet<string>(excludeDiagnosticCodes, StringComparer.OrdinalIgnoreCase);
            q = q.Where(d => !codes.Contains(d.Code));
        }

        // 2. Exclude by source — DiagnosticInfo does not expose a Source field yet;
        //    skipping this filter until the contract is extended.
        _ = excludeDiagnosticSources; // intentionally unused

        // 3. Minimum severity threshold (Error > Warning > Info > Hidden; "All" = no filter)
        if (minimumSeverity is not null &&
            !string.Equals(minimumSeverity, "All", StringComparison.OrdinalIgnoreCase))
        {
            var minRank = SeverityRank(minimumSeverity);
            q = q.Where(d => SeverityRank(d.Severity) >= minRank);
        }

        // 4. Exclude generated files when includeGenerated == false
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
