using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public sealed class InvocationIndexTests
{
    [Test]
    public async Task Build_indexes_aspnet_routes()
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var index = host.Workspace.InvocationIndex;
        var routes = index.QueryRoutes();

        routes.Should().HaveCount(6);
        routes.Should().Contain(r => r.Verb == "GET" && r.Template == "/api/health");
        routes.Should().Contain(r => r.Verb == "POST" && r.Template == "/api/echo");
        routes.Should().Contain(r => r.Verb == "GET,HEAD" && r.Template == "/api/multi");
    }

    [Test]
    public async Task Build_indexes_middleware()
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var middleware = host.Workspace.InvocationIndex.QueryMiddleware();

        middleware.Should().HaveCount(2);
        middleware.Select(m => m.Method).Should().Contain(new[] { "UseAuthentication", "UseAuthorization" });
    }

    [Test]
    public async Task Build_indexes_hosted_services_registered_and_subclassed()
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var hosted = host.Workspace.InvocationIndex.QueryHostedServices();

        hosted.Should().Contain(h => h.Kind == "registered" && h.ServiceType!.EndsWith("EmailWorker"));
        hosted.Should().Contain(h => h.Kind == "subclass" && h.Type!.EndsWith("PollingWorker"));
    }

    [Test]
    public async Task Build_indexes_di_registrations_with_lifetime()
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var di = host.Workspace.InvocationIndex.QueryDi();

        di.Registrations.Should().Contain(r => r.Lifetime == "Singleton" && r.ImplType!.EndsWith("Foo"));
        di.Registrations.Should().Contain(r => r.Lifetime == "Transient" && r.ImplType!.EndsWith("Bar"));
        di.Registrations.Should().Contain(r => r.Lifetime == "Scoped"    && r.ImplType!.EndsWith("Baz"));
        di.Unclassified.Should().Contain(u => u.RawCall.Contains("AddCustomThing"));
    }

    [Test]
    public async Task Subclass_entry_survives_a_refresh_of_its_own_file_and_is_indexed_once()
    {
        // IDX-003: a dirty re-walk removed every hosted-service entry for the document, but only
        // the full build could find subclasses — touching the file made the worker vanish.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var sol = await host.Workspace.GetFreshSolutionAsync();
        var workerPath = sol.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "PollingWorker.cs")
            .FilePath!;

        host.Workspace.InvocationIndex.QueryHostedServices()
            .Where(h => h.Kind == "subclass" && h.Type!.EndsWith("PollingWorker"))
            .Should().ContainSingle("the build records each subclass once");

        var original = await File.ReadAllTextAsync(workerPath);
        try
        {
            await File.WriteAllTextAsync(workerPath, original + "\n// touched\n");
            File.SetLastWriteTimeUtc(workerPath, DateTime.UtcNow.AddSeconds(1));
            await host.Workspace.GetFreshSolutionAsync();

            host.Workspace.InvocationIndex.QueryHostedServices()
                .Where(h => h.Kind == "subclass" && h.Type!.EndsWith("PollingWorker"))
                .Should().ContainSingle("a refresh re-finds the subclass in the changed file");
        }
        finally
        {
            await File.WriteAllTextAsync(workerPath, original);
        }
    }

    // Whichever part Roslyn lists first, the other one's case fails under a first-declaration-only
    // rule; the both-parts case produces two raw entries and fails without de-duplication.
    [TestCase("A")]
    [TestCase("B")]
    [TestCase("A", "B")]
    public async Task Base_list_added_to_any_partial_part_is_found_once(params string[] partsWithBase)
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var sol = await host.Workspace.GetFreshSolutionAsync();
        var paths = partsWithBase.ToDictionary(
            part => part,
            part => sol.Projects.SelectMany(p => p.Documents).First(d => d.Name == $"SplitWorker.{part}.cs").FilePath!);

        host.Workspace.InvocationIndex.QueryHostedServices()
            .Should().NotContain(h => h.Type != null && h.Type.EndsWith("SplitWorker"));

        var originals = paths.ToDictionary(p => p.Key, p => File.ReadAllText(p.Value));
        try
        {
            foreach (var part in partsWithBase)
            {
                // The override may be declared once, so only the first listed part carries it.
                var body = part == partsWithBase[0]
                    ? "    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;\n"
                    : "";
                await File.WriteAllTextAsync(paths[part],
                    "using Microsoft.Extensions.Hosting;\n\nnamespace TestWeb;\n\n" +
                    "public partial class SplitWorker : BackgroundService\n{\n" + body + "}\n");
                File.SetLastWriteTimeUtc(paths[part], DateTime.UtcNow.AddSeconds(1));
            }
            await host.Workspace.GetFreshSolutionAsync();

            host.Workspace.InvocationIndex.QueryHostedServices()
                .Where(h => h.Kind == "subclass" && h.Type!.EndsWith("SplitWorker"))
                .Should().ContainSingle();
        }
        finally
        {
            foreach (var (part, text) in originals) await File.WriteAllTextAsync(paths[part], text);
        }
    }

    [Test]
    public async Task MarkDirty_then_query_walks_fresh_document()
    {
        await using var host = await TestHost.CreateWorkspaceAsync();
        var sol = await host.Workspace.GetFreshSolutionAsync();
        var programPath = sol.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Program.cs" && d.Project.Name == "TestWeb")
            .FilePath!;

        var original = await File.ReadAllTextAsync(programPath);
        try
        {
            await File.WriteAllTextAsync(programPath,
                original.Replace("app.MapPost(\"/api/echo\"", "app.MapPost(\"/api/echo-renamed\""));
            // Touch the mtime so GetFreshSolutionAsync sees the file as changed
            File.SetLastWriteTimeUtc(programPath, DateTime.UtcNow.AddSeconds(1));
            // refresh updates mtime and marks the document dirty
            await host.Workspace.GetFreshSolutionAsync();

            var routes = host.Workspace.InvocationIndex.QueryRoutes();
            routes.Should().Contain(r => r.Template == "/api/echo-renamed");
            routes.Should().NotContain(r => r.Template == "/api/echo");
        }
        finally
        {
            await File.WriteAllTextAsync(programPath, original);
        }
    }

    [Test]
    public async Task Parallel_queries_during_a_refresh_never_see_a_documents_entries_missing()
    {
        // IDX-004: the refresh removed a document's entries and cleared its marker before re-binding it,
        // so a query arriving in that window saw neither the old routes nor the new ones.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var sol = await host.Workspace.GetFreshSolutionAsync();
        var programPath = sol.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Program.cs" && d.Project.Name == "TestWeb")
            .FilePath!;

        var original = await File.ReadAllTextAsync(programPath);
        try
        {
            await File.WriteAllTextAsync(programPath, original + "\n// touched\n");
            File.SetLastWriteTimeUtc(programPath, DateTime.UtcNow.AddSeconds(1));
            await host.Workspace.GetFreshSolutionAsync();

            var counts = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => host.Workspace.InvocationIndex.QueryRoutes().Count)));
            counts.Should().AllBeEquivalentTo(6);
        }
        finally
        {
            await File.WriteAllTextAsync(programPath, original);
        }
    }

    [Test]
    public async Task A_file_restored_with_an_older_timestamp_is_still_a_change()
    {
        // IDX-004: freshness was cachedMtime >= diskMtime, so a copy or unpack preserving an older time
        // read as unchanged.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var sol = await host.Workspace.GetFreshSolutionAsync();
        var programPath = sol.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Program.cs" && d.Project.Name == "TestWeb")
            .FilePath!;

        var original = await File.ReadAllTextAsync(programPath);
        var originalTime = File.GetLastWriteTimeUtc(programPath);
        try
        {
            await File.WriteAllTextAsync(programPath,
                original.Replace("app.MapPost(\"/api/echo\"", "app.MapPost(\"/api/echo-restored\""));
            File.SetLastWriteTimeUtc(programPath, originalTime.AddDays(-1));
            await host.Workspace.GetFreshSolutionAsync();

            host.Workspace.InvocationIndex.QueryRoutes().Should().Contain(r => r.Template == "/api/echo-restored");
        }
        finally
        {
            await File.WriteAllTextAsync(programPath, original);
        }
    }
}
