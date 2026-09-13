using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class ReloadWorkspaceToolTests
{
    [Test]
    public async Task ReloadWorkspace_returns_project_count_and_duration()
    {
        await using var host = await TestHost.CreateAsync<ReloadWorkspaceTool>();
        var result = await host.Tool.InvokeAsync(ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Loaded.Should().BeTrue();
        result.Result.ProjectCount.Should().Be(4);
        result.Result.DurationMs.Should().BeGreaterThan(0);
        result.Result.Diagnostics.Should().NotBeNull();
        result.Result.Diagnostics.Should().BeEmpty(
            "clean fixture solution should reload without MSBuild diagnostics");
    }

    [Test]
    public async Task ReloadWorkspace_switches_to_another_solution_and_stays_on_it()
    {
        // WS-005: a session pinned to a lean solution was blind to projects only the full one declares, with no
        // way to switch short of restarting the server.
        await using var host = await TestHost.CreateAsync<ReloadWorkspaceTool>();
        var full = FixturePaths.TestSolutionPath;
        var lean = Path.Combine(Path.GetDirectoryName(full)!, $"Lean{Guid.NewGuid():N}.sln");
        try
        {
            await File.WriteAllTextAsync(lean,
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"TestLib\", \"TestLib\\TestLib.csproj\", \"{44444444-4444-4444-4444-444444444444}\"\n" +
                "EndProject\n");

            var switched = await host.Tool.InvokeAsync(solutionPath: lean, ct: CancellationToken.None);
            switched.Error.Should().BeNull();
            switched.Result!.ProjectCount.Should().Be(1);
            switched.Result.SolutionPath.Should().Be(lean);

            var again = await host.Tool.InvokeAsync(ct: CancellationToken.None);
            again.Result!.SolutionPath.Should().Be(lean, "a plain reload re-evaluates the solution now being served");
            again.Result.ProjectCount.Should().Be(1);

            var back = await host.Tool.InvokeAsync(solutionPath: full, ct: CancellationToken.None);
            back.Result!.ProjectCount.Should().Be(4);
        }
        finally
        {
            File.Delete(lean);
        }
    }

    [TestCase("TestSolution.sln")]              // relative
    [TestCase(@"C:\no\such\place\Missing.sln")] // missing
    [TestCase(@"C:\Windows\win.ini")]           // not a solution
    public async Task ReloadWorkspace_rejects_a_path_that_is_not_an_existing_solution(string solutionPath)
    {
        await using var host = await TestHost.CreateAsync<ReloadWorkspaceTool>();

        var result = await host.Tool.InvokeAsync(solutionPath: solutionPath, ct: CancellationToken.None);

        result.Error!.Code.Should().Be("SOLUTION_NOT_FOUND");
        var current = await host.Tool.InvokeAsync(ct: CancellationToken.None);
        current.Result!.ProjectCount.Should().Be(4, "the solution being served is untouched");
        current.Result.SolutionPath.Should().Be(FixturePaths.TestSolutionPath);
    }
}
