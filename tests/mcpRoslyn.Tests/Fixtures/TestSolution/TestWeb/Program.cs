using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestWeb;

var builder = WebApplication.CreateBuilder(args);

// DI registrations: 1 singleton, 1 transient, 1 scoped, 1 hosted, 1 unclassified
builder.Services.AddSingleton<IFoo, Foo>();
builder.Services.AddTransient<IBar, Bar>();
builder.Services.AddScoped<IBaz, Baz>();
builder.Services.AddHostedService<EmailWorker>();
builder.Services.AddCustomThing();

var app = builder.Build();

// Middleware: 2 Use* calls
app.UseAuthentication();
app.UseAuthorization();

// Routes: 1 GET, 1 POST, 2 MapMethods (literal and partly computed verbs), a static-call route,
// and one with escaped, out-of-order named arguments — 6 in total.
app.MapGet("/api/health", () => "ok");
app.MapPost("/api/echo", (string body) => body);
app.MapMethods("/api/multi", new[] { "GET", "HEAD" }, () => "ok");
app.MapMethods("/api/mixed", new[] { "GET", MixedVerb() }, () => "ok");
EndpointRouteBuilderExtensions.MapGet(app, "/api/static", () => "static");
app.MapGet(@handler: () => "named", @pattern: "/api/named");

app.Run();

static string MixedVerb() => "PUT";

namespace TestWeb
{
    public static class CustomServicesExtensions
    {
        public static IServiceCollection AddCustomThing(this IServiceCollection services) => services;
    }
}
