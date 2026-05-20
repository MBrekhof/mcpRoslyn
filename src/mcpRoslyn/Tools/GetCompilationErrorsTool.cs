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
    [Description("Solution-wide diagnostic list — equivalent to 'would dotnet build succeed?' without invoking MSBuild. Optional severity filter and projectName filter.")]
    public Task<Contracts.ToolResult<GetCompilationErrorsResult>> InvokeAsync(
        string? severity, string? projectName,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var minSeverity = ParseSeverity(severity);
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
                    if (minSeverity is not null && d.Severity != minSeverity.Value) continue;
                    results.Add(new Contracts.DiagnosticInfo(
                        Severity: d.Severity.ToString(),
                        Code: d.Id,
                        Message: d.GetMessage(),
                        Location: RoslynHelpers.ToLocation(d.Location) ?? new Contracts.SymbolLocation(d.Location.SourceTree?.FilePath ?? "", 1, 1, 1, 1)));
                }
            }

            var result = new GetCompilationErrorsResult(results);
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
}
