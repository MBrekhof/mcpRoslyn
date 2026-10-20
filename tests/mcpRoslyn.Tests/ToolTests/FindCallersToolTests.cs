using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class FindCallersToolTests
{
    [Test]
    public async Task FindCallers_of_IGreeter_Greet_finds_TestApp_call_site()
    {
        await using var host = await TestHost.CreateAsync<FindCallersTool>();
        var iGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "IGreeter.cs");

        // IGreeter.cs line 5: `    string Greet(string name);`
        // 'G' of Greet after 4 spaces + "string " (7 chars) = column 12 (1-based)
        var result = await host.Tool.InvokeAsync(iGreeterPath, line: 5, column: 12, symbolId: null, transitive: false, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Callers.Should().NotBeEmpty();
        result.Result.Callers.Should().Contain(c => c.CallSite.FilePath.EndsWith("Program.cs"));
    }

    [Test]
    public async Task FindCallers_with_stale_symbolId_returns_hint_to_use_workspace_symbol()
    {
        await using var host2 = await TestHost.CreateAsync<FindCallersTool>();

        var result = await host2.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "M:Bogus.Namespace.MissingMethod(System.String)",
            transitive: false, ct: CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
        result.Error.Hint.Should().NotBeNullOrEmpty();
        result.Error.Hint.Should().Contain("workspace_symbol");
    }

    [Test]
    public async Task Transitive_callers_on_cyclic_call_graph_do_not_duplicate_call_sites()
    {
        // TOOL-016: DeadChain.Ping <-> DeadChain.Pong form a cycle and DeadChain.Live calls Ping.
        // Without seeding the root symbol into `seen`, transitive traversal revisited Ping via Pong
        // and emitted the Live -> Ping and Pong -> Ping call sites a second time.
        await using var host = await TestHost.CreateAsync<FindCallersTool>();

        var result = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "M:TestLib.DeadChain.Ping(System.Int32)",
            transitive: true, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Callers.Select(c => c.Caller.Name)
            .Should().BeEquivalentTo(["Live", "Pong", "Ping"]);
        result.Result.Callers
            .Select(c => (c.Caller.SymbolId, c.CallSite.FilePath, c.CallSite.Line, c.CallSite.Column))
            .Should().OnlyHaveUniqueItems();
    }
}
