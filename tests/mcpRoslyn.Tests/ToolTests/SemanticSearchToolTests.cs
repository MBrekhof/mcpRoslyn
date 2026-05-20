using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class SemanticSearchToolTests
{
    [TestCase("derives-from:TestLib.Shape", "Circle", "Square")]
    [TestCase("implements:TestLib.IGreeter", "EnglishGreeter", "DutchGreeter")]
    public async Task SemanticSearch_returns_expected_matches(string pattern, params string[] expectedNames)
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var result = await host.Tool.InvokeAsync(pattern, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        var names = result.Result!.Matches.Select(m => m.Name).ToList();
        foreach (var expected in expectedNames)
            names.Should().Contain(expected);
    }

    [Test]
    public async Task SemanticSearch_has_attribute_finds_both_targets()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var result = await host.Tool.InvokeAsync("has-attribute:TestLib.MyMarkerAttribute", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        var names = result.Result!.Matches.Select(m => m.Name).ToList();
        names.Should().Contain("MarkedType");
        names.Should().Contain("MarkedMethod");
    }

    [Test]
    public async Task SemanticSearch_returns_filter_finds_int_returning_methods()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var result = await host.Tool.InvokeAsync("returns:int", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        var names = result.Result!.Matches.Select(m => m.Name).ToList();
        names.Should().Contain("Foo");
        names.Should().Contain("Bar");
    }

    [Test]
    public async Task SemanticSearch_parameter_type_finds_methods_taking_string()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var result = await host.Tool.InvokeAsync("parameter-type:string", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        var names = result.Result!.Matches.Select(m => m.Name).ToList();
        names.Should().Contain("Greet");
    }

    [Test]
    public async Task SemanticSearch_invalid_pattern_returns_error()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var result = await host.Tool.InvokeAsync("garbage:foo", ct: CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("INVALID_PATTERN");
    }
}
