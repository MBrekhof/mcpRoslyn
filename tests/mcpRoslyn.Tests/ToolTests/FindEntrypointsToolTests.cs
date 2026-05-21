using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class FindEntrypointsToolTests
{
    [Test]
    public async Task Returns_two_routes_two_middleware_two_hosted_services()
    {
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync();
        r.Result!.Routes.Should().HaveCount(2);
        r.Result.Middleware.Should().HaveCount(2);
        r.Result.HostedServices.Should().HaveCount(2);
    }

    [Test]
    public async Task IncludeAspNetRoutes_false_suppresses_routes_section()
    {
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync(includeAspNetRoutes: false);
        r.Result!.Routes.Should().BeEmpty();
        r.Result.Middleware.Should().NotBeEmpty();
    }

    [Test]
    public async Task MaxResultsPerSection_truncates_and_flags()
    {
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync(maxResultsPerSection: 1);
        r.Result!.Routes.Should().HaveCount(1);
        r.Result.Truncated.Should().Contain("routes");
    }
}
