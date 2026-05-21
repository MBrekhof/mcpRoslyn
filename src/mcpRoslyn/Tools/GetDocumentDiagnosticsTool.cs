using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record GetDocumentDiagnosticsResult(IReadOnlyList<Contracts.DiagnosticInfo> Diagnostics);

[McpServerToolType]
internal sealed class GetDocumentDiagnosticsTool(IWorkspaceService ws, ILogger<GetDocumentDiagnosticsTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "get_document_diagnostics")]
    [Description("Returns Roslyn diagnostics (errors/warnings/info) for one file. Optional severity filter: Error | Warning | Info | Hidden.")]
    public Task<Contracts.ToolResult<GetDocumentDiagnosticsResult>> InvokeAsync(
        string filePath, string? severity,
        bool includeGenerated = true,
        string? minimumSeverity = "Warning",
        string[]? excludeDiagnosticCodes = null,
        string[]? excludeDiagnosticSources = null,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var doc = RoslynHelpers.FindDocument(solution, filePath);
            if (doc is null)
                return Contracts.ToolResult<GetDocumentDiagnosticsResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");

            var semantic = await doc.GetSemanticModelAsync(ct2);
            if (semantic is null)
                return Contracts.ToolResult<GetDocumentDiagnosticsResult>.Fail(
                    "INTERNAL_ERROR", "Could not obtain semantic model.");

            var exactSeverity = ParseSeverity(severity);

            var diagnostics = semantic.GetDiagnostics(cancellationToken: ct2);
            var mapped = diagnostics
                .Where(d => exactSeverity is null || d.Severity == exactSeverity.Value)
                .Select(d => new Contracts.DiagnosticInfo(
                    Severity: d.Severity.ToString(),
                    Code: d.Id,
                    Message: d.GetMessage(),
                    Location: RoslynHelpers.ToLocation(d.Location) ?? new Contracts.SymbolLocation(filePath, 1, 1, 1, 1)))
                .ToList();

            // Post-collection filters (applied in order; do not affect index construction)
            var filtered = GetCompilationErrorsTool.ApplyFilters(
                mapped, includeGenerated, minimumSeverity, excludeDiagnosticCodes, excludeDiagnosticSources);

            var result = new GetDocumentDiagnosticsResult(filtered);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var errCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                var warnCount = result.Diagnostics.Count(d => string.Equals(d.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
                return Contracts.ToolResult<GetDocumentDiagnosticsResult>.OkSummary($"{errCount} errors, {warnCount} warnings");
            }
            return Contracts.ToolResult<GetDocumentDiagnosticsResult>.Ok(result);
        }, ct);

    private static DiagnosticSeverity? ParseSeverity(string? s)
        => s is null ? null
            : Enum.TryParse<DiagnosticSeverity>(s, ignoreCase: true, out var sev) ? sev
            : null;
}
