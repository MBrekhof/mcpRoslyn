using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class FindDeadCodeCandidatesToolTests
{
    [Test]
    public async Task Detects_unused_private_method_with_high_confidence()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 100);
        r.Result.Should().NotBeNull();
        r.Result!.Candidates.Should().Contain(c =>
            c.Symbol.Contains("UnusedPrivate") && c.Confidence == "high");
    }

    [Test]
    public async Task Same_named_unreferenced_types_in_two_projects_are_both_reported()
    {
        // IDX-005: each index entry resolves to its own declaration, not to whichever project first
        // shares the id — so both Shared.Dup types (TestApp, TestWeb) are candidates.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Where(c => c.Symbol.EndsWith("Shared.Dup")).Should().HaveCount(2);
        // A linked declaration is dead on its own only if no assembly compiling it uses it, and TestWeb's copy is used.
        // Its one user, FakeMapperUsage, is itself unreferenced fixture code, so TOOL-013 reports it as a chain.
        var linked = r.Result.Candidates.Where(c => c.Symbol.EndsWith("Shared.Linked")).Should().ContainSingle().Which;
        linked.Reason.Should().Be("only-referenced-by-dead-code",
            "TestWeb's copy is used, so the declaration is not dead on its own");
        linked.KeptAliveBy.Should().ContainSingle(k => k.EndsWith("FakeMapperUsage"));
        r.Result.Candidates.Where(c => c.Symbol.EndsWith("Shared.LinkedUnused")).Should().ContainSingle(
            "an unused linked declaration is one candidate, not one per assembly and not none");
        r.Result.Candidates.Should().NotContain(c => c.Symbol.EndsWith("Shared.LinkedExtensions"),
            "TestWeb's copy declares an extension method, which makes the class framework-reached");

        // internal in TestApp, public in TestWeb (#if TESTWEB): without includePublicTypes its public
        // copy keeps it out, whichever project's copy resolves first.
        var internalsOnly = await host.Tool.InvokeAsync(maxResults: 1000);
        internalsOnly.Result!.Candidates.Should().NotContain(c => c.Symbol.EndsWith("Shared.LinkedConditional"));
    }

    [Test]
    public async Task Detects_unreferenced_internal_type_with_medium_confidence_when_InternalsVisibleTo_present()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 100);
        // TestLib has [assembly: InternalsVisibleTo("TestTests")], so internal types are medium-confidence.
        r.Result!.Candidates.Should().Contain(c =>
            c.Symbol.EndsWith("UnreferencedInternalType") && c.Confidence == "medium");
    }

    [Test]
    public async Task Skipped_counters_report_publicMembers_and_tests()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 100);

        // Exact counts would be fixture-brittle, so assert the counters are populated and that
        // what they claim to have excluded really is absent from the results — a bare non-null
        // check passed even when both counters were stuck at zero.
        r.Result!.Skipped.PublicMembers.Should().BeGreaterThan(0,
            "TestLib and TestWeb declare public types and members, which are never dead-code candidates");
        r.Result.Skipped.Tests.Should().BeGreaterThan(0,
            "the TestTests fixture project is skipped while includeTests is false");

        r.Result.Candidates.Should().NotContain(
            c => c.Accessibility == "Public" || c.Accessibility == "Protected",
            "public surface is counted into Skipped.PublicMembers instead of reported");
        r.Result.Candidates.Should().NotContain(
            c => c.Location != null && c.Location.FilePath.Contains("TestTests"),
            "test-project symbols are counted into Skipped.Tests instead of reported");
    }

    [Test]
    public async Task Skipped_counters_stay_complete_when_maxResults_truncates()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var full = await host.Tool.InvokeAsync(maxResults: 1000);
        var capped = await host.Tool.InvokeAsync(maxResults: 1);

        capped.Result!.Candidates.Should().HaveCount(1);
        capped.Result.Truncated.Should().BeTrue();
        full.Result!.Truncated.Should().BeFalse();

        // TOOL-003: the scan used to stop at maxResults, so the counters described only the
        // prefix it had walked. They now describe the whole solution either way.
        capped.Result.Skipped.PublicMembers.Should().Be(full.Result.Skipped.PublicMembers);
        capped.Result.Skipped.Tests.Should().Be(full.Result.Skipped.Tests);
        capped.Result.Skipped.Denylisted.Should().Be(full.Result.Skipped.Denylisted);
    }

    [Test]
    public async Task Unreferenced_public_type_is_reported_only_when_includePublicTypes_is_set()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();

        var off = await host.Tool.InvokeAsync(maxResults: 1000);
        off.Result!.Candidates.Should().NotContain(c => c.Symbol.Contains("FooHelper"));

        var on = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);
        on.Result!.Candidates.Should().Contain(c =>
            c.Symbol.Contains("FooHelper")
            && c.Accessibility == "Public"
            && c.Confidence == "medium");
    }

    [Test]
    public async Task Candidates_are_not_repeated_once_per_referencing_project()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        // TestLib is referenced by TestApp, TestWeb and TestTests, so its symbols land in four
        // compilations and SymbolIndex holds an entry for each. Every one used to be reported.
        r.Result!.Candidates.Select(c => $"{c.Symbol}|{c.Location?.FilePath}")
            .Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Code_referenced_only_from_dead_code_is_reported_with_the_dead_code_keeping_it()
    {
        // TOOL-013: DeadEntry is unreferenced; OnlyFromDead is called only from DeadEntry, so it is dead too, while
        // Shared is also called from the public (never-candidate) Live and must stay out.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().Contain(c => c.Symbol.Contains("DeadChain.DeadEntry") && c.Reason == "no-references");
        var chained = r.Result.Candidates.Should().ContainSingle(c => c.Symbol.Contains("DeadChain.OnlyFromDead(")).Which;
        chained.Reason.Should().Be("only-referenced-by-dead-code");
        chained.Confidence.Should().Be("medium", "one wrongly-dead root would take its whole chain with it");
        chained.KeptAliveBy.Should().ContainSingle(k => k.Contains("DeadChain.DeadEntry"));
        r.Result.Candidates.Should().NotContain(c => c.Symbol.Contains("DeadChain.Shared"));
    }

    [TestCase("TestLib.FieldOnlyType", TestName = "type named only in a dead field's declaration")]
    [TestCase("TestLib.DeadChain.OnlyFromDeadPartial", TestName = "helper called only from a dead partial method's body")]
    public async Task Dead_declarations_cover_everything_they_contain(string symbol)
    {
        // TOOL-013 review: a field's declaring syntax is just its declarator (not the type before it), and a partial
        // method's resolved symbol is its body-less definition part, so references inside either were missed.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().Contain(c => c.Symbol.StartsWith(symbol) && c.Reason == "only-referenced-by-dead-code");
    }

    [Test]
    public async Task An_unused_fields_initializer_still_keeps_what_it_calls_alive()
    {
        // TOOL-013 review: the field is dead storage, but its initializer runs whenever the type initializes.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().Contain(c => c.Symbol.Contains("DeadChain.UnusedInitialized"));
        r.Result.Candidates.Should().NotContain(c => c.Symbol.Contains("DeadChain.RunsDuringTypeInit"));
    }

    [Test]
    public async Task A_static_constructor_is_runtime_invoked_and_keeps_what_it_calls_alive()
    {
        // TOOL-013 review: an explicit static constructor has no source references, so it was reported as dead and,
        // as a chain root, took every helper it calls with it.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().NotContain(c => c.Kind == "Method" && c.Symbol.Contains("DeadChain.DeadChain("));
        r.Result.Candidates.Should().NotContain(c => c.Symbol.Contains("DeadChain.RunsInStaticConstructor"));
    }

    [Test]
    public async Task Registration_whose_only_consumers_are_dead_is_listed_as_evidence()
    {
        // TOOL-013: IBaz is registered and its only constructor consumer, OrphanConsumer, is unreferenced. That is
        // evidence the registration is dead, not a verdict, so it is listed apart from the candidates.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().Contain(c => c.Symbol.EndsWith("OrphanConsumer"));
        var evidence = r.Result.RegistrationsWithOnlyDeadConsumers.Should().ContainSingle(e => e.ServiceType.EndsWith("IBaz")).Which;
        evidence.Consumers.Should().ContainSingle(c => c.EndsWith("OrphanConsumer"));
        r.Result.RegistrationsWithOnlyDeadConsumers.Should().NotContain(e => e.ServiceType.EndsWith("IFoo"),
            "BarController consumes IFoo and is framework-reached");
        r.Result.RegistrationsWithOnlyDeadConsumers.Should().NotContain(e => e.ServiceType.EndsWith("IBar"),
            "no observed consumer is not the same as only dead ones");
    }

    [Test]
    public async Task Chains_are_complete_before_maxResults_caps_the_output()
    {
        // TOOL-013 design: an unscanned symbol must stay unknown, never dead, so analysis runs to the fixed point
        // and only then is the output capped.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var full = await host.Tool.InvokeAsync(maxResults: 1000);
        var capped = await host.Tool.InvokeAsync(maxResults: full.Result!.Candidates.Count - 1);

        capped.Result!.Truncated.Should().BeTrue();
        // Compared by identity, not record equality: KeptAliveBy is a fresh array on every call.
        static string Key(DeadCodeCandidate c) => $"{c.Symbol}|{c.Location?.FilePath}|{c.Reason}";
        capped.Result.Candidates.Select(Key).Should().Equal(full.Result.Candidates.Take(full.Result.Candidates.Count - 1).Select(Key),
            "a capped result is a prefix of the full answer, never a different answer");
    }

    [Test]
    public async Task Unregistered_BackgroundService_subclass_is_a_candidate_and_a_registered_one_is_not()
    {
        // The hosted-service index lists every BackgroundService subclass, registered or not, so only its
        // "registered" entries may count as a registration. PollingWorker is registered nowhere; EmailWorker
        // is AddHostedService<EmailWorker>().
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().Contain(c => c.Symbol.EndsWith("PollingWorker"));
        r.Result.Candidates.Should().NotContain(c => c.Symbol.EndsWith("EmailWorker"));
    }

    [Test]
    public async Task Program_entry_point_is_never_a_candidate()
    {
        // TOOL-012: TestApp and TestWeb use top-level statements. The compiler-synthesized entry point
        // is private and unreferenced by definition, and was reported as dead at high confidence.
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        r.Error.Should().BeNull();
        r.Result!.Candidates.Should().NotContain(c => c.Symbol.Contains("top-level-statements-entry-point"));
    }

    [Test]
    public async Task Framework_reached_public_types_are_suppressed_from_the_public_sweep()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(includePublicTypes: true, maxResults: 1000);

        // Neither is referenced by name anywhere, so both would be reported without the guard:
        // BarController is reached by MVC's reflection scan, CustomServicesExtensions through
        // the extension method it declares (`builder.Services.AddCustomThing()`).
        r.Result!.Candidates.Should().NotContain(c => c.Symbol.EndsWith("BarController"));
        r.Result.Candidates.Should().NotContain(c => c.Symbol.EndsWith("CustomServicesExtensions"));
        r.Result.Skipped.FrameworkReached.Should().BeGreaterThan(0);
    }
}
