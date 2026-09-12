using FluentAssertions;
using mcpRoslyn.Contracts;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindReferencesToolTests
{
    private static string IGreeterPath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

    [Test]
    public async Task FindReferences_IGreeter_finds_three_or_more()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();

        // Line 3, column 18 — lands on `IGreeter` in `public interface IGreeter`
        // File content:
        //   1: namespace TestLib;
        //   2: (blank)
        //   3: public interface IGreeter
        // "public interface " is 17 chars, so `I` of IGreeter is at column 18.
        var result = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

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

    [Test]
    public async Task FindReferences_returns_unique_source_positions()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        var result = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        var keys = result.Result!.References.Select(Key).ToList();
        keys.Should().OnlyHaveUniqueItems("a reference site should be reported once per source position");
    }

    [Test]
    public async Task FindReferences_two_consecutive_calls_return_identical_sets()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();

        var first = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);
        var second = await host.Tool.InvokeAsync(IGreeterPath, line: 3, column: 18, symbolId: null, ct: CancellationToken.None);

        first.Error.Should().BeNull();
        second.Error.Should().BeNull();

        var firstKeys = first.Result!.References.Select(Key).OrderBy(k => k).ToList();
        var secondKeys = second.Result!.References.Select(Key).OrderBy(k => k).ToList();
        secondKeys.Should().Equal(firstKeys, "two back-to-back calls with no workspace changes must yield the same reference set");
    }

    [Test]
    public async Task SymbolId_naming_distinct_types_in_two_projects_is_AMBIGUOUS_not_the_first_one()
    {
        // IDX-005: "T:Shared.Dup" names a type in TestApp and another in TestWeb.
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        var result = await host.Tool.InvokeAsync(filePath: null, line: null, column: null, symbolId: "T:Shared.Dup",
            ct: CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("AMBIGUOUS_SYMBOL_ID");
        result.Error.Message.Should().Contain(Path.Combine("TestApp", "Dup.cs")).And.Contain(Path.Combine("TestWeb", "Dup.cs"));

        // A file linked into both projects declares two symbols at one path — still ambiguous.
        var linked = await host.Tool.InvokeAsync(filePath: null, line: null, column: null, symbolId: "T:Shared.Linked",
            ct: CancellationToken.None);
        linked.Error!.Code.Should().Be("AMBIGUOUS_SYMBOL_ID");

        // A symbol seen through several compilations (TestLib, referenced by others) is not ambiguous.
        var single = await host.Tool.InvokeAsync(filePath: null, line: null, column: null, symbolId: "T:TestLib.IGreeter",
            ct: CancellationToken.None);
        single.Error.Should().BeNull();
    }

    private static string Key(SymbolLocation l) => $"{l.FilePath}|{l.Line}:{l.Column}-{l.EndLine}:{l.EndColumn}";
}
