using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class HoverToolTests
{
    [Test]
    public async Task Hover_on_Greet_method_returns_method_signature()
    {
        await using var host = await TestHost.CreateAsync<HoverTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        // Line 5: `    public string Greet(string name) => $"Hello, {name}!";`
        // The 'G' of Greet is at column 19 (1-based, after 4 spaces + "public string ").
        var result = await host.Tool.InvokeAsync(englishGreeterPath, line: 5, column: 19, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Symbol.Name.Should().Be("Greet");
        result.Result.Symbol.Kind.Should().Be("Method");
        result.Result.Signature.Should().Contain("Greet");
        result.Result.Signature.Should().Contain("string");
    }
}
