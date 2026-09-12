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

        // Line 5: `    public string Greet(string name) => $"Hello, {name.Trim()}!";`
        // The 'G' of Greet is at column 19 (1-based, after 4 spaces + "public string ").
        var result = await host.Tool.InvokeAsync(englishGreeterPath, line: 5, column: 19, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Symbol.Name.Should().Be("Greet");
        result.Result.Symbol.Kind.Should().Be("Method");
        result.Result.Signature.Should().Contain("Greet");
        result.Result.Signature.Should().Contain("string");
    }

    [Test]
    public async Task Out_of_range_position_is_POSITION_INVALID_and_end_of_line_is_not()
    {
        // TOOL-010: an unchecked column used to run on into the following lines, so a tool could
        // answer about (or rename) a symbol the caller never pointed at.
        await using var host = await TestHost.CreateAsync<HoverTool>();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var lines = File.ReadAllLines(path);
        var line5Length = lines[4].Length;

        foreach (var (line, column) in new[] { (5, line5Length + 2), (5, 0), (0, 1), (lines.Length + 5, 1) })
        {
            var result = await host.Tool.InvokeAsync(path, line, column, ct: CancellationToken.None);
            result.Error.Should().NotBeNull($"{line}:{column} is outside the file");
            result.Error!.Code.Should().Be("POSITION_INVALID", $"{line}:{column} is outside the file");
        }

        // The cursor just past the last character is a real editor position.
        var endOfLine = await host.Tool.InvokeAsync(path, 5, line5Length + 1, ct: CancellationToken.None);
        (endOfLine.Error?.Code ?? "OK").Should().BeOneOf("OK", "SYMBOL_NOT_FOUND");
    }
}
