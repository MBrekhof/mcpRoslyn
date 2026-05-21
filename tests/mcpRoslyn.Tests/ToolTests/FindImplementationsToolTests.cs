using FluentAssertions;
using mcpRoslyn.Contracts;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindImplementationsToolTests
{
    private static string IGreeterPath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

    [Test]
    public async Task FindImplementations_IGreeter_returns_two_impls()
    {
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();

        // line 3 col 18 lands on IGreeter identifier — same position as find_references test
        var result = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Implementations.Should().HaveCount(2);
        var paths = result.Result.Implementations.Select(l => l.FilePath).ToList();
        paths.Should().Contain(p => p.EndsWith("EnglishGreeter.cs"));
        paths.Should().Contain(p => p.EndsWith("DutchGreeter.cs"));
    }

    [Test]
    public async Task FindImplementations_returns_unique_source_positions()
    {
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();
        var result = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        var keys = result.Result!.Implementations.Select(Key).ToList();
        keys.Should().OnlyHaveUniqueItems("an implementation site should be reported once per source position");
    }

    [Test]
    public async Task FindImplementations_two_consecutive_calls_return_identical_sets()
    {
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();

        var first = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);
        var second = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

        first.Error.Should().BeNull();
        second.Error.Should().BeNull();

        var firstKeys = first.Result!.Implementations.Select(Key).OrderBy(k => k).ToList();
        var secondKeys = second.Result!.Implementations.Select(Key).OrderBy(k => k).ToList();
        secondKeys.Should().Equal(firstKeys, "two back-to-back calls with no workspace changes must yield the same implementation set");
    }

    private static string Key(SymbolLocation l) => $"{l.FilePath}|{l.Line}:{l.Column}-{l.EndLine}:{l.EndColumn}";
}
