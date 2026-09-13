using FluentAssertions;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public sealed class GenericDefinitionNameTests
{
    // A registration names the constructed type (Worker<int>); SymbolIndex names the declaration (Worker<T>).
    // Both must reduce to one identity, or a registered generic type reads as unregistered.
    [TestCase("Ns.Plain", "Ns.Plain")]
    [TestCase("Ns.Worker<int>", "Ns.Worker`1")]
    [TestCase("Ns.Worker<T>", "Ns.Worker`1")]
    [TestCase("Ns.Worker<>", "Ns.Worker`1")]
    [TestCase("Ns.Pair<,>", "Ns.Pair`2")]
    [TestCase("Ns.Repo<Ns.Foo>", "Ns.Repo`1")]
    [TestCase("System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>", "System.Collections.Generic.Dictionary`2")]
    [TestCase("Ns.Outer<T>.Inner<U, V>", "Ns.Outer`1.Inner`2")]
    [TestCase("Ns.Box<(int, string)>", "Ns.Box`1")]
    public void Constructed_and_declared_generic_names_reduce_to_the_definition(string displayName, string expected)
        => RoslynHelpers.GenericDefinitionName(displayName).Should().Be(expected);
}
