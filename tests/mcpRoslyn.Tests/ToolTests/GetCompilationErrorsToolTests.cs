using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GetCompilationErrorsToolTests
{
    [Test]
    public async Task GetCompilationErrors_returns_at_least_one_error()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await host.Tool.InvokeAsync(severity: null, projectName: null, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().Contain(d => d.Severity == "Error");
    }

    [Test]
    public async Task GetCompilationErrors_filtered_to_TestLib_returns_no_errors()
    {
        await using var host2 = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var result = await host2.Tool.InvokeAsync(severity: "Error", projectName: "TestLib", ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task ExcludeDiagnosticCodes_filters_specified_codes()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        // BrokenClass.cs produces CS1525 — assert it appears WITHOUT the filter
        var baseline = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "All",
            ct: CancellationToken.None);
        baseline.Result!.Diagnostics.Should().Contain(d => d.Code == "CS1525");

        var filtered = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "All",
            excludeDiagnosticCodes: new[] { "CS1525" },
            ct: CancellationToken.None);
        filtered.Result!.Diagnostics.Should().NotContain(d => d.Code == "CS1525");
    }

    [Test]
    public async Task MinimumSeverity_Error_drops_warnings()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            minimumSeverity: "Error",
            ct: CancellationToken.None);
        r.Result!.Diagnostics.Should().OnlyContain(d => d.Severity == "Error");
    }

    [Test]
    public async Task IncludeGenerated_false_excludes_g_cs_paths()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(
            severity: null, projectName: null,
            includeGenerated: false,
            minimumSeverity: "All",
            ct: CancellationToken.None);
        r.Result!.Diagnostics.Should().NotContain(
            d => d.Location != null && d.Location.FilePath.EndsWith(".g.cs"));
    }
}
