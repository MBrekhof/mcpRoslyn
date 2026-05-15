using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;
using NUnit.Framework;
using System.Diagnostics;

namespace mcpRoslyn.Tests;

[TestFixture]
[Explicit("Manual acceptance — runs against the real duetGPT solution, not portable.")]
[Category("Manual")]
public class AcceptanceTests
{
    private const string DuetGptSolutionPath = @"c:\projects\duetgpt\duetGPT\duetGPT.sln";

    private static async Task<T> CreateAsync<T>(WorkspaceService workspace) where T : class
    {
        var options = new McpRoslynOptions { SolutionPath = DuetGptSolutionPath };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IWorkspaceService>(workspace);
        services.AddSingleton<T>();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<T>();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    [Test]
    public async Task Acceptance_against_duetGPT()
    {
        if (!File.Exists(DuetGptSolutionPath))
        {
            Assert.Ignore($"duetGPT.sln not found at {DuetGptSolutionPath}");
            return;
        }

        var options = new McpRoslynOptions { SolutionPath = DuetGptSolutionPath };
        var workspace = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        var coldStartSw = Stopwatch.StartNew();
        await workspace.LoadAsync();
        coldStartSw.Stop();

        var loadedProjects = workspace.LoadedProjectCount;
        TestContext.WriteLine($"Cold start: {coldStartSw.ElapsedMilliseconds} ms, {loadedProjects} projects loaded.");

        // 1) find_references on IGroupPermissionResolver
        var findRefs = await CreateAsync<FindReferencesTool>(workspace);
        var refsSw = Stopwatch.StartNew();
        var refsResult = await findRefs.InvokeAsync(filePath: null, line: null, column: null,
            symbolId: "T:duetGPT.Services.IGroupPermissionResolver",
            CancellationToken.None);
        refsSw.Stop();
        TestContext.WriteLine($"find_references(IGroupPermissionResolver): {refsSw.ElapsedMilliseconds} ms, " +
            $"error={refsResult.Error?.Code ?? "null"}, count={refsResult.Result?.References.Count ?? 0}");

        // 2) find_implementations on IBuiltInToolProvider
        var findImpls = await CreateAsync<FindImplementationsTool>(workspace);
        var implsSw = Stopwatch.StartNew();
        var implsResult = await findImpls.InvokeAsync(filePath: null, line: null, column: null,
            symbolId: "T:duetGPT.Services.IBuiltInToolProvider",
            CancellationToken.None);
        implsSw.Stop();
        TestContext.WriteLine($"find_implementations(IBuiltInToolProvider): {implsSw.ElapsedMilliseconds} ms, " +
            $"error={implsResult.Error?.Code ?? "null"}, count={implsResult.Result?.Implementations.Count ?? 0}");

        // 3) find_callers on KnowledgeService.ExpandQueryAsync
        var findCallers = await CreateAsync<FindCallersTool>(workspace);
        var callersSw = Stopwatch.StartNew();
        var callersResult = await findCallers.InvokeAsync(filePath: null, line: null, column: null,
            symbolId: "M:duetGPT.Services.KnowledgeService.ExpandQueryAsync(System.String,System.Threading.CancellationToken)",
            transitive: false,
            CancellationToken.None);
        callersSw.Stop();
        TestContext.WriteLine($"find_callers(KnowledgeService.ExpandQueryAsync): {callersSw.ElapsedMilliseconds} ms, " +
            $"error={callersResult.Error?.Code ?? "null"}, count={callersResult.Result?.Callers.Count ?? 0}");

        // 4) semantic_search has-attribute:McpServerToolType (should be 0 — duetGPT doesn't use this attribute)
        var search = await CreateAsync<SemanticSearchTool>(workspace);
        var searchSw = Stopwatch.StartNew();
        var searchResult = await search.InvokeAsync(
            "has-attribute:ModelContextProtocol.Server.McpServerToolTypeAttribute",
            CancellationToken.None);
        searchSw.Stop();
        TestContext.WriteLine($"semantic_search(has-attribute:McpServerToolType): {searchSw.ElapsedMilliseconds} ms, " +
            $"error={searchResult.Error?.Code ?? "null"}, count={searchResult.Result?.Matches.Count ?? 0}");

        // Soft assertions — log everything, don't hard-fail since duetGPT may have evolved.
        loadedProjects.Should().BeGreaterThan(0, "duetGPT.sln must load at least one project");
    }
}
