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
        var sut = await TestHost.CreateAsync<ReloadWorkspaceTool>();
        var result = await sut.InvokeAsync(CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Loaded.Should().BeTrue();
        result.Result.ProjectCount.Should().Be(2);
        result.Result.DurationMs.Should().BeGreaterThan(0);
    }
}
