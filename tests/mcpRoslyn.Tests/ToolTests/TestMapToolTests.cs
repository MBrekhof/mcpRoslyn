using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class TestMapToolTests
{
    [Test]
    public async Task EnglishGreeter_Greet_returns_reference_based_high_confidence_test()
    {
        await using var host = await TestHost.CreateAsync<TestMapTool>();
        var r = await host.Tool.InvokeAsync(symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)");
        r.Result.Should().NotBeNull();
        r.Result!.Candidates.Should().Contain(c =>
            c.TestSymbol.Contains("EnglishGreeterTests") &&
            c.Confidence == "high" &&
            c.Via == "reference");
    }

    [Test]
    public async Task Shape_returns_name_based_medium_confidence_test()
    {
        await using var host = await TestHost.CreateAsync<TestMapTool>();
        var r = await host.Tool.InvokeAsync(symbolId: "T:TestLib.Shape");
        r.Result.Should().NotBeNull();
        r.Result!.Candidates.Should().Contain(c =>
            c.TestSymbol.Contains("ShapeSpec") &&
            c.Confidence == "medium" &&
            c.Via == "name-match");
    }

    [Test]
    public async Task Symbol_with_no_tests_returns_empty_not_error()
    {
        await using var host = await TestHost.CreateAsync<TestMapTool>();
        var r = await host.Tool.InvokeAsync(symbolId: "T:TestLib.DutchGreeter");
        r.Result.Should().NotBeNull();
        // DutchGreeter has neither a test that references it (only EnglishGreeter does) NOR a name-matching test.
        // Some test projects MIGHT show up in scanned list but Candidates should be empty.
        r.Result!.Candidates.Should().BeEmpty();
        r.Error.Should().BeNull();
    }
}
