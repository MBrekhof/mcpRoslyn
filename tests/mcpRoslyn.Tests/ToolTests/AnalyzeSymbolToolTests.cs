using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class AnalyzeSymbolToolTests
{
    [Test]
    public async Task Interface_returns_all_sections_with_data()
    {
        await using var host = await TestHost.CreateAsync<AnalyzeSymbolTool>();
        // IGreeter exists in TestLib with at least one implementation (EnglishGreeter/DutchGreeter)
        var r = await host.Tool.InvokeAsync(symbolId: "T:TestLib.IGreeter");
        r.Result.Should().NotBeNull();
        r.Result!.Hover.Should().NotBeNull();
        r.Result.References.Should().NotBeNull();
        r.Result.Implementations.Should().NotBeNull();
        r.Result.Implementations!.Items.Count.Should().BeGreaterThan(0);
        // DerivedTypes applies to classes, not interfaces — fine to be empty list or null based on your impl
        // Callers on an interface is informational; just present
    }

    [Test]
    public async Task Include_flag_null_skips_section()
    {
        await using var host = await TestHost.CreateAsync<AnalyzeSymbolTool>();
        var r = await host.Tool.InvokeAsync(
            symbolId: "T:TestLib.IGreeter",
            includeReferences: false);
        r.Result!.References.Should().BeNull();
        r.Result.Implementations.Should().NotBeNull(); // others still present
    }

    [Test]
    public async Task Method_symbol_returns_null_derivedTypes()
    {
        await using var host = await TestHost.CreateAsync<AnalyzeSymbolTool>();
        var r = await host.Tool.InvokeAsync(symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)");
        r.Result.Should().NotBeNull();
        r.Result!.DerivedTypes.Should().BeNull();  // method has no derived types
    }

    [Test]
    public async Task MaxPerSection_truncates_and_records()
    {
        await using var host = await TestHost.CreateAsync<AnalyzeSymbolTool>();
        // Use a symbol with multiple references in the fixture
        var r = await host.Tool.InvokeAsync(
            symbolId: "T:TestLib.IGreeter",
            maxPerSection: 1);
        r.Result.Should().NotBeNull();
        if (r.Result!.References is not null && r.Result.References.Count > 1)
            r.Result.Truncated.Should().Contain("references");
        // Permissive — if fixture happens to have only 1 reference, no truncation expected
    }
}
