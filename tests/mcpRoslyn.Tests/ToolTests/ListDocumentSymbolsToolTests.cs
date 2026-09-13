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
        await using var host = await TestHost.CreateAsync<ListDocumentSymbolsTool>();

        // The fixture is in the test output directory.
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        var result = await host.Tool.InvokeAsync(englishGreeterPath, ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Symbols.Should().HaveCountGreaterOrEqualTo(2);
        result.Result.Symbols.Should().Contain(s => s.Name == "EnglishGreeter" && s.Kind == "NamedType");
        result.Result.Symbols.Should().Contain(s => s.Name == "Greet" && s.Kind == "Method");
    }

    [Test]
    public async Task ListDocumentSymbols_includes_delegates_enum_members_and_indexers()
    {
        // TOOL-011: an indexer is not a PropertyDeclarationSyntax, a delegate not a BaseTypeDeclarationSyntax,
        // and enum members matched nothing, so all three were missing from the outline.
        await using var host = await TestHost.CreateAsync<ListDocumentSymbolsTool>();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib", "Shape.cs");
        var backup = await File.ReadAllTextAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, backup +
                "\npublic delegate void ShapeChanged(Shape shape);\n" +
                "public enum ShapeKind { Round, Square }\n" +
                "public class ShapeList { public Shape this[int index] => throw new System.NotSupportedException(); }\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            var result = await host.Tool.InvokeAsync(path, ct: CancellationToken.None);

            result.Error.Should().BeNull();
            result.Result!.Symbols.Select(s => s.Name).Should().Contain(new[] { "ShapeChanged", "Round", "Square", "this[]" });
        }
        finally
        {
            await File.WriteAllTextAsync(path, backup);
        }
    }
}
