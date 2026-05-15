using FluentAssertions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using NUnit.Framework;

namespace mcpRoslyn.Tests.ToolTests;

[TestFixture]
public class RenameSymbolToolTests
{
    [Test]
    public async Task RenameSymbol_preview_returns_edits_without_writing()
    {
        var sut = await TestHost.CreateAsync<RenameSymbolTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        var originalContent = File.ReadAllText(englishGreeterPath);

        // EnglishGreeter.cs line 5: `    public string Greet(string name) => $"Hello, {name}!";`
        // 'G' of Greet at column 19. Rename to "Salute" with applyEdits=false (default).
        var result = await sut.InvokeAsync(
            englishGreeterPath, line: 5, column: 19,
            newName: "Salute",
            applyEdits: false,
            CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Edits.Should().NotBeEmpty();

        // File on disk MUST be unchanged
        File.ReadAllText(englishGreeterPath).Should().Be(originalContent);
    }

    [Test]
    public async Task RenameSymbol_applyEdits_writes_files()
    {
        var sut = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutchPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "DutchGreeter.cs");

        var originalContent = File.ReadAllText(dutchPath);
        try
        {
            // DutchGreeter.cs line 3: `public class DutchGreeter : IGreeter`
            // 'D' of DutchGreeter at column 14 (after "public class ")
            var result = await sut.InvokeAsync(
                dutchPath, line: 3, column: 14,
                newName: "NederlandseGreeter",
                applyEdits: true,
                CancellationToken.None);

            result.Error.Should().BeNull();
            result.Result.Should().NotBeNull();

            var newContent = File.ReadAllText(dutchPath);
            newContent.Should().Contain("class NederlandseGreeter");
            newContent.Should().NotContain("class DutchGreeter");
        }
        finally
        {
            File.WriteAllText(dutchPath, originalContent);
            File.SetLastWriteTimeUtc(dutchPath, DateTime.UtcNow.AddSeconds(1));
        }
    }
}
