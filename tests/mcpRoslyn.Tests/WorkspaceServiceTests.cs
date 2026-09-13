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

        // DIAG-002: when the loaded paths are known, the quoted path decides — B\Foo.csproj failing is not
        // "loaded" just because A\Foo.csproj shares its file name.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo" };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\sln\A\Foo.csproj" };
        var otherFoo = new Contracts.WorkspaceLoadDiagnostic("Failure",
            @"Msbuild failed when processing the file 'C:\sln\B\Foo.csproj' with message: missing SDK.", "Foo");
        WorkspaceService.Reclassify(otherFoo, names, paths).Kind.Should().Be("Failure");
        var sameFoo = otherFoo with { Message = @"Msbuild failed when processing the file 'C:\sln\A\Foo.csproj' with message: pruning." };
        WorkspaceService.Reclassify(sameFoo, names, paths).Kind.Should().Be("ProjectLoadedWithWarnings");
        // A relative quoted path resolves against the solution directory instead of falling back to the name.
        var relativeOther = otherFoo with { Message = @"Cannot open project 'B\Foo.csproj' because it is missing." };
        WorkspaceService.Reclassify(relativeOther, names, paths, @"C:\sln").Kind.Should().Be("Failure");
        var relativeSame = otherFoo with { Message = @"Cannot open project 'B\..\A\Foo.csproj' because of a warning." };
        WorkspaceService.Reclassify(relativeSame, names, paths, @"C:\sln").Kind.Should().Be("ProjectLoadedWithWarnings");

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
    public async Task Indexed_tool_called_before_warmup_finishes_returns_the_settled_answer()
    {
        // IDX-002: the tool must wait for the index, not answer from a half-built one as success.
        await using var sut = new WorkspaceService(
            new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath }, NullLogger<WorkspaceService>.Instance);
        // Hold the index build so the call provably lands before it, however fast the machine is.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.BeforeIndexBuild = () => release.Task;
        await sut.LoadAsync();
        var tool = new mcpRoslyn.Tools.FindRegistrationsTool(sut, NullLogger<mcpRoslyn.Tools.FindRegistrationsTool>.Instance);

        try
        {
            var earlyCall = tool.InvokeAsync(includeConsumers: false);
            await Task.Delay(500);
            earlyCall.IsCompleted.Should().BeFalse("the index is held unbuilt, so the tool must still be waiting for it");

            release.SetResult();
            var early = await earlyCall;
            var settled = await tool.InvokeAsync(includeConsumers: false);

            settled.Result!.Registrations.Should().NotBeEmpty();
            early.Error.Should().BeNull();
            early.Result!.Registrations.Should().HaveCount(settled.Result.Registrations.Count);
        }
        finally
        {
            release.TrySetResult(); // the hook ignores cancellation: a failed assertion must not hang disposal
        }
    }

    [Test]
    public async Task Reload_during_warmup_does_not_mix_generations_into_the_indexes()
    {
        // WS-006: the retired warm-up must not build into the new generation's indexes.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        int cleanRoutes, cleanRegistrations;
        await using (var clean = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance))
        {
            await clean.LoadAsync();
            await clean.WarmupTask;
            cleanRoutes = clean.InvocationIndex.QueryRoutes().Count;
            cleanRegistrations = clean.InvocationIndex.QueryDi().Registrations.Count;
        }

        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        // Park the first generation's warm-up just before its index build, reload while it is
        // parked, let the successor finish, and only then release the retired one.
        var firstReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        sut.BeforeIndexBuild = () =>
        {
            if (Interlocked.Increment(ref builds) != 1) return Task.CompletedTask;
            firstReached.SetResult();
            return releaseFirst.Task;
        };

        await sut.LoadAsync();
        var firstWarmup = sut.WarmupTask;
        try
        {
            await firstReached.Task;
            // An indexed query that starts against the parked generation and outlives it.
            var spanning = sut.GetIndexedSolutionAsync();
            await sut.ReloadAsync();
            await sut.WarmupTask;
            releaseFirst.SetResult();
            try { await firstWarmup; } catch (OperationCanceledException) { /* retired: cancelled, as intended */ }

            (await spanning).SymbolIndex.Should().BeSameAs(sut.SymbolIndex,
                "a query waiting on a retired generation must answer from its successor");
            sut.InvocationIndex.QueryRoutes().Should().HaveCount(cleanRoutes);
            sut.InvocationIndex.QueryDi().Registrations.Should().HaveCount(cleanRegistrations);
        }
        finally
        {
            releaseFirst.TrySetResult(); // the hook ignores cancellation: a failed assertion must not hang disposal
        }
    }

    [Test]
    public async Task Failed_reload_keeps_the_previous_generation_usable()
    {
        // WS-006: a reload that cannot open the solution must not leave empty indexes behind.
        await using var sut = new WorkspaceService(
            new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath }, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;
        var solution = await sut.GetFreshSolutionAsync();
        var before = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", solution).Count;
        before.Should().BeGreaterThan(0);

        var symbolIndexBefore = sut.SymbolIndex;
        var hostedBefore = sut.InvocationIndex.QueryHostedServices().Count;

        var hidden = FixturePaths.TestSolutionPath + ".hidden";
        File.Move(FixturePaths.TestSolutionPath, hidden);
        try
        {
            var reload = () => sut.ReloadAsync();
            await reload.Should().ThrowAsync<FileNotFoundException>();
        }
        finally
        {
            File.Move(hidden, FixturePaths.TestSolutionPath);
        }

        // The same generation still serves. Asserting answers alone isn't enough: the old code
        // swapped in empty indexes, and the full re-walk its cleared mtime cache then forced
        // masked that for attribute queries while silently losing hosted-service subclasses.
        sut.LoadedProjectCount.Should().Be(4);
        sut.SymbolIndex.Should().BeSameAs(symbolIndexBefore);
        var ready = await sut.GetIndexedSolutionAsync();
        ready.InvocationIndex.QueryHostedServices().Should().HaveCount(hostedBefore);
        ready.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", ready.Solution).Should().HaveCount(before);
    }

    [Test]
    public async Task Solution_with_diagnostics_does_not_wait_for_the_index_build()
    {
        // get_compilation_errors needs no index; its paired read must not block on warm-up.
        await using var sut = new WorkspaceService(
            new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath }, NullLogger<WorkspaceService>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.BeforeIndexBuild = () => release.Task;
        try
        {
            await sut.LoadAsync();
            var loaded = await sut.GetFreshSolutionWithDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            loaded.Solution.Projects.Should().NotBeEmpty();
            sut.WarmupTask.IsCompleted.Should().BeFalse("the index build is still held");
        }
        finally
        {
            release.TrySetResult(); // the hook ignores cancellation: never leave disposal waiting on it
        }
    }

    [Test]
    public async Task Failed_reload_keeps_the_serving_generations_load_diagnostics()
    {
        // DIAG-002 review: diagnostics were cleared before a reload opened its solution, so a reload that
        // then failed left the old solution serving with no record of its load failures — and
        // get_compilation_errors reporting LoadFailures 0.
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-keepdiags-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var slnPath = Path.Combine(tempDir, "Broken.sln");
            File.WriteAllText(slnPath,
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing\\Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n");
            await using var sut = new WorkspaceService(
                new McpRoslynOptions { SolutionPath = slnPath }, NullLogger<WorkspaceService>.Instance);
            await sut.LoadAsync();
            sut.Diagnostics.Should().Contain(d => d.Message.Contains("Missing", StringComparison.OrdinalIgnoreCase));

            File.Delete(slnPath);
            var reload = () => sut.ReloadAsync();
            await reload.Should().ThrowAsync<Exception>();

            sut.Diagnostics.Should().Contain(d => d.Message.Contains("Missing", StringComparison.OrdinalIgnoreCase),
                "the generation still serving keeps its own load diagnostics");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ReloadAsync_clears_prior_diagnostics()
    {
        // Reload the same broken solution: each successful load publishes a generation carrying its own
        // diagnostics, so a reload replaces them rather than appending to the previous load's list.
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
