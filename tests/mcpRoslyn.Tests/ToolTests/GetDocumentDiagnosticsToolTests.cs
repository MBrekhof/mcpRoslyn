using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GetDocumentDiagnosticsToolTests
{
    [Test]
    public async Task GetDocumentDiagnostics_BrokenClass_returns_at_least_one_error()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();
        var brokenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestApp", "BrokenClass.cs");

        var result = await host.Tool.InvokeAsync(brokenPath, severity: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().NotBeEmpty();
        result.Result.Diagnostics.Should().Contain(d => d.Severity == "Error" && d.Code.StartsWith("CS"));
    }

    [Test]
    public async Task ExcludeDiagnosticCodes_filters_specified_codes()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();
        var brokenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestApp", "BrokenClass.cs");

        // BrokenClass.cs produces CS1525 — assert it appears WITHOUT the filter
        var baseline = await host.Tool.InvokeAsync(
            brokenPath, severity: null,
            minimumSeverity: "All",
            ct: CancellationToken.None);
        baseline.Result!.Diagnostics.Should().Contain(d => d.Code == "CS1525");

        var filtered = await host.Tool.InvokeAsync(
            brokenPath, severity: null,
            minimumSeverity: "All",
            excludeDiagnosticCodes: new[] { "CS1525" },
            ct: CancellationToken.None);
        filtered.Result!.Diagnostics.Should().NotContain(d => d.Code == "CS1525");
    }

    private static string AnalyzerTargetPath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestApp", "AnalyzerTarget.cs");

    [Test]
    public async Task Analyzers_run_by_default_at_configured_severity_and_honour_pragma()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();

        var result = await host.Tool.InvokeAsync(AnalyzerTargetPath, severity: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        // Exactly one: Twice (line 7) is reported, pragma-suppressed Thrice is not.
        var ca1822 = result.Result!.Diagnostics.Where(d => d.Code == "CA1822").ToList();
        ca1822.Should().ContainSingle();
        ca1822[0].Severity.Should().Be("Warning"); // rule default is off; .editorconfig raised it
        ca1822[0].Location.Line.Should().Be(7);
    }

    [Test]
    public async Task IncludeAnalyzers_false_returns_compiler_diagnostics_only()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();

        var result = await host.Tool.InvokeAsync(
            AnalyzerTargetPath, severity: null, minimumSeverity: "All", includeAnalyzers: false,
            ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Diagnostics.Should().NotContain(d => d.Code == "CA1822");
    }

    [Test]
    public async Task Compiler_warning_suppressors_run_only_when_analyzers_are_included()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("SuppressorTest", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddAnalyzerReference(new AnalyzerImageReference([new UnusedLocalSuppressor()]));
        var document = project.AddDocument("Target.cs", SourceText.From("class Target { void M() { int unused = 0; } void N() { Missing(); } }"),
            filePath: Path.GetFullPath("Target.cs"));
        var tool = new GetDocumentDiagnosticsTool(new DiagnosticTestWorkspace(document.Project.Solution),
            NullLogger<GetDocumentDiagnosticsTool>.Instance);

        var compilerOnly = await tool.InvokeAsync(document.FilePath!, severity: null,
            minimumSeverity: "All", includeAnalyzers: false);
        var withAnalyzers = await tool.InvokeAsync(document.FilePath!, severity: null,
            minimumSeverity: "All");

        compilerOnly.Error.Should().BeNull();
        compilerOnly.Result!.Diagnostics.Should().Contain(d => d.Code == "CS0219");
        compilerOnly.Result.Diagnostics.Should().Contain(d => d.Code == "CS0103");
        withAnalyzers.Error.Should().BeNull();
        withAnalyzers.Result!.Diagnostics.Should().NotContain(d => d.Code == "CS0219");
        withAnalyzers.Result.Diagnostics.Should().Contain(d => d.Code == "CS0103");
    }

    [TestCase("initialize")]
    [TestCase("syntax")]
    [TestCase("semantic")]
    [TestCase("syntax", ReportDiagnostic.Suppress)]
    [TestCase("syntax", ReportDiagnostic.Error)]
    public async Task Analyzer_exceptions_are_reported_without_losing_other_diagnostics(
        string phase, ReportDiagnostic report = ReportDiagnostic.Default)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("AnalyzerTest", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSpecificDiagnosticOptions([new KeyValuePair<string, ReportDiagnostic>("AD0001", report)]))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddAnalyzerReference(new AnalyzerImageReference([new ThrowingAnalyzer(phase)]));
        var document = project.AddDocument("Target.cs", SourceText.From("class Target { int x = ; }"),
            filePath: Path.GetFullPath("Target.cs"));
        var tool = new GetDocumentDiagnosticsTool(new DiagnosticTestWorkspace(document.Project.Solution),
            NullLogger<GetDocumentDiagnosticsTool>.Instance);

        var result = await tool.InvokeAsync(document.FilePath!, severity: null);

        result.Error.Should().BeNull();
        result.Result!.Diagnostics.Should().Contain(d => d.Code == "CS1525");
        var failures = result.Result.Diagnostics.Where(d => d.Code == "AD0001").ToList();
        if (report == ReportDiagnostic.Suppress)
            failures.Should().BeEmpty();
        else
        {
            failures.Should().ContainSingle().Which.Message.Should().Contain("DIAG-001 test failure");
            failures[0].Severity.Should().Be(report == ReportDiagnostic.Error ? "Error" : "Warning");
        }
    }

    // Instantiated directly by tests; this is not a discoverable analyzer assembly.
