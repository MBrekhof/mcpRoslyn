using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GotoDefinitionToolTests
{
    [Test]
    public async Task GotoDefinition_finds_IGreeter_from_EnglishGreeter_base_list()
    {
        var sut = await TestHost.CreateAsync<GotoDefinitionTool>();

        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        // Line 3 (1-based): `public class EnglishGreeter : IGreeter`
        // 'I' of IGreeter is at column 31 (1-based, spaces only — no tabs).
        var result = await sut.InvokeAsync(englishGreeterPath, line: 3, column: 31, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Definitions.Should().NotBeEmpty();
        result.Result.Definitions.First().FilePath.Should().EndWith("IGreeter.cs");
    }
}
