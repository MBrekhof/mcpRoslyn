using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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

        // DIAG-003: EF Core's suppressor must hide CS8618 on BPGDbContext's DbSets in the default pass.
        var (dataMs, data) = await Time(() => slnTool.InvokeAsync(severity: "Error", projectName: "BPG.Data"));
        out_.WriteLine($"get_compilation_errors    BPG.Data errors, default pass: {data.Result!.Diagnostics.Count} in {dataMs} ms");
    }

    private const string BpgLlmServiceFile = @"C:\Projects\BPG\src\BPG.LLM\Services\LLMService.cs";

    private static async Task<(long Ms, string Text, int StructuredChars, bool Failed)> CallTool(
        McpClient client, string tool, Dictionary<string, object?> args)
    {
        var sw = Stopwatch.StartNew();
        var r = await client.CallToolAsync(tool, args!);
        var ms = sw.ElapsedMilliseconds;
        var text = string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
        // A tool failure is an ordinary ToolResult payload ({"error":{...}}), not MCP isError,
        // so check both — otherwise a stale anchor is measured as if it were a real answer.
        // Unescaped "error":{ can only be structure: inside a JSON string the quotes are escaped.
        var failed = r.IsError == true || text.Contains("\"error\":{", StringComparison.Ordinal);
        return (ms, text, r.StructuredContent?.ToString()?.Length ?? 0, failed);
    }

    /// <summary>
    /// PERF-002: what each tool's default response costs the agent's context. Goes through the real
    /// stdio transport against the built server, so the measured text is exactly what an MCP client
    /// receives, SDK serialization included. Tokens ~= chars / 4 (roslynk's benchmark convention).
    /// Anchors are real BPG symbols; a stale one shows up as an error in the table, not a crash.
    /// </summary>
    [Test]
    public async Task Tool_response_sizes()
    {
        if (!File.Exists(BpgSolutionPath))
            Assert.Ignore($"BPG.sln not found at {BpgSolutionPath}");
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "mcpRoslyn", "bin", "Debug", "net10.0", "win-x64", "mcpRoslyn.exe"));
        if (!File.Exists(exe))
            Assert.Ignore($"Server not built at {exe}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mcpRoslyn",
            Command = exe,
            Arguments = ["--solution", BpgSolutionPath],
        }));

        // No readiness probe: indexed tools wait for the index themselves (IDX-002), and every
        // scenario below gets an unrecorded warm-up call first.

        (string Tool, Dictionary<string, object?> Args)[] scenarios =
        [
            ("project_overview", new()),
            ("workspace_symbol", new() { ["query"] = "Spec", ["kinds"] = null, ["maxResults"] = null }),
            ("list_document_symbols", new() { ["filePath"] = BpgLlmServiceFile }),
            ("hover", new() { ["filePath"] = BpgLlmServiceFile, ["line"] = 15, ["column"] = 31 }),
            ("goto_definition", new() { ["filePath"] = BpgLlmServiceFile, ["line"] = 15, ["column"] = 31 }),
            ("find_references", Sym("T:BPG.Core.Interfaces.IUnitOfWork")),
            ("find_implementations", Sym("T:BPG.Core.Interfaces.ILLMService")),
            ("find_derived_types", Sym("T:BPG.Core.Interfaces.IRepository`1")),
            ("find_callers", Sym("M:BPG.Core.Interfaces.ILLMService.GenerateResponseAsync(System.String,BPG.Core.Models.Conversation)")),
            ("find_callees", Sym("M:BPG.LLM.Services.LLMService.GenerateResponseAsync(System.String,BPG.Core.Models.Conversation)")),
            ("analyze_symbol", new() { ["symbolId"] = "T:BPG.Core.Interfaces.ILLMService" }),
            ("semantic_search", new() { ["pattern"] = "parameter-type:BPG.Core.Models.Conversation" }),
            ("test_map", new() { ["symbolId"] = "T:BPG.LLM.Services.LLMService" }),
            ("find_entrypoints", new()),
            ("find_registrations", new()),
            ("find_dead_code_candidates", new()),
            ("get_document_diagnostics", new() { ["filePath"] = BpgLlmServiceFile, ["severity"] = null }),
            ("get_compilation_errors", new() { ["severity"] = null, ["projectName"] = null }),
            ("rename_symbol", new() { ["filePath"] = BpgLlmServiceFile, ["line"] = 15, ["column"] = 31, ["newName"] = "ILlmServiceRenamed" }),
            ("reload_workspace", new()), // last: it rebuilds the workspace; timed once
        ];

        var dumpDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "perf002");
        Directory.CreateDirectory(dumpDir);
        var out_ = TestContext.Out;
        out_.WriteLine($"Responses dumped to {dumpDir}");
        out_.WriteLine("| tool | warm ms (median of 3) | chars | ~tokens | structuredContent chars | failed |");
        out_.WriteLine("|---|---:|---:|---:|---:|---|");

        foreach (var (tool, args) in scenarios)
        {
            var once = tool == "reload_workspace";
            if (!once) await CallTool(client, tool, args); // warm-up call, not recorded
            var runs = new List<(long Ms, string Text, int StructuredChars, bool Failed)>();
            for (var i = 0; i < (once ? 1 : 3); i++) runs.Add(await CallTool(client, tool, args));

            var last = runs[^1];
            await File.WriteAllTextAsync(Path.Combine(dumpDir, tool + ".txt"), last.Text);
            out_.WriteLine($"| {tool} | {Median(runs.Select(r => r.Ms).ToList())} | {last.Text.Length} | {last.Text.Length / 4} | {last.StructuredChars} | {runs.Any(r => r.Failed)} |");
        }

        static Dictionary<string, object?> Sym(string id) => new()
            { ["filePath"] = null, ["line"] = null, ["column"] = null, ["symbolId"] = id };
    }
}
