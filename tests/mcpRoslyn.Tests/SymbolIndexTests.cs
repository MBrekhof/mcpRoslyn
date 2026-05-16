using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public class SymbolIndexTests
{
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    [Test]
    public async Task SymbolIndex_property_is_available_after_load()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        sut.SymbolIndex.Should().NotBeNull();
    }

    [Test]
    public async Task QueryAttribute_returns_fixture_MyMarker_matches()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", solution);

        matches.Should().HaveCount(2);
        matches.Select(m => m.Name).Should().BeEquivalentTo("MarkedType", "MarkedMethod");
    }

    [Test]
    public async Task QueryReturnType_int_finds_partial_class_methods()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryReturnType("int", solution);

        matches.Should().HaveCountGreaterThanOrEqualTo(2);
        matches.Select(m => m.Name).Should().Contain(new[] { "Foo", "Bar" });
    }

    [Test]
    public async Task QueryParameterType_string_finds_Greet_methods()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryParameterType("string", solution);

        matches.Should().HaveCountGreaterThanOrEqualTo(3);
        matches.Select(m => m.Name).Distinct().Should().Contain("Greet");
    }
}
