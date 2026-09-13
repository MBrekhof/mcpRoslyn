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

    [Test]
    public async Task FindImplementations_abstract_member_returns_its_overrides()
    {
        // TOOL-011 review: the tool promises abstract members, but Roslyn's FindImplementationsAsync returns
        // nothing for a class member — its implementations are its overrides.
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();
        var result = await host.Tool.InvokeAsync(null, null, null, symbolId: "M:TestLib.Shape.Area", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Implementations.Should().HaveCount(2);
        result.Result.Implementations.Should().OnlyContain(l => l.FilePath.EndsWith("Shape.cs"));
    }

    [Test]
    public async Task FindImplementations_skips_an_abstract_intermediate_override()
    {
        // TOOL-011 review: FindOverridesAsync is transitive and includes abstract overrides, which implement nothing.
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib", "Shape.cs");
        var backup = await File.ReadAllTextAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, backup +
                "\npublic abstract class Polygon : Shape { public abstract override double Area(); }\n" +
                "public class Triangle : Polygon { public override double Area() => 1; }\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            var result = await host.Tool.InvokeAsync(null, null, null, symbolId: "M:TestLib.Shape.Area", ct: CancellationToken.None);

            result.Error.Should().BeNull();
            result.Result!.Implementations.Should().HaveCount(3, "Circle, Square and Triangle — not Polygon");
        }
        finally
        {
            await File.WriteAllTextAsync(path, backup);
        }
    }

    private static string Key(SymbolLocation l) => $"{l.FilePath}|{l.Line}:{l.Column}-{l.EndLine}:{l.EndColumn}";
}
