using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GetCompilationErrorsToolTests
{
    [Test]
    public async Task GetCompilationErrors_returns_at_least_one_error()
    {
        var sut = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await sut.InvokeAsync(severity: null, projectName: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().Contain(d => d.Severity == "Error");
    }

    [Test]
    public async Task GetCompilationErrors_filtered_to_TestLib_returns_no_errors()
    {
        var sut = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await sut.InvokeAsync(severity: "Error", projectName: "TestLib", CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().BeEmpty();
    }
}
