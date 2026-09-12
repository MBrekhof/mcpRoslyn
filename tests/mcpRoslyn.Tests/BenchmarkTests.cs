using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

/// <summary>
/// Numbers behind tool defaults, measured against a real solution. Run one explicitly:
/// <c>dotnet test tests/mcpRoslyn.Tests --filter "FullyQualifiedName~BenchmarkTests" --logger "console;verbosity=detailed"</c>.
/// Timings on this machine swing ~2x between runs, so every timing here is paired with an
/// interleaved in-process control and reported as a ratio as well as milliseconds.
/// </summary>
[TestFixture]
[Explicit("Manual benchmark — runs against the real BPG solution, not portable.")]
[Category("Manual")]
public class BenchmarkTests
{
    private const string BpgSolutionPath = @"C:\Projects\BPG\BPG.sln";

    [OneTimeSetUp]
    public void Setup()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    private static async Task<WorkspaceService> LoadBpgAsync()
    {
        if (!File.Exists(BpgSolutionPath))
            Assert.Ignore($"BPG.sln not found at {BpgSolutionPath}");

        var workspace = new WorkspaceService(
            new McpRoslynOptions { SolutionPath = BpgSolutionPath }, NullLogger<WorkspaceService>.Instance);
        await workspace.LoadAsync();
        await workspace.WarmupTask;
        return workspace;
    }

    private static async Task<(long Ms, T Result)> Time<T>(Func<Task<T>> call)
    {
        var sw = Stopwatch.StartNew();
        var result = await call();
        return (sw.ElapsedMilliseconds, result);
    }

    private static long Median(List<long> xs)
    {
        xs.Sort();
        return xs[xs.Count / 2];
    }

    /// <summary>
    /// DIAG-001: what running the project's analyzers costs, per file and solution-wide. The
    /// compiler-only call is the control: same process, same moment, untouched by analyzers.
    /// </summary>
    [Test]
    public async Task Diagnostics_analyzer_cost()
    {
        await using var workspace = await LoadBpgAsync();
        var docTool = new GetDocumentDiagnosticsTool(workspace, NullLogger<GetDocumentDiagnosticsTool>.Instance);
        var slnTool = new GetCompilationErrorsTool(workspace, NullLogger<GetCompilationErrorsTool>.Instance);
        var solution = await workspace.GetFreshSolutionAsync();

        // The ten largest hand-written files: the expensive end of what an agent asks about.
        var docs = solution.Projects.SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null
                        && !d.FilePath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                        && !d.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => new FileInfo(d.FilePath!).Length)
            .Take(10)
            .ToList();

        var docCompiler = new List<long>();
        var docAnalyzers = new List<long>();
        long firstAnalyzerCall = -1;
        int analyzerOnlyDiagnostics = 0;
        foreach (var doc in docs)
        {
            for (var round = 0; round < 3; round++)
            {
                var (cMs, c) = await Time(() => docTool.InvokeAsync(
                    doc.FilePath!, severity: null, minimumSeverity: "All", includeAnalyzers: false));
                var (aMs, a) = await Time(() => docTool.InvokeAsync(
                    doc.FilePath!, severity: null, minimumSeverity: "All", includeAnalyzers: true));
                Assert.That(c.Error, Is.Null);
                Assert.That(a.Error, Is.Null);

                docCompiler.Add(cMs);
                if (firstAnalyzerCall < 0) firstAnalyzerCall = aMs; // analyzer assembly load lands here
                else docAnalyzers.Add(aMs);
                if (round == 0) analyzerOnlyDiagnostics += a.Result!.Diagnostics.Count - c.Result!.Diagnostics.Count;
            }
        }

        var slnCompiler = new List<long>();
        var slnAnalyzers = new List<long>();
        int slnCompilerCount = 0, slnAnalyzerCount = 0;
        for (var round = 0; round < 3; round++)
        {
            var (cMs, c) = await Time(() => slnTool.InvokeAsync(
                severity: null, projectName: null, minimumSeverity: "All", includeAnalyzers: false));
            var (aMs, a) = await Time(() => slnTool.InvokeAsync(
                severity: null, projectName: null, minimumSeverity: "All", includeAnalyzers: true));
            slnCompiler.Add(cMs);
            slnAnalyzers.Add(aMs);
            slnCompilerCount = c.Result!.Diagnostics.Count;
            slnAnalyzerCount = a.Result!.Diagnostics.Count;
        }

        var out_ = TestContext.Out;
        out_.WriteLine($"BPG: {solution.Projects.Count()} projects; per-file sample = {docs.Count} largest files x 3 rounds");
        out_.WriteLine($"get_document_diagnostics  first analyzer call (cold): {firstAnalyzerCall} ms");
        out_.WriteLine($"get_document_diagnostics  median compiler-only: {Median(docCompiler)} ms | median with analyzers: {Median(docAnalyzers)} ms | max with analyzers: {docAnalyzers.Max()} ms");
        out_.WriteLine($"get_document_diagnostics  analyzer-only diagnostics across sample: {analyzerOnlyDiagnostics}");
        out_.WriteLine($"get_compilation_errors    compiler-only ms: [{string.Join(", ", slnCompiler)}] -> {slnCompilerCount} diagnostics");
        out_.WriteLine($"get_compilation_errors    with analyzers ms: [{string.Join(", ", slnAnalyzers)}] -> {slnAnalyzerCount} diagnostics");
        out_.WriteLine($"get_compilation_errors    median ratio analyzers/compiler: {(double)Median(slnAnalyzers) / Math.Max(1, Median(slnCompiler)):F1}x");
    }
}
