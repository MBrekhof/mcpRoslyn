using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindCallersToolTests
{
    [Test]
    public async Task FindCallers_of_IGreeter_Greet_finds_TestApp_call_site()
    {
        var sut = await TestHost.CreateAsync<FindCallersTool>();
        var iGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

        // IGreeter.cs line 5: `    string Greet(string name);`
        // 'G' of Greet after 4 spaces + "string " (7 chars) = column 12 (1-based)
        var result = await sut.InvokeAsync(iGreeterPath, line: 5, column: 12, symbolId: null, transitive: false, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Callers.Should().NotBeEmpty();
        result.Result.Callers.Should().Contain(c => c.CallSite.FilePath.EndsWith("Program.cs"));
    }
}
