using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

/// <summary>
/// Verifies the cross-cutting format="structured" | "summary" parameter on every tool.
/// Each tool gets one "summary returns non-empty string" test and one "structured keeps
/// Result populated and Summary null" test. This exercises the mechanism without
/// duplicating the full functional coverage in the ToolTests folder.
/// </summary>
[TestFixture]
public sealed class FormatParameterTests
{
    // ── Navigation tools ────────────────────────────────────────────────────────

    [Test]
    public async Task FindReferences_summary_returns_non_empty_string()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:TestLib.IGreeter",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().NotBeNullOrEmpty();
        r.Summary.Should().MatchRegex(@"\d+ references in \d+ files");
    }

    [Test]
    public async Task FindReferences_structured_leaves_Summary_null()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:TestLib.IGreeter",
            format: "structured");
        r.Summary.Should().BeNull();
        r.Result.Should().NotBeNull();
    }

    [Test]
    public async Task GotoDefinition_summary_returns_definition_location_string()
    {
        await using var host = await TestHost.CreateAsync<GotoDefinitionTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        // col 31 lands on IGreeter in the base list
        var r = await host.Tool.InvokeAsync(englishGreeterPath, line: 3, column: 31, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().NotBeNullOrEmpty();
        r.Summary.Should().Contain("definition at");
    }

    [Test]
    public async Task GotoDefinition_structured_leaves_Summary_null()
    {
        await using var host = await TestHost.CreateAsync<GotoDefinitionTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var r = await host.Tool.InvokeAsync(englishGreeterPath, line: 3, column: 31, format: "structured");
        r.Summary.Should().BeNull();
        r.Result.Should().NotBeNull();
    }

    [Test]
    public async Task Hover_summary_returns_signature_or_doc()
    {
        await using var host = await TestHost.CreateAsync<HoverTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var r = await host.Tool.InvokeAsync(englishGreeterPath, line: 5, column: 19, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Hover_structured_leaves_Summary_null()
    {
        await using var host = await TestHost.CreateAsync<HoverTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var r = await host.Tool.InvokeAsync(englishGreeterPath, line: 5, column: 19, format: "structured");
        r.Summary.Should().BeNull();
        r.Result.Should().NotBeNull();
    }

    // ── Search tools ────────────────────────────────────────────────────────────

    [Test]
    public async Task WorkspaceSymbol_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<WorkspaceSymbolTool>();
        var r = await host.Tool.InvokeAsync("Greeter", null, null, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ matching symbols");
    }

    [Test]
    public async Task WorkspaceSymbol_structured_leaves_Summary_null()
    {
        await using var host = await TestHost.CreateAsync<WorkspaceSymbolTool>();
        var r = await host.Tool.InvokeAsync("Greeter", null, null, format: "structured");
        r.Summary.Should().BeNull();
        r.Result.Should().NotBeNull();
    }

    [Test]
    public async Task SemanticSearch_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var r = await host.Tool.InvokeAsync("implements:TestLib.IGreeter", format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ matches");
    }

    [Test]
    public async Task SemanticSearch_structured_leaves_Summary_null()
    {
        await using var host = await TestHost.CreateAsync<SemanticSearchTool>();
        var r = await host.Tool.InvokeAsync("implements:TestLib.IGreeter", format: "structured");
        r.Summary.Should().BeNull();
        r.Result.Should().NotBeNull();
    }

    // ── Structure tools ─────────────────────────────────────────────────────────

    [Test]
    public async Task FindImplementations_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<FindImplementationsTool>();
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:TestLib.IGreeter",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ implementations");
    }

    [Test]
    public async Task FindDerivedTypes_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<FindDerivedTypesTool>();
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:TestLib.Shape",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ derived types");
    }

    [Test]
    public async Task ListDocumentSymbols_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<ListDocumentSymbolsTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var r = await host.Tool.InvokeAsync(englishGreeterPath, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ symbols in document");
    }

    // ── Call graph tools ─────────────────────────────────────────────────────────

    [Test]
    public async Task FindCallers_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<FindCallersTool>();
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ caller sites");
    }

    [Test]
    public async Task FindCallees_summary_returns_count_string()
    {
        await using var host = await TestHost.CreateAsync<FindCalleesTool>();
        var r = await host.Tool.InvokeAsync(
            symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ callees");
    }

    // ── Diagnostics tools ────────────────────────────────────────────────────────

    [Test]
    public async Task GetCompilationErrors_summary_returns_error_and_warning_count()
    {
        await using var host = await TestHost.CreateAsync<GetCompilationErrorsTool>();
        var r = await host.Tool.InvokeAsync(severity: null, projectName: null, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ errors, \d+ warnings");
    }

    [Test]
    public async Task GetDocumentDiagnostics_summary_returns_error_and_warning_count()
    {
        await using var host = await TestHost.CreateAsync<GetDocumentDiagnosticsTool>();
        var brokenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestApp", "BrokenClass.cs");
        var r = await host.Tool.InvokeAsync(brokenPath, severity: null, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ errors, \d+ warnings");
    }

    // ── Editing tools ────────────────────────────────────────────────────────────

    [Test]
    public async Task RenameSymbol_summary_returns_preview_description()
    {
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");
        var originalContent = File.ReadAllText(englishGreeterPath);
        try
        {
            var r = await host.Tool.InvokeAsync(
                englishGreeterPath, line: 5, column: 19,
                newName: "Salute",
                applyEdits: false,
                format: "summary");
            r.Error.Should().BeNull();
            r.Result.Should().BeNull();
            r.Summary.Should().NotBeNullOrEmpty();
            r.Summary.Should().Contain("preview:");
        }
        finally
        {
            // Guard: preview should not write, but restore just in case
            File.WriteAllText(englishGreeterPath, originalContent);
        }
    }

    // ── Lifecycle tools ──────────────────────────────────────────────────────────

    [Test]
    public async Task ReloadWorkspace_summary_returns_project_count_string()
    {
        await using var host = await TestHost.CreateAsync<ReloadWorkspaceTool>();
        var r = await host.Tool.InvokeAsync(format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"reloaded \d+ projects");
    }

    // ── New v1.3 tools ───────────────────────────────────────────────────────────

    [Test]
    public async Task ProjectOverview_summary_returns_project_count_string()
    {
        await using var host = await TestHost.CreateAsync<ProjectOverviewTool>();
        var r = await host.Tool.InvokeAsync(format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ projects");
    }

    [Test]
    public async Task FindEntrypoints_summary_returns_section_counts()
    {
        await using var host = await TestHost.CreateAsync<FindEntrypointsTool>();
        var r = await host.Tool.InvokeAsync(format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ routes, \d+ middleware, \d+ hosted services");
    }

    [Test]
    public async Task FindRegistrations_summary_returns_registration_count()
    {
        await using var host = await TestHost.CreateAsync<FindRegistrationsTool>();
        var r = await host.Tool.InvokeAsync(format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ DI registrations");
    }

    [Test]
    public async Task AnalyzeSymbol_summary_returns_composite_counts()
    {
        await using var host = await TestHost.CreateAsync<AnalyzeSymbolTool>();
        var r = await host.Tool.InvokeAsync(symbolId: "T:TestLib.IGreeter", format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().NotBeNullOrEmpty();
        r.Summary.Should().Contain("IGreeter");
        r.Summary.Should().Contain("refs=");
    }

    [Test]
    public async Task TestMap_summary_returns_candidate_count_string()
    {
        await using var host = await TestHost.CreateAsync<TestMapTool>();
        var r = await host.Tool.InvokeAsync(
            symbolId: "M:TestLib.EnglishGreeter.Greet(System.String)",
            format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ candidate tests");
    }

    [Test]
    public async Task FindDeadCodeCandidates_summary_returns_candidate_count_string()
    {
        await using var host = await TestHost.CreateAsync<FindDeadCodeCandidatesTool>();
        var r = await host.Tool.InvokeAsync(maxResults: 50, format: "summary");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().MatchRegex(@"\d+ candidates \(\d+ high, \d+ medium\)");
    }

    // ── Cross-cutting: case-insensitive format value ──────────────────────────────

    [Test]
    public async Task Format_value_is_case_insensitive()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        // "SUMMARY" should behave the same as "summary"
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:TestLib.IGreeter",
            format: "SUMMARY");
        r.Error.Should().BeNull();
        r.Result.Should().BeNull();
        r.Summary.Should().NotBeNullOrEmpty();
    }

    // ── Cross-cutting: errors are always structured regardless of format ──────────

    [Test]
    public async Task Error_result_has_null_Summary_even_in_summary_mode()
    {
        await using var host = await TestHost.CreateAsync<FindReferencesTool>();
        // symbolId that doesn't exist — should return an error
        var r = await host.Tool.InvokeAsync(
            filePath: null, line: null, column: null,
            symbolId: "T:NonExistent.FakeType",
            format: "summary");
        r.Error.Should().NotBeNull("an unknown symbol ID should produce an error");
        r.Summary.Should().BeNull("errors are always structured, never summarised");
        r.Result.Should().BeNull();
    }
}
