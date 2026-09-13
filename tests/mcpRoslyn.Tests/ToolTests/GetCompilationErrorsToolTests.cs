using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GetCompilationErrorsToolTests
{
    [Test]
    public async Task GetCompilationErrors_returns_at_least_one_error()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await host.Tool.InvokeAsync(severity: null, projectName: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().Contain(d => d.Severity == "Error");
    }

    [Test]
    public async Task GetCompilationErrors_filtered_to_TestLib_returns_no_errors()
    {
        await using var host2 = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await host2.Tool.InvokeAsync(severity: "Error", projectName: "TestLib", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task ExcludeDiagnosticCodes_filters_specified_codes()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        // BrokenClass.cs produces CS1525 — assert it appears WITHOUT the filter
        var baseline = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "All",
            ct: CancellationToken.None);
        baseline.Result!.Diagnostics.Should().Contain(d => d.Code == "CS1525");

        var filtered = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "All",
            excludeDiagnosticCodes: new[] { "CS1525" },
            ct: CancellationToken.None);
        filtered.Result!.Diagnostics.Should().NotContain(d => d.Code == "CS1525");
    }

    [Test]
    public async Task MinimumSeverity_Error_drops_warnings()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "Error",
            ct: CancellationToken.None);
        r.Result!.Diagnostics.Should().OnlyContain(d => d.Severity == "Error");
    }

    [Test]
    public async Task IncludeGenerated_false_excludes_g_cs_paths()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            includeGenerated: false,
            minimumSeverity: "All",
            ct: CancellationToken.None);
        r.Result!.Diagnostics.Should().NotContain(
            d => d.Location != null && d.Location.FilePath.EndsWith(".g.cs"));
    }

    [Test]
    public async Task Unknown_projectName_is_PROJECT_NOT_FOUND_not_an_empty_success()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(severity: null, projectName: "NoSuchProject", ct: CancellationToken.None);

        r.Error.Should().NotBeNull();
        r.Error!.Code.Should().Be("PROJECT_NOT_FOUND");
    }

    [Test]
    public async Task Exact_severity_overrides_the_default_minimum()
    {
        // DIAG-002: an exact Info/Hidden severity still went through minimumSeverity's default of
        // Warning, which removed every diagnostic it had just selected.
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var everything = await host.Tool.InvokeAsync(severity: null, projectName: null, minimumSeverity: "All", ct: CancellationToken.None);
        var belowWarning = everything.Result!.Diagnostics.Select(d => d.Severity).FirstOrDefault(s => s is "Hidden" or "Info");
        belowWarning.Should().NotBeNull("the fixture needs an Info or Hidden diagnostic for this test to mean anything");

        var exact = await host.Tool.InvokeAsync(severity: belowWarning, projectName: null, ct: CancellationToken.None);
        exact.Result!.Diagnostics.Should().NotBeEmpty().And.OnlyContain(d => d.Severity == belowWarning);
    }

    [Test]
    public async Task A_project_that_failed_to_load_is_counted_not_silently_ignored()
    {
        // DIAG-002: a solution whose project failed to load compiled nothing and reported
        // "0 errors, 0 warnings" — indistinguishable from a clean build.
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-notloaded-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var slnPath = Path.Combine(tempDir, "Broken.sln");
            File.WriteAllText(slnPath,
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing\\Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n");
            await using var workspace = new WorkspaceService(
                new McpRoslynOptions { SolutionPath = slnPath }, NullLogger<WorkspaceService>.Instance);
            await workspace.LoadAsync();
            var tool = new GetCompilationErrorsTool(workspace, NullLogger<GetCompilationErrorsTool>.Instance);

            var r = await tool.InvokeAsync(severity: null, projectName: null, ct: CancellationToken.None);
            r.Error.Should().BeNull();
            r.Result!.Diagnostics.Should().BeEmpty("nothing loaded, so nothing was compiled");
            r.Result.LoadFailures.Should().Be(1, "an empty diagnostics list must not read as a clean build");

            var summary = await tool.InvokeAsync(severity: null, projectName: null, format: "summary", ct: CancellationToken.None);
            summary.Summary.Should().Contain("1 load failures");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestCase("Foo", "Foo", true)]
    [TestCase("foo", "Foo", true)]
    [TestCase("Foo(net8.0)", "Foo", true)]
    [TestCase("Foo(net8.0-windows)", "Foo", true)]
    [TestCase("Foo(netstandard2.0)", "Foo", true)]
    [TestCase("Foo(net48)", "Foo", true)]
    [TestCase("Foo(netcoreapp3.1)", "Foo", true)]
    [TestCase("Foo(net8.0-windows10.0.19041.0)", "Foo", true)]
    [TestCase("Foo(network)", "Foo", false)]
    [TestCase("Foo(net)", "Foo", false)]
    [TestCase("Foo(Bar)", "Foo", false)]
    [TestCase("Foo(net8.0)Extra", "Foo", false)]
    [TestCase("FooBar", "Foo", false)]
    public void ProjectName_matches_exactly_or_with_a_target_framework_suffix_only(string loaded, string wanted, bool expected)
        => GetCompilationErrorsTool.MatchesProjectName(loaded, wanted).Should().Be(expected);

    [Test]
    public async Task Load_failures_are_counted_from_the_diagnostics_captured_with_the_solution()
    {
        // DIAG-002 review: re-reading Workspace.Diagnostics after compiling could pair this generation's
        // compiler results with a reload's failures. The fake's property and its paired snapshot disagree,
        // so reading the property again would report 0.
        using var adhoc = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var failure = new Contracts.WorkspaceLoadDiagnostic("Failure", "Cannot open 'Gone.csproj'", "Gone");
        var workspace = new DiagnosticTestWorkspace(adhoc.CurrentSolution, diagnostics: [], pairedDiagnostics: [failure]);
        var tool = new GetCompilationErrorsTool(workspace, NullLogger<GetCompilationErrorsTool>.Instance);

        var r = await tool.InvokeAsync(severity: null, projectName: null, ct: CancellationToken.None);

        r.Result!.LoadFailures.Should().Be(1);
    }

    [Test]
    public void Load_failures_count_named_projects_once_and_unnamed_failures_too()
    {
        var failures = new[]
        {
            new Contracts.WorkspaceLoadDiagnostic("Failure", "first message about Gone", "Gone"),
            new Contracts.WorkspaceLoadDiagnostic("Failure", "second message about Gone", "gone"),
            new Contracts.WorkspaceLoadDiagnostic("Failure", "Solution file could not be read", null),
            new Contracts.WorkspaceLoadDiagnostic("ProjectLoadedWithWarnings", "pruning", "Loaded"),
            new Contracts.WorkspaceLoadDiagnostic("SkippedUnsupportedProject", "esproj", "Frontend"),
        };

        GetCompilationErrorsTool.CountLoadFailures(failures).Should().Be(2,
            "Gone once, plus the failure that names no project; the reclassified and skipped kinds are not failures");
    }

    [Test]
    public async Task Analyzers_are_opt_in_solution_wide()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();

        var compilerOnly = await host.Tool.InvokeAsync(severity: null, projectName: null, ct: CancellationToken.None);
        compilerOnly.Result!.Diagnostics.Should().NotContain(d => d.Code == "CA1822");

        var withAnalyzers = await host.Tool.InvokeAsync(
            severity: null, projectName: null, includeAnalyzers: true, ct: CancellationToken.None);
        withAnalyzers.Error.Should().BeNull();
        var ca1822 = withAnalyzers.Result!.Diagnostics
            .Where(d => d.Code == "CA1822" && d.Location.FilePath.EndsWith("AnalyzerTarget.cs"))
            .ToList();
        ca1822.Should().ContainSingle("Twice is reported, pragma-suppressed Thrice is not");
        ca1822[0].Severity.Should().Be("Warning");
    }
}
