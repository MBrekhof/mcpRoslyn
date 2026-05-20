using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class WorkspaceSymbolToolTests
{
    [Test]
    public async Task WorkspaceSymbol_finds_Greeter_types()
    {
        await using var host = await TestHost.CreateAsync<WorkspaceSymbolTool>();

        var result = await host.Tool.InvokeAsync("Greeter", null, null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        var names = result.Result!.Symbols.Select(s => s.Name).ToList();
        names.Should().Contain("IGreeter");
        names.Should().Contain("EnglishGreeter");
        names.Should().Contain("DutchGreeter");
    }

    [Test]
    public async Task WorkspaceSymbol_respects_kinds_filter()
    {
        await using var host2 = await TestHost.CreateAsync<WorkspaceSymbolTool>();

        var result = await host2.Tool.InvokeAsync("Greeter", new[] { "Interface" }, null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        var symbols = result.Result!.Symbols;
        symbols.Should().Contain(s => s.Name == "IGreeter");
        symbols.Should().NotContain(s => s.Name == "EnglishGreeter");
    }
}
