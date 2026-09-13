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
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var englishGreeterPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "EnglishGreeter.cs");

        var originalContent = File.ReadAllText(englishGreeterPath);

        // EnglishGreeter.cs line 5: `    public string Greet(string name) => $"Hello, {name}!";`
        // 'G' of Greet at column 19. Rename to "Salute" with applyEdits=false (default).
        var result = await host.Tool.InvokeAsync(
            englishGreeterPath, line: 5, column: 19,
            newName: "Salute",
            applyEdits: false,
            ct: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result.Should().NotBeNull();
        result.Result!.Edits.Should().NotBeEmpty();
        // PERF-002: each edit is the renamed occurrence, not the whole file before and after.
        result.Result.Edits.Should().OnlyContain(e => e.OldText == "Greet" && e.NewText == "Salute");

        // File on disk MUST be unchanged
        File.ReadAllText(englishGreeterPath).Should().Be(originalContent);
    }

    [Test]
    public async Task RenameSymbol_applyEdits_writes_files()
    {
        await using var host2 = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutchPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "TestSolution", "TestLib", "DutchGreeter.cs");

        var originalContent = File.ReadAllText(dutchPath);
        try
        {
            // DutchGreeter.cs line 3: `public class DutchGreeter : IGreeter`
            // 'D' of DutchGreeter at column 14 (after "public class ")
            var result = await host2.Tool.InvokeAsync(
                dutchPath, line: 3, column: 14,
                newName: "NederlandseGreeter",
                applyEdits: true,
                ct: CancellationToken.None);

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

    private static string FixturePath(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "Fixtures", "TestSolution", .. parts]);

    [Test]
    public async Task ApplyEdits_refuses_a_file_changed_since_the_snapshot_and_writes_nothing()
    {
        // TOOL-008: the whole file was written from the snapshot, silently discarding any edit made
        // after it. An edit that keeps its old timestamp (a restore preserving times) is exactly one
        // the mtime refresh cannot see, so the rename is computed from stale text.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutch = FixturePath("TestLib", "DutchGreeter.cs");
        var original = File.ReadAllText(dutch);
        var originalMtime = File.GetLastWriteTimeUtc(dutch);
        try
        {
            var edited = original + "\n// someone else's edit\n";
            File.WriteAllText(dutch, edited);
            File.SetLastWriteTimeUtc(dutch, originalMtime);

            var result = await host.Tool.InvokeAsync(dutch, line: 3, column: 14, newName: "NederlandseGreeter",
                applyEdits: true, ct: CancellationToken.None);

            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("STALE_FILE");
            File.ReadAllText(dutch).Should().Be(edited, "the newer edit must survive");
        }
        finally
        {
            File.WriteAllText(dutch, original);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(1));
        }
    }

    [Test]
    public async Task ApplyEdits_with_a_read_only_target_writes_no_file_at_all()
    {
        // TOOL-008: renaming EnglishGreeter touches TestLib/EnglishGreeter.cs and TestApp/Program.cs.
        // With Program.cs read-only, writing EnglishGreeter.cs first used to leave a half-applied rename.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var english = FixturePath("TestLib", "EnglishGreeter.cs");
        var program = FixturePath("TestApp", "Program.cs");
        // Every file the rename touches is saved first, so a regression cannot leave a renamed fixture behind.
        var touched = new[] { english, program, FixturePath("TestTests", "EnglishGreeterTests.cs") };
        var originals = touched.ToDictionary(p => p, File.ReadAllText);
        File.SetAttributes(program, File.GetAttributes(program) | FileAttributes.ReadOnly);
        try
        {
            // EnglishGreeter.cs line 3: `public class EnglishGreeter : IGreeter` — the name starts at column 14.
            var result = await host.Tool.InvokeAsync(english, line: 3, column: 14, newName: "BritishGreeter",
                applyEdits: true, ct: CancellationToken.None);

            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("FILE_READ_ONLY");
            foreach (var path in touched)
                File.ReadAllText(path).Should().Be(originals[path], "no file is written unless every file can be");
        }
        finally
        {
            File.SetAttributes(program, File.GetAttributes(program) & ~FileAttributes.ReadOnly);
            foreach (var path in touched)
            {
                if (File.ReadAllText(path) == originals[path]) continue;
                File.WriteAllText(path, originals[path]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            }
        }
    }

    [Test]
    public async Task ApplyEdits_refuses_a_file_another_program_holds_open_and_writes_nothing()
    {
        // The rename holds every target exclusively from check to write; a file someone else holds
        // without sharing is refused up front rather than raced.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutch = FixturePath("TestLib", "DutchGreeter.cs");
        var original = File.ReadAllText(dutch);

        using (new FileStream(dutch, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await host.Tool.InvokeAsync(dutch, line: 3, column: 14, newName: "NederlandseGreeter",
                applyEdits: true, ct: CancellationToken.None);
            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("FILE_LOCKED");
        }

        File.ReadAllText(dutch).Should().Be(original);
    }

    [Test]
    public async Task ApplyEdits_refuses_text_that_is_not_valid_in_its_declared_encoding()
    {
        // A Windows-1252 'é' is invalid UTF-8. Replacement-character decoding made the file look
        // unchanged on both sides, and the rename wrote it back as a U+FFFD — corrupted.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutch = FixturePath("TestLib", "DutchGreeter.cs");
        var original = File.ReadAllBytes(dutch);
        byte[] legacy = [.. original, .. "// caf"u8.ToArray(), 0xE9, (byte)'\n'];
        try
        {
            File.WriteAllBytes(dutch, legacy);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(1));

            var result = await host.Tool.InvokeAsync(dutch, line: 3, column: 14, newName: "NederlandseGreeter",
                applyEdits: true, ct: CancellationToken.None);

            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("UNSUPPORTED_ENCODING");
            File.ReadAllBytes(dutch).Should().Equal(legacy);
        }
        finally
        {
            File.WriteAllBytes(dutch, original);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(2));
        }
    }

    [Test]
    public async Task ApplyEdits_renames_a_utf32_file_and_keeps_its_byte_order_mark()
    {
        // FF FE 00 00 used to be read as a UTF-16 BOM, so an unchanged UTF-32 file always looked stale.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutch = FixturePath("TestLib", "DutchGreeter.cs");
        var original = File.ReadAllBytes(dutch);
        var utf32 = new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: true);
        try
        {
            File.WriteAllText(dutch, System.Text.Encoding.UTF8.GetString(original), utf32);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(1));

            var result = await host.Tool.InvokeAsync(dutch, line: 3, column: 14, newName: "NederlandseGreeter",
                applyEdits: true, ct: CancellationToken.None);

            result.Error.Should().BeNull();
            var bytes = File.ReadAllBytes(dutch);
            bytes.Take(4).Should().Equal((byte)0xFF, (byte)0xFE, (byte)0x00, (byte)0x00);
            utf32.GetString(bytes, 4, bytes.Length - 4).Should().Contain("class NederlandseGreeter");
        }
        finally
        {
            File.WriteAllBytes(dutch, original);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(2));
        }
    }

    [Test]
    public async Task ApplyEdits_keeps_the_files_byte_order_mark()
    {
        // TOOL-008: File.WriteAllTextAsync wrote UTF-8 without a BOM, re-encoding every renamed file.
        await using var host = await TestHost.CreateAsync<RenameSymbolTool>();
        var dutch = FixturePath("TestLib", "DutchGreeter.cs");
        var original = File.ReadAllBytes(dutch);
        try
        {
            File.WriteAllBytes(dutch, [0xEF, 0xBB, 0xBF, .. original]);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(1));

            var result = await host.Tool.InvokeAsync(dutch, line: 3, column: 14, newName: "NederlandseGreeter",
                applyEdits: true, ct: CancellationToken.None);

            result.Error.Should().BeNull();
            var bytes = File.ReadAllBytes(dutch);
            bytes.Take(3).Should().Equal((byte)0xEF, (byte)0xBB, (byte)0xBF);
            System.Text.Encoding.UTF8.GetString(bytes).Should().Contain("class NederlandseGreeter");
        }
        finally
        {
            File.WriteAllBytes(dutch, original);
            File.SetLastWriteTimeUtc(dutch, DateTime.UtcNow.AddSeconds(2));
        }
    }
}
