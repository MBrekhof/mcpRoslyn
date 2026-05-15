using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindImplementationsToolTests
{
    [Test]
    public async Task FindImplementations_IGreeter_returns_two_impls()
    {
        var sut = await TestHost.CreateAsync<FindImplementationsTool>();
        var iGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

        // line 3 col 18 lands on IGreeter identifier — same position as find_references test
        var result = await sut.InvokeAsync(iGreeterPath, line: 3, column: 18, symbolId: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Implementations.Should().HaveCount(2);
        var paths = result.Result.Implementations.Select(l => l.FilePath).ToList();
        paths.Should().Contain(p => p.EndsWith("EnglishGreeter.cs"));
        paths.Should().Contain(p => p.EndsWith("DutchGreeter.cs"));
    }
}
