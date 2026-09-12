using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class FindEntrypointsToolTests
{
    [Test]
    public async Task Returns_six_routes_two_middleware_two_hosted_services()
    {
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync();
        r.Result!.Routes.Should().HaveCount(6);
        r.Result.Middleware.Should().HaveCount(2);
        r.Result.HostedServices.Should().HaveCount(2);
    }

    [Test]
    public async Task MapMethods_reports_its_verbs_and_handler_and_lookalike_calls_are_not_entrypoints()
    {
        // TOOL-009: routes were matched by method name alone, so AutoMapper-style Map<T>(x) calls
        // became ANY routes, and MapMethods took its method array for the handler.
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync();

        var multi = r.Result!.Routes.Should().ContainSingle(x => x.Template == "/api/multi").Subject;
        multi.Verb.Should().Be("GET,HEAD");
        multi.Handler.Should().Be("() => \"ok\"");

        r.Result.Routes.Should().ContainSingle(x => x.Template == "/api/mixed").Which.Verb
            .Should().Be("ANY", "one of its methods is computed, so the literal part is not the whole answer");

        var viaStaticCall = r.Result.Routes.Should().ContainSingle(x => x.Template == "/api/static").Subject;
        viaStaticCall.Verb.Should().Be("GET");
        viaStaticCall.Handler.Should().Be("() => \"static\"");

        r.Result.Routes.Should().ContainSingle(x => x.Template == "/api/named").Which.Handler
            .Should().Be("() => \"named\"", "escaped, out-of-order named arguments bind by parameter name");

        r.Result.Routes.Should().OnlyContain(x => x.Location.FilePath.EndsWith("Program.cs"),
            "FakeMapper.cs holds only look-alikes: a mapper's Map<T>/MapGet and a handler-less IEndpointRouteBuilder.Map<T>");
        r.Result.HostedServices.Should().NotContain(h => h.ServiceType != null && h.ServiceType.EndsWith("FakeMapper"),
            "neither FakeMapper nor FakeServiceCollection is an IServiceCollection");
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
