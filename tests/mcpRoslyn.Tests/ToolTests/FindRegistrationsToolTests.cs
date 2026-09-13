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
    public async Task Query_matching_no_registration_names_the_types_it_matched()
    {
        // TOOL-014: an empty result read the same as a typo, so an agent had to grep to learn whether the
        // type existed at all. FooHelper exists in the fixture and is registered nowhere.
        await using var host = await TestHost.CreateAsync<FindRegistrationsTool>();

        var unregistered = await host.Tool.InvokeAsync(query: "FooHelper");
        unregistered.Error.Should().BeNull();
        unregistered.Result!.Registrations.Should().BeEmpty();
        unregistered.Result.UnregisteredTypes.Should().ContainSingle(t => t.EndsWith("FooHelper"));

        var qualified = await host.Tool.InvokeAsync(query: "TestWeb.FooHelper");
        qualified.Result!.UnregisteredTypes.Should().ContainSingle(t => t == "TestWeb.FooHelper",
            "a qualified query names a type the way ServiceType/ImplType do");

        // AddHostedService<EmailWorker>() is indexed as a hosted service, not a DI registration.
        var hosted = await host.Tool.InvokeAsync(query: "EmailWorker");
        hosted.Result!.UnregisteredTypes.Should().NotContain(t => t.EndsWith("EmailWorker"));

        // PollingWorker derives from BackgroundService but is registered nowhere: the hosted-service index lists
        // every such subclass, so only its registrations may exclude a type.
        var subclassOnly = await host.Tool.InvokeAsync(query: "PollingWorker");
        subclassOnly.Result!.UnregisteredTypes.Should().ContainSingle(t => t.EndsWith("PollingWorker"));

        var missing = await host.Tool.InvokeAsync(query: "NoSuchTypeAnywhere");
        missing.Result!.UnregisteredTypes.Should().NotBeNull().And.BeEmpty();

        var registered = await host.Tool.InvokeAsync(query: "IFoo");
        registered.Result!.Registrations.Should().NotBeEmpty();
        registered.Result.UnregisteredTypes.Should().BeNull("the lookup only runs when nothing matched");
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
