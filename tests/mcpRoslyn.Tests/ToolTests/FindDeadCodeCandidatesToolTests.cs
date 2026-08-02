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
}
