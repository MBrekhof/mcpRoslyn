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

        var result = await host.Tool.InvokeAsync("Greeter", null, null, ct: CancellationToken.None);

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

        var result = await host2.Tool.InvokeAsync("Greeter", new[] { "Interface" }, null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        var symbols = result.Result!.Symbols;
        symbols.Should().Contain(s => s.Name == "IGreeter");
        symbols.Should().NotContain(s => s.Name == "EnglishGreeter");
    }

    [Test]
    public async Task WorkspaceSymbol_lists_same_named_types_from_two_projects_separately()
    {
        // IDX-005: Shared.Dup is declared in TestApp and in TestWeb; both share "T:Shared.Dup".
        await using var host = await TestHost.CreateAsync<WorkspaceSymbolTool>();
        var result = await host.Tool.InvokeAsync("Dup", null, null, ct: CancellationToken.None);

        result.Result!.Symbols.Where(s => s.SymbolId == "T:Shared.Dup").Should().HaveCount(2);
    }

    [Test]
    public async Task WorkspaceSymbol_reports_truncation_only_when_more_matched()
    {
        await using var host = await TestHost.CreateAsync<WorkspaceSymbolTool>();

        var capped = await host.Tool.InvokeAsync("Greeter", null, maxResults: 1, ct: CancellationToken.None);
        capped.Result!.Symbols.Should().ContainSingle();
        capped.Result.Truncated.Should().BeTrue();

        var all = await host.Tool.InvokeAsync("Greeter", null, null, ct: CancellationToken.None);
        all.Result!.Truncated.Should().BeFalse();

        // Exactly at the cap is complete, not truncated.
        var exact = await host.Tool.InvokeAsync("Greeter", null, maxResults: all.Result.Symbols.Count, ct: CancellationToken.None);
        exact.Result!.Truncated.Should().BeFalse();
    }
}
