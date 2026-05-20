using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindReferencesToolTests
{
    [Test]
    public async Task FindReferences_IGreeter_finds_three_or_more()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        var iGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

        // Line 3, column 18 — lands on `IGreeter` in `public interface IGreeter`
        // File content:
        //   1: namespace TestLib;
        //   2: (blank)
        //   3: public interface IGreeter
        // "public interface " is 17 chars, so `I` of IGreeter is at column 18.
        var result = await host.Tool.InvokeAsync(iGreeterPath, line: 3, column: 18, symbolId: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Symbol.Name.Should().Be("IGreeter");
        // We expect references from:
        //  - EnglishGreeter.cs (base list)
        //  - DutchGreeter.cs (base list)
        //  - TestApp/Program.cs (`IGreeter greeter = ...`)
        // Could also include the declaration itself depending on Roslyn settings.
        result.Result.References.Should().HaveCountGreaterOrEqualTo(3);
    }
}
