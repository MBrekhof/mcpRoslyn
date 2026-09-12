using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
    [Description("Returns Roslyn diagnostics for one file: compiler diagnostics plus, by default, the project's own analyzers (NetAnalyzers, StyleCop, …) at their configured .editorconfig severities — set includeAnalyzers=false for compiler-only. Compilation-end analyzer rules (whole-project checks) are not run per file. Defaults: minimumSeverity=\"Warning\" (Info/Hidden hidden), includeGenerated=true; pass minimumSeverity=\"All\" to see everything. excludeDiagnosticCodes and excludeDiagnosticSources accept string arrays.")]
    public Task<Contracts.ToolResult<GetDocumentDiagnosticsResult>> InvokeAsync(
        string filePath, string? severity,
        bool includeGenerated = true,
        string? minimumSeverity = "Warning",
        string[]? excludeDiagnosticCodes = null,
        string[]? excludeDiagnosticSources = null,
        bool includeAnalyzers = true,
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

            IEnumerable<Diagnostic> diagnostics = semantic.GetDiagnostics(cancellationToken: ct2);
            var analyzerExceptions = new ConcurrentQueue<Diagnostic>();
            var withAnalyzers = includeAnalyzers
                ? RoslynHelpers.WithProjectAnalyzers(doc.Project, semantic.Compilation,
                    (_, _, diagnostic) => analyzerExceptions.Enqueue(diagnostic))
                : null;
            if (withAnalyzers is not null)
            {
                var tree = semantic.SyntaxTree;
                var suppressors = withAnalyzers.Analyzers
                    .Where(a => a is DiagnosticSuppressor)
                    .ToImmutableArray();
                if (suppressors.Any(a =>
                    {
                        try
                        {
                            return ((DiagnosticSuppressor)a).SupportedSuppressions
                                .Any(s => diagnostics.Any(d => d.Id == s.SuppressedDiagnosticId));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            return false; // Roslyn reports the getter failure when analyzers run.
                        }
                    }))
                {
                    // Compiler suppressors require a whole-compilation pass; run only when this
                    // file has a diagnostic they can suppress, and keep ordinary analyzers scoped.
                    var withSuppressors = semantic.Compilation.WithAnalyzers(
                        suppressors, withAnalyzers.AnalysisOptions);
                    diagnostics = (await withSuppressors.GetAllDiagnosticsAsync(ct2))
                        .Where(d => d.Location.SourceTree == tree ||
                            d.AdditionalLocations.Any(location => location.SourceTree == tree));
                }

                // Scoped to this one tree, so the cost is one file's analysis, not the project's.
                // The semantic model must come from the analyzer compilation, not the original.
                diagnostics = diagnostics
                    .Concat(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, ct2))
                    .Concat(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(
                        withAnalyzers.Compilation.GetSemanticModel(tree), filterSpan: null, ct2));
                // Per-tree APIs omit non-local diagnostics, including analyzer failures (AD0001).
                // Collect failures via the callback without running whole-compilation analysis.
                diagnostics = diagnostics.Concat(CompilationWithAnalyzers.GetEffectiveDiagnostics(
                    analyzerExceptions.Distinct(), withAnalyzers.Compilation).Where(d => !d.IsSuppressed));
            }

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
