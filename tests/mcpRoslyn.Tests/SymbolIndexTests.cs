using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Options;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public class SymbolIndexTests
{
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    [Test]
    public async Task SymbolIndex_property_is_available_after_load()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        sut.SymbolIndex.Should().NotBeNull();
    }

    [Test]
    public async Task QueryAttribute_returns_fixture_MyMarker_matches()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", solution);

        matches.Should().HaveCount(2);
        matches.Select(m => m.Name).Should().BeEquivalentTo("MarkedType", "MarkedMethod");
    }

    [Test]
    public async Task QueryReturnType_int_finds_partial_class_methods()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryReturnType("int", solution);

        matches.Should().HaveCountGreaterThanOrEqualTo(2);
        matches.Select(m => m.Name).Should().Contain(new[] { "Foo", "Bar" });
    }

    [Test]
    public async Task QueryParameterType_string_finds_Greet_methods()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryParameterType("string", solution);

        matches.Should().HaveCountGreaterThanOrEqualTo(3);
        matches.Select(m => m.Name).Distinct().Should().Contain("Greet");
    }

    [Test]
    public async Task QueryAttribute_dirty_walk_picks_up_newly_added_attribute()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Partial1.cs");
        var backup = File.ReadAllText(doc.FilePath!);

        try
        {
            var mutated = backup.Replace(
                "public partial class PartialThing",
                "[MyMarker]\npublic partial class PartialThing");
            File.WriteAllText(doc.FilePath!, mutated);
            File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

            var refreshed = await sut.GetFreshSolutionAsync();
            var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", refreshed);

            matches.Should().Contain(m => m.Name == "PartialThing",
                "the dirty walk should surface the newly-attributed partial class");
        }
        finally
        {
            File.WriteAllText(doc.FilePath!, backup);
        }
    }

    [Test]
    public async Task QueryAttribute_dirty_walk_excludes_removed_attribute()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "MyAttribute.cs");
        var backup = File.ReadAllText(doc.FilePath!);

        try
        {
            // Tolerate either CRLF or LF between the attribute and the class declaration —
            // core.autocrlf can flip the fixture's checked-out line endings between
            // branch switches, so searching only "[MyMarker]\n..." is fragile on Windows.
            var mutated = backup
                .Replace("[MyMarker]\r\npublic class MarkedType", "public class MarkedType")
                .Replace("[MyMarker]\npublic class MarkedType", "public class MarkedType");
            File.WriteAllText(doc.FilePath!, mutated);
            File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

            var refreshed = await sut.GetFreshSolutionAsync();
            var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", refreshed);

            matches.Should().NotContain(m => m.Name == "MarkedType",
                "the dirty walk should exclude the entry whose attribute was removed");
            matches.Should().Contain(m => m.Name == "MarkedMethod",
                "MarkedMethod still carries its [MyMarker] attribute");
        }
        finally
        {
            File.WriteAllText(doc.FilePath!, backup);
        }
    }

    [Test]
    public async Task AllSymbols_dirty_walk_picks_up_a_newly_added_type()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Partial1.cs");
        var backup = File.ReadAllText(doc.FilePath!);

        try
        {
            sut.SymbolIndex.AllSymbols(solution).Symbols
                .Should().NotContain(e => e.Info.Name == "AddedAfterIndexBuild");

            File.WriteAllText(doc.FilePath!, backup + "\npublic sealed class AddedAfterIndexBuild { }\n");
            File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

            // IDX-001: AllSymbols used to return the raw build-time list, so a symbol added after
            // the index was built stayed invisible until an explicit reload_workspace.
            var refreshed = await sut.GetFreshSolutionAsync();
            sut.SymbolIndex.AllSymbols(refreshed).Symbols
                .Should().Contain(e => e.Info.Name == "AddedAfterIndexBuild");
        }
        finally
        {
            File.WriteAllText(doc.FilePath!, backup);
        }
    }

    [Test]
    public async Task ReloadAsync_constructs_fresh_index_with_empty_dirty_set()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var doc = (await sut.GetFreshSolutionAsync()).Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "EnglishGreeter.cs");
        var backup = File.ReadAllText(doc.FilePath!);

        try
        {
            File.WriteAllText(doc.FilePath!, backup + "\n// touched\n");
            File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));
            await sut.GetFreshSolutionAsync();

            var indexBefore = sut.SymbolIndex;

            await sut.ReloadAsync();
            await sut.WarmupTask;

            var indexAfter = sut.SymbolIndex;
            indexAfter.Should().NotBeSameAs(indexBefore,
                "ReloadAsync should construct a fresh SymbolIndex");

            var solution = await sut.GetFreshSolutionAsync();
            var matches = indexAfter.QueryAttribute("TestLib.MyMarkerAttribute", solution);
            matches.Should().HaveCount(2);
        }
        finally
        {
            File.WriteAllText(doc.FilePath!, backup);
        }
    }

    [Test]
    public async Task QueryAttribute_partial_class_entry_invalidates_via_any_declaring_doc()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var initialSolution = await sut.GetFreshSolutionAsync();
        var partial1 = initialSolution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Partial1.cs");
        var partial2 = initialSolution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.Name == "Partial2.cs");
        var partial1Backup = File.ReadAllText(partial1.FilePath!);
        var partial2Backup = File.ReadAllText(partial2.FilePath!);

        try
        {
            // Stage 1: write [MyMarker] into Partial1.cs and RELOAD so the index
            // sees the attribute at build time. The IndexedSymbol for PartialThing
            // should now have BOTH Partial1.cs and Partial2.cs DocumentIds in
            // its DeclaringDocs set (partial types span both files).
            var partial1Mutated = partial1Backup.Replace(
                "public partial class PartialThing",
                "[MyMarker]\npublic partial class PartialThing");
            File.WriteAllText(partial1.FilePath!, partial1Mutated);
            File.SetLastWriteTimeUtc(partial1.FilePath!, DateTime.UtcNow.AddSeconds(1));

            await sut.ReloadAsync();
            await sut.WarmupTask;

            // Stage 2: touch Partial2.cs — the file WITHOUT the attribute declaration.
            // The cached entry's DeclaringDocs should intersect dirty, invalidating it.
            // The dirty walk on Partial2.cs should re-find PartialThing because the
            // partial type still has [MyMarker] (declared in Partial1.cs).
            File.WriteAllText(partial2.FilePath!, partial2Backup + "\n// touched\n");
            File.SetLastWriteTimeUtc(partial2.FilePath!, DateTime.UtcNow.AddSeconds(2));
            var afterPartial2Touch = await sut.GetFreshSolutionAsync();

            var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", afterPartial2Touch);
            matches.Should().Contain(m => m.Name == "PartialThing",
                "DeclaringDocs spans both partial files; touching Partial2.cs should " +
                "invalidate the cached entry, and the dirty walk on Partial2.cs should re-find PartialThing");
        }
        finally
        {
            File.WriteAllText(partial1.FilePath!, partial1Backup);
            File.WriteAllText(partial2.FilePath!, partial2Backup);
        }
    }

    [Test]
    public async Task AllSymbols_keeps_same_named_types_from_different_projects_apart()
    {
        // IDX-005: TestApp and TestWeb each declare Shared.Dup, so both carry the id "T:Shared.Dup".
        // De-duplicating on the id alone kept one and silently dropped the other.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;
        var solution = await sut.GetFreshSolutionAsync();

        sut.SymbolIndex.AllSymbols(solution).Symbols
            .Where(e => e.SymbolId == "T:Shared.Dup")
            .Select(e => e.Info.PrimaryLocation!.FilePath)
            .Should().HaveCount(2).And.OnlyHaveUniqueItems();

        // The harder case: one file linked into TestApp and TestWeb — same path, two assemblies.
        sut.SymbolIndex.AllSymbols(solution).Symbols
            .Where(e => e.SymbolId == "T:Shared.Linked")
            .Should().HaveCount(2);
    }

    [Test]
    public async Task Removing_an_attribute_from_a_partial_implementation_alone_drops_the_match()
    {
        // The build recorded only the definition's file as declaring the method, so a change to the
        // implementation file never invalidated the cached [MyMarker] match.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var docs = (await sut.GetFreshSolutionAsync()).Projects.SelectMany(p => p.Documents).ToList();
        var part1 = docs.First(d => d.Name == "Partial1.cs").FilePath!;
        var part2 = docs.First(d => d.Name == "Partial2.cs").FilePath!;
        var backup1 = File.ReadAllText(part1);
        var backup2 = File.ReadAllText(part2);
        try
        {
            File.WriteAllText(part1, backup1.Replace("public int Foo() => 1;",
                "public int Foo() => 1;\n    public partial int GetValue();"));
            File.WriteAllText(part2, backup2.Replace("public int Bar() => 2;",
                "public int Bar() => 2;\n    [MyMarker] public partial int GetValue() => 3;"));
            File.SetLastWriteTimeUtc(part1, DateTime.UtcNow.AddSeconds(1));
            File.SetLastWriteTimeUtc(part2, DateTime.UtcNow.AddSeconds(1));
            await sut.ReloadAsync(); // the index is built with the attribute on the implementation
            await sut.WarmupTask;
            sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", await sut.GetFreshSolutionAsync())
                .Should().Contain(s => s.Name == "GetValue");

            File.WriteAllText(part2, backup2.Replace("public int Bar() => 2;",
                "public int Bar() => 2;\n    public partial int GetValue() => 3;"));
            File.SetLastWriteTimeUtc(part2, DateTime.UtcNow.AddSeconds(2));

            sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", await sut.GetFreshSolutionAsync())
                .Should().NotContain(s => s.Name == "GetValue");
        }
        finally
        {
            File.WriteAllText(part1, backup1);
            File.WriteAllText(part2, backup2);
        }
    }

    [Test]
    public async Task Removing_a_partial_implementation_keeps_the_surviving_definition()
    {
        // Tracking both parts' files (so an implementation-only edit invalidates) dropped the method
        // when the implementation was deleted: only the dirty file was re-walked, and the definition
        // lives in the unchanged one.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var docs = (await sut.GetFreshSolutionAsync()).Projects.SelectMany(p => p.Documents).ToList();
        var part1 = docs.First(d => d.Name == "Partial1.cs").FilePath!;
        var part2 = docs.First(d => d.Name == "Partial2.cs").FilePath!;
        var backup1 = File.ReadAllText(part1);
        var backup2 = File.ReadAllText(part2);
        try
        {
            File.WriteAllText(part1, backup1.Replace("public int Foo() => 1;",
                "public int Foo() => 1;\n    [MyMarker] partial void Hook(int value);"));
            File.WriteAllText(part2, backup2.Replace("public int Bar() => 2;",
                "public int Bar() => 2;\n    partial void Hook(int value) { }"));
            File.SetLastWriteTimeUtc(part1, DateTime.UtcNow.AddSeconds(1));
            File.SetLastWriteTimeUtc(part2, DateTime.UtcNow.AddSeconds(1));
            await sut.ReloadAsync();
            await sut.WarmupTask;
            sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", await sut.GetFreshSolutionAsync())
                .Should().Contain(s => s.Name == "Hook");

            File.WriteAllText(part2, backup2); // implementation gone; the definition in Partial1.cs survives
            File.SetLastWriteTimeUtc(part2, DateTime.UtcNow.AddSeconds(2));

            sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", await sut.GetFreshSolutionAsync())
                .Should().Contain(s => s.Name == "Hook");
        }
        finally
        {
            File.WriteAllText(part1, backup1);
            File.WriteAllText(part2, backup2);
        }
    }

    [Test]
    public async Task Dirty_walk_reports_a_partial_method_split_across_files_once()
    {
        // Keyed on (id, file), the definition in Partial1.cs and the implementation in Partial2.cs
        // read as two symbols once both files were re-walked; the build only ever saw one.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var docs = (await sut.GetFreshSolutionAsync()).Projects.SelectMany(p => p.Documents).ToList();
        var part1 = docs.First(d => d.Name == "Partial1.cs").FilePath!;
        var part2 = docs.First(d => d.Name == "Partial2.cs").FilePath!;
        var backup1 = File.ReadAllText(part1);
        var backup2 = File.ReadAllText(part2);
        try
        {
            File.WriteAllText(part1, backup1.Replace("public int Foo() => 1;",
                "public int Foo() => 1;\n    public partial int GetValue();"));
            File.WriteAllText(part2, backup2.Replace("public int Bar() => 2;",
                "public int Bar() => 2;\n    public partial int GetValue() => 3;"));
            File.SetLastWriteTimeUtc(part1, DateTime.UtcNow.AddSeconds(1));
            File.SetLastWriteTimeUtc(part2, DateTime.UtcNow.AddSeconds(1));
            var refreshed = await sut.GetFreshSolutionAsync();

            sut.SymbolIndex.QueryReturnType("int", refreshed)
                .Where(s => s.Name == "GetValue")
                .Should().ContainSingle();
        }
        finally
        {
            File.WriteAllText(part1, backup1);
            File.WriteAllText(part2, backup2);
        }
    }

    [Test]
    public async Task A_refresh_folds_a_change_in_once_and_keeps_members_without_syntax()
    {
        // IDX-004: the dirty set was never cleared, so every query re-walked every document edited since
        // load; and that walk met only declarations, so an implicit constructor vanished on refresh.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var path = solution.Projects.SelectMany(p => p.Documents).First(d => d.Name == "Partial1.cs").FilePath!;
        // Identity and count: a set of ids would hide an entry that is duplicated or merged differently.
        static List<string> Identities(SymbolIndex.IndexedSymbols index) => index.Symbols
            .Select(e => $"{e.SymbolId}|{e.Info.PrimaryLocation?.FilePath}|{e.DeclaringDocs.Count}")
            .Order(StringComparer.Ordinal).ToList();
        var before = Identities(sut.SymbolIndex.AllSymbols(solution));
        before.Should().Contain(i => i.StartsWith("M:TestLib.PartialThing.#ctor|"));
        var backup = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, backup + "\n// touched\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var refreshed = await sut.GetFreshSolutionAsync();

            Identities(sut.SymbolIndex.AllSymbols(refreshed))
                .Should().Equal(before, "a change that declares nothing new leaves the index as the build made it");
            var walked = sut.SymbolIndex.DocumentsWalked;
            walked.Should().BePositive();

            sut.SymbolIndex.QueryReturnType("int", refreshed);
            sut.SymbolIndex.DocumentsWalked.Should().Be(walked, "the first query folded the change in");
        }
        finally
        {
            File.WriteAllText(path, backup);
        }
    }

    [Test]
    public async Task A_cancelled_refresh_leaves_the_change_for_the_next_query()
    {
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var path = solution.Projects.SelectMany(p => p.Documents).First(d => d.Name == "Partial1.cs").FilePath!;
        var backup = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, backup.Replace("public partial class PartialThing", "[MyMarker]\npublic partial class PartialThing"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var refreshed = await sut.GetFreshSolutionAsync();

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            sut.SymbolIndex.Invoking(i => i.QueryAttribute("TestLib.MyMarkerAttribute", refreshed, cancelled.Token))
                .Should().Throw<OperationCanceledException>();

            sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", refreshed)
                .Should().Contain(m => m.Name == "PartialThing");
        }
        finally
        {
            File.WriteAllText(path, backup);
        }
    }

    [Test]
    public async Task AllSymbols_names_the_newer_solution_when_another_refresh_overtook_the_caller()
    {
        // IDX-004 review: the refresh walks the newest solution, so a caller resolving the entries against
        // its own older snapshot found a type renamed in between under neither name.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var older = await sut.GetFreshSolutionAsync();
        var path = older.Projects.SelectMany(p => p.Documents).First(d => d.Name == "Partial1.cs").FilePath!;
        var backup = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, backup + "\npublic sealed class AddedWhileAnotherCallWaited { }\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var newer = await sut.GetFreshSolutionAsync(); // another tool call's refresh

            var snapshot = sut.SymbolIndex.AllSymbols(older);

            snapshot.Solution.Should().BeSameAs(newer);
            snapshot.Symbols.Should().Contain(e => e.Info.Name == "AddedWhileAnotherCallWaited");
        }
        finally
        {
            File.WriteAllText(path, backup);
        }
    }

    [Test]
    public async Task A_refresh_that_fails_part_way_still_hands_the_files_it_read_to_the_index()
    {
        // IDX-004 review: invalidation ran after the scan, so a read failing on a later file lost the earlier
        // files' changes — their timestamps were already cached, so the retry skipped them.
        var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
        await using var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
        await sut.LoadAsync();
        await sut.WarmupTask;

        var solution = await sut.GetFreshSolutionAsync();
        var sep = Path.DirectorySeparatorChar;
        var sources = solution.Projects.SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .OfType<string>()
            .Where(p => !p.Contains($"{sep}obj{sep}"))
            .ToList();
        var first = sources[0];
        var later = sources.Last(p => p != first);
        var backup = File.ReadAllText(first);
        try
        {
            File.WriteAllText(first, backup + "\npublic sealed class ChangedBeforeAFailedRead { }\n");
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(1));
            File.SetLastWriteTimeUtc(later, DateTime.UtcNow.AddSeconds(1));
            using (new FileStream(later, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var refresh = () => sut.GetFreshSolutionAsync();
                await refresh.Should().ThrowAsync<IOException>();
            }

            var retried = await sut.GetFreshSolutionAsync();
            sut.SymbolIndex.AllSymbols(retried).Symbols
                .Should().Contain(e => e.Info.Name == "ChangedBeforeAFailedRead");
        }
        finally
        {
            File.WriteAllText(first, backup);
        }
    }
}
