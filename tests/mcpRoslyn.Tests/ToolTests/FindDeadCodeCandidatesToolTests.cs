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
