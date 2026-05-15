using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindDerivedTypesToolTests
{
    [Test]
    public async Task FindDerivedTypes_of_Shape_returns_Circle_and_Square()
    {
        var sut = await TestHost.CreateAsync<FindDerivedTypesTool>();
        var shapePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "Shape.cs");

        // Line 3: `public abstract class Shape`
        // 'S' of Shape after "public abstract class " (22 chars) = column 23 (1-based)
        var result = await sut.InvokeAsync(shapePath, line: 3, column: 23, symbolId: null, transitive: false, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.DerivedTypes.Should().HaveCount(2);
        var names = result.Result.DerivedTypes.Select(s => s.Name).ToList();
        names.Should().Contain("Circle");
        names.Should().Contain("Square");
    }
}
