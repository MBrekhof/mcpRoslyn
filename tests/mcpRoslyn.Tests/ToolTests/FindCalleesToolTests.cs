using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class FindCalleesToolTests
{
    [Test]
    public async Task Method_with_invocations_returns_callees()
    {
        await using var host = await TestHost.CreateAsync<FindCalleesTool>();

        // EnglishGreeter.Greet calls name.Trim() — fixture was updated in v1.3 Task 6.
        var r = await host.Tool.InvokeAsync(
            symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)");

        r.Error.Should().BeNull();
        r.Result.Should().NotBeNull();
        r.Result!.Callees.Should().NotBeEmpty();
    }

    [Test]
    public async Task Method_with_no_invocations_returns_empty_list_not_error()
    {
        await using var host = await TestHost.CreateAsync<FindCalleesTool>();

        // EnglishGreeter.GreetEmpty has an empty block body — no invocations.
        var r = await host.Tool.InvokeAsync(
            symbolId: "M:TestLib.EnglishGreeter.GreetEmpty");

        r.Error.Should().BeNull();
        r.Result.Should().NotBeNull();
        r.Result!.Callees.Should().BeEmpty();
    }

    [Test]
    public async Task Non_method_symbol_returns_SYMBOL_NOT_FOUND_with_hint()
    {
        await using var host = await TestHost.CreateAsync<FindCalleesTool>();

        // A type symbol (not a method) — should fail with SYMBOL_NOT_FOUND.
        var r = await host.Tool.InvokeAsync(
            symbolId: "T:TestLib.EnglishGreeter");

        r.Result.Should().BeNull();
        r.Error.Should().NotBeNull();
        r.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
        r.Error.Hint.Should().NotBeNullOrEmpty();
    }
}
