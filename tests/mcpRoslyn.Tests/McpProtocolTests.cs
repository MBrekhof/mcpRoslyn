using FluentAssertions;
using ModelContextProtocol.Client;
using mcpRoslyn.Tests.TestHelpers;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

/// <summary>Through the real stdio transport against the built server, as an MCP client sees it.</summary>
[TestFixture]
public sealed class McpProtocolTests
{
    [Test]
    public async Task A_failed_tool_call_sets_isError_and_a_successful_one_does_not()
    {
        // TOOL-011: a failure travelled as an ordinary ToolResult payload, so isError stayed false and a
        // client keyed on it read every failure as success.
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "mcpRoslyn", "bin", configuration!, "net10.0", "win-x64", "mcpRoslyn.exe"));
        File.Exists(exe).Should().BeTrue($"the test project references the server, so building it builds {exe}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mcpRoslyn",
            Command = exe,
            Arguments = ["--solution", FixturePaths.TestSolutionPath],
        }));

        // Parameters without a C# default are required by the SDK's binding, so every one is passed: a missing
        // argument would fail in the SDK itself and prove nothing about the filter.
        var failed = await client.CallToolAsync("find_references", new Dictionary<string, object?>
            { ["filePath"] = null, ["line"] = null, ["column"] = null, ["symbolId"] = null });
        Text(failed).Should().Contain("POSITION_INVALID", "the failure must come from the tool, not argument binding");
        failed.IsError.Should().BeTrue();

        var succeeded = await client.CallToolAsync("find_references", new Dictionary<string, object?>
            { ["filePath"] = null, ["line"] = null, ["column"] = null, ["symbolId"] = "T:TestLib.IGreeter" });
        succeeded.IsError.Should().NotBe(true, Text(succeeded));

        static string Text(ModelContextProtocol.Protocol.CallToolResult r)
            => string.Concat(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
    }
}
