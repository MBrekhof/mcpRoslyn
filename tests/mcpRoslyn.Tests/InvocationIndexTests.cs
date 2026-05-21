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

        routes.Should().HaveCount(2);
        routes.Should().Contain(r => r.Verb == "GET" && r.Template == "/api/health");
        routes.Should().Contain(r => r.Verb == "POST" && r.Template == "/api/echo");
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
}