#pragma warning disable RS1001
    internal sealed class UnusedLocalSuppressor : DiagnosticSuppressor
    {
        private static readonly SuppressionDescriptor Descriptor =
            new("TESTSPR001", "CS0219", "Unused locals are allowed in this test.");

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [Descriptor];

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                if (diagnostic.Id == Descriptor.SuppressedDiagnosticId)
                    context.ReportSuppression(Suppression.Create(Descriptor, diagnostic));
            }
        }
    }

    private sealed class ThrowingAnalyzer(string phase) : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            [new("TEST001", "Test", "Test", "Test", DiagnosticSeverity.Warning, true)];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            if (phase == "initialize") throw new InvalidOperationException("DIAG-001 test failure");
            if (phase == "syntax")
                context.RegisterSyntaxTreeAction(_ => throw new InvalidOperationException("DIAG-001 test failure"));
            else
                context.RegisterSemanticModelAction(_ => throw new InvalidOperationException("DIAG-001 test failure"));
        }
    }
#pragma warning restore RS1001
}

internal sealed class DiagnosticTestWorkspace(
    Solution solution,
    IReadOnlyList<mcpRoslyn.Contracts.WorkspaceLoadDiagnostic>? diagnostics = null,
    IReadOnlyList<mcpRoslyn.Contracts.WorkspaceLoadDiagnostic>? pairedDiagnostics = null) : IWorkspaceService
{
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkspaceLoad> ReloadAsync(string? solutionPath = null, CancellationToken ct = default)
        => Task.FromResult(new WorkspaceLoad(SolutionPath, solution.Projects.Count(), []));
    public string SolutionPath => solution.FilePath ?? "";
    public Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default) => Task.FromResult(solution);
    public int LoadedProjectCount => solution.ProjectIds.Count;
    public Task WarmupTask => Task.CompletedTask;
    public IReadOnlyList<mcpRoslyn.Contracts.WorkspaceLoadDiagnostic> Diagnostics => diagnostics ?? [];
    public IReadOnlyList<string> StaleReasons => [];
    public int LoadCount => 1;
    public SymbolIndex SymbolIndex => throw new NotSupportedException();
    public InvocationIndex InvocationIndex => throw new NotSupportedException();
    public Task<IndexedSolution> GetIndexedSolutionAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LoadedSolution> GetFreshSolutionWithDiagnosticsAsync(CancellationToken ct = default)
        => Task.FromResult(new LoadedSolution(solution, pairedDiagnostics ?? []));
}
