using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public sealed class InvocationIndexDiTypeTests
{
    [Test]
    public async Task Inferred_and_typeof_registrations_carry_their_types()
    {
        // TOOL-014 review: types were read from explicit generic syntax only, so AddSingleton(options) and
        // AddScoped(typeof(IFoo), typeof(Foo)) were indexed with no type at all, and a registered type then read as
        // unregistered. They come from the bound method and its typeof operands instead.
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location)
            .Append(typeof(Microsoft.Extensions.Hosting.BackgroundService).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var project = workspace.AddProject("DiTypes", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(references);
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            namespace Ns;
            public interface IFoo { }
            public class Foo : IFoo { }
            public class Options { }
            public interface IBar { }
            public class Bar : IBar { }
            public interface IBaz { }
            public class Baz : IBaz { }
            public class Worker : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken ct)
                    => System.Threading.Tasks.Task.CompletedTask;
            }
            public static class Setup
            {
                public static void Register(IServiceCollection services, Options options)
                {
                    services.AddSingleton(options);
                    services.AddScoped(typeof(IFoo), typeof(Foo));
                    services.AddTransient(sp => new Foo());
                    services.AddSingleton(implementationType: typeof(Bar), serviceType: typeof(IBar));
                    services.AddHostedService(sp => new Worker());
                    services.AddScoped<IBar>(sp => new Bar());
                    services.AddSingleton<IFoo>(new Foo());
                    object boxed = new Baz();
                    services.AddSingleton<IBaz>((Baz)boxed);
                    services.AddTransient(typeof(IBaz), sp => new Baz());
                }
            }
            """;
        var document = project.AddDocument("Setup.cs", SourceText.From(source), filePath: Path.GetFullPath("Setup.cs"));
        var index = new InvocationIndex();

        await index.BuildAsync(document.Project.Solution);

        var registrations = index.QueryDi().Registrations;
        registrations.Should().Contain(r => r.Lifetime == "Singleton" && r.ServiceType == "Ns.Options");
        registrations.Should().Contain(r => r.Lifetime == "Scoped" && r.ServiceType == "Ns.IFoo" && r.ImplType == "Ns.Foo");
        registrations.Should().Contain(r => r.Lifetime == "Transient" && r.ServiceType == "Ns.Foo");
        registrations.Should().Contain(r => r.Lifetime == "Singleton" && r.ServiceType == "Ns.IBar" && r.ImplType == "Ns.Bar",
            "typeof operands map to the parameters they bind to, not to their position in the call");
        registrations.Should().Contain(r => r.Lifetime == "Scoped" && r.ServiceType == "Ns.IBar" && r.ImplType == "Ns.Bar",
            "a factory's implementation is the type it returns");
        registrations.Should().Contain(r => r.Lifetime == "Singleton" && r.ServiceType == "Ns.IFoo" && r.ImplType == "Ns.Foo",
            "an instance registration's implementation is the instance's static type");
        registrations.Should().Contain(r => r.Lifetime == "Singleton" && r.ServiceType == "Ns.IBaz" && r.ImplType == "Ns.Baz",
            "an explicit cast decides the implementation type; only implicit conversions are looked through");
        registrations.Should().Contain(r => r.Lifetime == "Transient" && r.ServiceType == "Ns.IBaz" && r.ImplType == "Ns.Baz",
            "non-generic overloads name the implementation in their factory argument too");
        index.QueryHostedServices().Should().Contain(h => h.Kind == "registered" && h.ServiceType == "Ns.Worker",
            "an inferred AddHostedService(factory) registers its type argument");
    }
}
