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
}
