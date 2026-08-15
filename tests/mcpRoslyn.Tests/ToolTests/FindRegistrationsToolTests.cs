using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class FindRegistrationsToolTests
{
    [Test]
    public async Task Returns_three_classified_registrations_and_one_unclassified()
    {
        await using var host = await TestHost.CreateAsync<FindRegistrationsTool>();
        var r = await host.Tool.InvokeAsync();
        r.Result!.Registrations.Should().HaveCount(3);
        r.Result.Registrations.Select(x => x.Lifetime)
            .Should().BeEquivalentTo(new[] { "Singleton", "Transient", "Scoped" });
        r.Result.Unclassified.Should().HaveCount(1);
        r.Result.Unclassified[0].RawCall.Should().Contain("AddCustomThing");
    }

    [Test]
    public async Task IFoo_registration_lists_BarController_as_likely_consumer()
    {
        await using var host = await TestHost.CreateAsync<FindRegistrationsTool>();
        var r = await host.Tool.InvokeAsync(includeConsumers: true);
        var foo = r.Result!.Registrations.Single(x => x.ServiceType is not null && x.ServiceType.EndsWith("IFoo"));
        foo.LikelyConsumers.Should().Contain(c => c.Type.EndsWith("BarController"));
    }

    [Test]
    public async Task Non_constructor_method_taking_the_service_type_is_not_a_consumer()
    {
        await using var host = await TestHost.CreateAsync<FindRegistrationsTool>();
        var r = await host.Tool.InvokeAsync(includeConsumers: true);
        var foo = r.Result!.Registrations.Single(x => x.ServiceType is not null && x.ServiceType.EndsWith("IFoo"));

        // FooHelper.Use(IFoo) is a plain method, not injection. Before TOOL-002 any method with a
        // parameter of the service type qualified, which made helpers look like DI consumers.
        foo.LikelyConsumers.Should().NotContain(c => c.Type.EndsWith("FooHelper"));
        foo.LikelyConsumers.Should().Contain(c => c.Type.EndsWith("BarController"));
    }
}
