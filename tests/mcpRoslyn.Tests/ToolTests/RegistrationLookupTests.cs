using FluentAssertions;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class RegistrationLookupTests
{
    private static readonly RegistrationLookup Lookup = new(
        typeNames: ["Ns.IFoo", "Ns.Foo", "Ns.Repo<Ns.Widget>", "Ns.Worker<int>"],
        rawCalls: ["services.TryAddSingleton<Gadget>()", "services.AddCustomThing()", "services.AddScoped<ICodeGenerationService>()"]);

    [TestCase("Ns.Foo", "Foo", true, TestName = "classified registration by full name")]
    [TestCase("Ns.Repo<T>", "Repo", true, TestName = "declared generic matches a constructed registration")]
    [TestCase("Ns.Worker<T>", "Worker", true, TestName = "declared generic hosted-style name matches")]
    [TestCase("Ns.Gadget", "Gadget", true, TestName = "unclassified call naming the type")]
    [TestCase("Ns.Bar", "Bar", false, TestName = "no evidence at all")]
    [TestCase("Ns.CodeGenerationService", "CodeGenerationService", false, TestName = "a longer identifier in a raw call is not a mention")]
    [TestCase("Ns.Repo", "Repo", false, TestName = "non-generic type does not match a generic registration")]
    public void Covers_every_kind_of_registration_evidence(string displayName, string simpleName, bool expected)
        => Lookup.Covers(displayName, simpleName).Should().Be(expected);

    [Test]
    public void A_classified_call_without_type_arguments_still_counts_as_evidence()
    {
        // AddSingleton(typeof(Foo)) is classified by lifetime, but the index can't fill ServiceType/ImplType from a
        // typeof operand, so its source text is the only evidence that Foo is registered.
        var doc = Microsoft.CodeAnalysis.DocumentId.CreateNewId(Microsoft.CodeAnalysis.ProjectId.CreateNewId());
        var loc = new mcpRoslyn.Contracts.SymbolLocation("Program.cs", 1, 1, 1, 1);
        var di = new mcpRoslyn.Workspace.DiQueryResult(
            Registrations:
            [
                new mcpRoslyn.Workspace.DiEntry(null, null, "Singleton", "services.AddSingleton(typeof(Foo))", doc, loc),
                // Resolved: its identity is A.Widget, so its text must not also vouch for a same-named B.Widget.
                new mcpRoslyn.Workspace.DiEntry("A.Widget", null, "Singleton", "services.AddSingleton<Widget>()", doc, loc),
            ],
            Unclassified: []);

        var lookup = RegistrationLookup.From(di, []);

        lookup.Covers("Ns.Foo", "Foo").Should().BeTrue();
        lookup.Covers("Ns.Bar", "Bar").Should().BeFalse();
        lookup.Covers("A.Widget", "Widget").Should().BeTrue();
        lookup.Covers("B.Widget", "Widget").Should().BeFalse("a resolved registration names exactly one type");
    }
}
