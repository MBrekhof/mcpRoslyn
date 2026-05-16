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

        sut.LoadedProjectCount.Should().Be(2);
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
}
