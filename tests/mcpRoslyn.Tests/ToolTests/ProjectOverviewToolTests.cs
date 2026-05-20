using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class ProjectOverviewToolTests
{
    [Test]
    public async Task Returns_all_four_fixture_projects()
    {
        var sut = await TestHost.CreateAsync<ProjectOverviewTool>();
        var result = await sut.InvokeAsync();

        result.Result.Should().NotBeNull();
        result.Result!.Projects.Should().HaveCount(4);
        result.Result.Projects.Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "TestLib", "TestApp", "TestWeb", "TestTests" });
    }

    [Test]
    public async Task TestWeb_lists_its_project_reference_to_TestLib()
    {
        var sut = await TestHost.CreateAsync<ProjectOverviewTool>();
        var result = await sut.InvokeAsync();
        var web = result.Result!.Projects.Single(p => p.Name == "TestWeb");
        web.ProjectReferences.Should().Contain("TestLib");
    }

    [Test]
    public async Task TestTests_lists_xunit_abstractions_package()
    {
        var sut = await TestHost.CreateAsync<ProjectOverviewTool>();
        var result = await sut.InvokeAsync();
        var tests = result.Result!.Projects.Single(p => p.Name == "TestTests");
        tests.PackageReferences.Should().Contain(p => p.Name.Contains("xunit.abstractions"));
    }
}
