using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class GetDocumentDiagnosticsToolTests
{
    [Test]
    public async Task GetDocumentDiagnostics_BrokenClass_returns_at_least_one_error()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();
        var brokenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestApp", "BrokenClass.cs");

        var result = await host.Tool.InvokeAsync(brokenPath, severity: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Diagnostics.Should().NotBeEmpty();
        result.Result.Diagnostics.Should().Contain(d => d.Severity == "Error" && d.Code.StartsWith("CS"));
    }
}
