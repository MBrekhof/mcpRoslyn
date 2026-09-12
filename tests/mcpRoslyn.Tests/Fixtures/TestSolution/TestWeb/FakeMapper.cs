using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace TestWeb;

// TOOL-009 fixture: calls named like endpoint and hosted-service registrations that are neither —
// AutoMapper's Map<T> is the real-world case. None of them may show up in find_entrypoints.
public sealed class FakeMapper
{
    public T Map<T>(object source) => default!;
    public void MapGet(string pattern, Delegate handler) { }
    public void AddHostedService<T>() { }
}

// A type name containing "ServiceCollection" is not an IServiceCollection.
public sealed class FakeServiceCollection
{
    public void AddHostedService<T>() { }
}

// A real IEndpointRouteBuilder extension with a route-like name but no handler registers nothing.
public static class FakeRouteExtensions
{
    public static T Map<T>(this IEndpointRouteBuilder builder, object source) => default!;
}

public static class FakeMapperUsage
{
    public static void Run(FakeMapper mapper, WebApplication app)
    {
        mapper.Map<string>(new object());
        mapper.MapGet("/not-a-route", () => 1);
        mapper.AddHostedService<FakeMapper>();
        new FakeServiceCollection().AddHostedService<FakeMapper>();
        app.Map<string>(new object());
    }
}
