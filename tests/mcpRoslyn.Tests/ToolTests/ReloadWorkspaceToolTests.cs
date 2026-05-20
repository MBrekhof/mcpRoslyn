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
        var result = await host.Tool.InvokeAsync(CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Loaded.Should().BeTrue();
        result.Result.ProjectCount.Should().Be(4);
        result.Result.DurationMs.Should().BeGreaterThan(0);
        result.Result.Diagnostics.Should().NotBeNull();
        result.Result.Diagnostics.Should().BeEmpty(
            "clean fixture solution should reload without MSBuild diagnostics");
    }
}
