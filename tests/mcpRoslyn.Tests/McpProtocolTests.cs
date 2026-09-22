using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
        await using var client = await StartAsync(["--solution", FixturePaths.TestSolutionPath]);

        // Parameters without a C# default are required by the SDK's binding, so every one is passed: a missing
        // argument would fail in the SDK itself and prove nothing about the filter.
        var failed = await client.CallToolAsync("find_references", new Dictionary<string, object?>
            { ["filePath"] = null, ["line"] = null, ["column"] = null, ["symbolId"] = null });
        Text(failed).Should().Contain("POSITION_INVALID", "the failure must come from the tool, not argument binding");
        failed.IsError.Should().BeTrue();

        var succeeded = await client.CallToolAsync("find_references", new Dictionary<string, object?>
            { ["filePath"] = null, ["line"] = null, ["column"] = null, ["symbolId"] = "T:TestLib.IGreeter" });
        succeeded.IsError.Should().NotBe(true, Text(succeeded));
    }

    [Test]
    public async Task An_unresolvable_sdk_pin_leaves_the_server_up_and_reload_recovers_once_fixed()
    {
        // WS-008: a global.json pinning an SDK that is not installed made the startup load throw, the host
        // stopped, and the client saw only "Connection closed". Launched from inside the pinned directory with no
        // --solution, as Claude Code launches it, so discovery and the pin are both exercised.
        var dir = Directory.CreateTempSubdirectory("mcpRoslyn-sdkpin-").FullName;
        try
        {
            WriteSolution(dir);
            File.WriteAllText(Path.Combine(dir, "global.json"),
                """{ "sdk": { "version": "9.9.999", "rollForward": "disable" } }""");
            await using var client = await StartAsync([], workingDirectory: dir);

            var overview = await CallAsync(client, "project_overview");
            overview.IsError.Should().BeTrue();
            Text(overview).Should().Contain("WORKSPACE_NOT_LOADED").And.Contain("9.9.999").And.Contain("reload_workspace");
            (await CallAsync(client, "project_overview")).IsError.Should().BeTrue("the server is still there to answer");

            var failedReload = await CallAsync(client, "reload_workspace");
            Text(failedReload).Should().Contain("WORKSPACE_NOT_LOADED", "a failed reload with nothing loaded is not an internal error");

            File.Delete(Path.Combine(dir, "global.json"));
            await AssertRecoversAsync(client);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task No_solution_at_startup_leaves_the_server_up_and_reload_discovers_one_created_later()
    {
        // WS-008: with no --solution and nothing discoverable, argument parsing threw before the host even started.
        var dir = Directory.CreateTempSubdirectory("mcpRoslyn-nosln-").FullName;
        try
        {
            await using var client = await StartAsync([], workingDirectory: dir);

            var overview = await CallAsync(client, "project_overview");
            overview.IsError.Should().BeTrue();
            Text(overview).Should().Contain("WORKSPACE_NOT_LOADED").And.Contain("No --solution provided");
            Text(await CallAsync(client, "reload_workspace")).Should().Contain("WORKSPACE_NOT_LOADED");

            WriteSolution(dir);
            await AssertRecoversAsync(client);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task A_missing_explicit_solution_leaves_the_server_up_and_names_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-missing-{Guid.NewGuid():N}.sln");
        await using var client = await StartAsync(["--solution", missing]);

        var overview = await CallAsync(client, "project_overview");
        overview.IsError.Should().BeTrue();
        Text(overview).Should().Contain("WORKSPACE_NOT_LOADED").And.Contain(Path.GetFileName(missing));
    }

    /// <summary>After the cause is fixed: a reload succeeds, then an ordinary, an indexed and a diagnostics call answer.</summary>
    private static async Task AssertRecoversAsync(McpClient client)
    {
        var reload = await CallAsync(client, "reload_workspace");
        reload.IsError.Should().NotBe(true, Text(reload));
        Text(reload).Should().Contain("Temp.sln");

        var symbol = await CallAsync(client, "workspace_symbol",
            new() { ["query"] = "Widget", ["kinds"] = null, ["maxResults"] = null });
        symbol.IsError.Should().NotBe(true, Text(symbol));
        Text(symbol).Should().Contain("Widget");

        var indexed = await CallAsync(client, "semantic_search", new() { ["pattern"] = "returns:int" });
        indexed.IsError.Should().NotBe(true, Text(indexed));
        Text(indexed).Should().Contain("Answer");

        var errors = await CallAsync(client, "get_compilation_errors",
            new() { ["severity"] = null, ["projectName"] = null });
        errors.IsError.Should().NotBe(true, Text(errors));
    }

    /// <summary>A one-project solution with no package references, so it loads without a restore.</summary>
    private static void WriteSolution(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Lib"));
        File.WriteAllText(Path.Combine(dir, "Lib", "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(dir, "Lib", "Widget.cs"), "public class Widget { public int Answer() => 42; }");
        File.WriteAllText(Path.Combine(dir, "Temp.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
            "EndProject\n");
    }

    private static async Task<McpClient> StartAsync(string[] arguments, string? workingDirectory = null)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "mcpRoslyn", "bin", configuration!, "net10.0", "win-x64", "mcpRoslyn.exe"));
        File.Exists(exe).Should().BeTrue($"the test project references the server, so building it builds {exe}");

        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mcpRoslyn",
            Command = exe,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
        }));
    }

    private static async Task<CallToolResult> CallAsync(
        McpClient client, string tool, Dictionary<string, object?>? arguments = null)
        => await client.CallToolAsync(tool, arguments ?? new Dictionary<string, object?>());

    private static string Text(CallToolResult r)
        => string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
