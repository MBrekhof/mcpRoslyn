using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class ListDocumentSymbolsToolTests
{
    [Test]
    public async Task ListDocumentSymbols_returns_class_and_method_for_EnglishGreeter()
    {
        var sut = await TestHost.CreateAsync<ListDocumentSymbolsTool>();

        // The fixture is in the test output directory.
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        var result = await sut.InvokeAsync(englishGreeterPath, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Symbols.Should().HaveCountGreaterOrEqualTo(2);
        result.Result.Symbols.Should().Contain(s => s.Name == "EnglishGreeter" && s.Kind == "NamedType");
        result.Result.Symbols.Should().Contain(s => s.Name == "Greet" && s.Kind == "Method");
    }
}
