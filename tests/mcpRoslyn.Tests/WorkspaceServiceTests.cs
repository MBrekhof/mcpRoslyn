using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public class WorkspaceServiceTests
{
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    [Test]
    public async Task LoadAsync_loads_fixture_solution_and_finds_both_projects()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();

        sut.LoadedProjectCount.Should().Be(4);
    }

    [Test]
    public async Task GetFreshSolutionAsync_picks_up_file_changes_via_mtime()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();

        var solution = await sut.GetFreshSolutionAsync();
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "EnglishGreeter.cs");
        var originalText = (await doc.GetTextAsync()).ToString();

        // mutate the file on disk
        var backup = File.ReadAllText(doc.FilePath!);
        try
        {
            File.WriteAllText(doc.FilePath!, originalText.Replace("Hello", "Hi"));
            File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

            var refreshed = await sut.GetFreshSolutionAsync();
            var refreshedDoc = refreshed.GetDocument(doc.Id)!;
            var refreshedText = (await refreshedDoc.GetTextAsync()).ToString();

            refreshedText.Should().Contain("Hi, ");
            refreshedText.Should().NotContain("Hello,");
        }
        finally
        {
            File.WriteAllText(doc.FilePath!, backup);
        }
    }

    [Test]
    public async Task LoadAsync_warmup_populates_project_compilations()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        foreach (var project in solution.Projects)
        {
            project.TryGetCompilation(out var compilation).Should().BeTrue(
                "warm-up should have cached the compilation for {0}", project.Name);
            compilation.Should().NotBeNull();
        }
    }

    [Test]
    public async Task LoadAsync_returns_before_warmup_completes()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();

        // WarmupTask must be a fresh Task (not the Task.CompletedTask sentinel),
        // proving warm-up was kicked off rather than awaited inline.
        sut.WarmupTask.Should().NotBeSameAs(Task.CompletedTask);

        // And it must complete cleanly when given the chance.
        await sut.WarmupTask;
        sut.WarmupTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_clean_fixture_produces_empty_diagnostics()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();

        sut.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task LoadAsync_broken_solution_captures_diagnostics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-broken-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // .sln referencing a project whose .csproj does not exist on disk.
            // MSBuildWorkspace fires WorkspaceFailed when it cannot evaluate the project file.
            var slnContent =
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing\\Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n";
            var slnPath = Path.Combine(tempDir, "Broken.sln");
            File.WriteAllText(slnPath, slnContent);

            var options = new McpRoslynOptions { SolutionPath = slnPath };
            var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

            await sut.LoadAsync();

            sut.Diagnostics.Should().NotBeEmpty(
                "MSBuildWorkspace should report a failure for the missing referenced project");
            sut.Diagnostics.Should().Contain(d =>
                d.Message.Contains("Missing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // Both real MSBuild wordings seen against BPG and duetGPT, plus the no-path case. The .esproj
    // message quotes the bare extension too ('.esproj'), which must not win over the full path.
    [TestCase(
        @"Cannot open project 'C:\Projects\BPG\src\bpg-frontend\bpg-frontend.esproj' because the file extension '.esproj' is not associated with a language.",
        "bpg-frontend")]
    [TestCase(
        @"Msbuild failed when processing the file 'C:\Projects\duetgpt\duetGPT\duetGPT.csproj' with message: PackageReference System.Text.Json will not be pruned.",
        "duetGPT")]
    [TestCase("Solution file could not be read", null)]
    public void ExtractProjectName_takes_the_name_from_the_quoted_project_path(string message, string? expected)
        => WorkspaceService.ExtractProjectName(message).Should().Be(expected);

    [Test]
    public void Failure_naming_a_project_that_loaded_is_re_kinded_but_one_naming_an_absent_project_is_not()
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "duetGPT", "duetGPT.Tests" };

        // Real message from a run where all 4 projects loaded — a pruning suggestion, not a failure.
        var pruning = new Contracts.WorkspaceLoadDiagnostic("Failure",
            @"Msbuild failed when processing the file 'C:\x\duetGPT.csproj' with message: PackageReference System.Text.Json will not be pruned.",
            "duetGPT");
        WorkspaceService.Reclassify(pruning, loaded).Kind.Should().Be("ProjectLoadedWithWarnings");

        // A project that is genuinely missing from the loaded solution keeps its Failure kind —
        // that is the signal a declared-but-unloaded project should produce.
        var missing = new Contracts.WorkspaceLoadDiagnostic("Failure",
            @"Msbuild failed when processing the file 'C:\x\Gone.csproj' with message: whatever.", "Gone");
        WorkspaceService.Reclassify(missing, loaded).Kind.Should().Be("Failure");

        // Already-classified kinds are left alone.
        var esproj = new Contracts.WorkspaceLoadDiagnostic("SkippedUnsupportedProject", "…", "Frontend");
        WorkspaceService.Reclassify(esproj, loaded).Kind.Should().Be("SkippedUnsupportedProject");
    }

    [Test]
    public async Task Project_with_no_Roslyn_language_is_classified_not_reported_as_a_failure()
    {
        // A polyglot solution (.esproj/.njsproj/.sqlproj) always trips WorkspaceFailed. That is
        // expected, so it gets its own kind instead of reading as a broken solution (TOOL-006).
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-polyglot-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Frontend.esproj"), "<Project />\n");
            var slnPath = Path.Combine(tempDir, "Polyglot.sln");
            File.WriteAllText(slnPath,
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{54A90642-561A-4BB1-A94E-469ADEE60C69}\") = \"Frontend\", \"Frontend.esproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
                "EndProject\n");

            var sut = new WorkspaceService(
                new McpRoslynOptions { SolutionPath = slnPath }, NullLogger<WorkspaceService>.Instance);
            await sut.LoadAsync();

            sut.Diagnostics.Should().Contain(d => d.Kind == "SkippedUnsupportedProject");
            sut.Diagnostics.Should().NotContain(d => d.Kind == "Failure");
            sut.Diagnostics.Should().Contain(d => d.ProjectName == "Frontend");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ReloadAsync_clears_prior_diagnostics()
    {
        // First load a broken solution to populate diagnostics, then reload pointing
        // at the clean fixture and verify the stale diagnostics are gone. We can't
        // re-point options at runtime, so we use two separate WorkspaceService instances
        // here — the contract under test is that Clear() runs at the top of LoadUnsafeAsync.
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-broken-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var slnContent =
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing\\Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n";
            var slnPath = Path.Combine(tempDir, "Broken.sln");
            File.WriteAllText(slnPath, slnContent);

            var options = new McpRoslynOptions { SolutionPath = slnPath };
            var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

            await sut.LoadAsync();
            sut.Diagnostics.Should().NotBeEmpty();
            var firstDiagCount = sut.Diagnostics.Count;

            await sut.ReloadAsync();
            // After reload of the SAME (still-broken) solution, diagnostics should be
            // re-collected fresh — not stacked on top of the prior list.
            sut.Diagnostics.Count.Should().Be(firstDiagCount,
                "ReloadAsync should clear and re-collect diagnostics, not append");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
