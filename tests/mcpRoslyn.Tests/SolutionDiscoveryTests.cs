using FluentAssertions;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public class SolutionDiscoveryTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateSolution(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var content = full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? "<Solution />"
            : "Microsoft Visual Studio Solution File, Format Version 12.00";
        File.WriteAllText(full, content);
        return full;
    }

    // ---- Upward search (existing behavior must be preserved) ----

    [Test]
    public void Discover_finds_sln_in_exact_directory()
    {
        var sln = CreateSolution("Test.sln");

        SolutionDiscovery.Discover(_root).Should().Be(sln);
    }

    [Test]
    public void Discover_finds_sln_in_parent_directory()
    {
        var sln = CreateSolution("MyRepo.sln");
        var child = Path.Combine(_root, "src", "MyProject");
        Directory.CreateDirectory(child);

        SolutionDiscovery.Discover(child).Should().Be(sln);
    }

    [Test]
    public void Discover_prefers_sln_over_slnx_in_same_directory()
    {
        var sln = CreateSolution("Test.sln");
        CreateSolution("Test.slnx");

        SolutionDiscovery.Discover(_root).Should().Be(sln);
    }

    // ---- Downward search (new behavior) ----

    [Test]
    public void Discover_finds_sln_in_subdirectory_when_none_upward()
    {
        // Mirrors the real failure: solution lives in ./src, below the working directory.
        var sln = CreateSolution(Path.Combine("src", "MyApp.sln"));

        SolutionDiscovery.Discover(_root).Should().Be(sln);
    }

    [Test]
    public void Discover_prefers_shallowest_solution_when_searching_downward()
    {
        CreateSolution(Path.Combine("a", "deep", "Deep.sln"));
        var shallow = CreateSolution(Path.Combine("b", "Shallow.sln"));

        SolutionDiscovery.Discover(_root).Should().Be(shallow);
    }

    [Test]
    public void Discover_ignores_bin_and_obj_directories_when_searching_downward()
    {
        // 'bin' sorts before 'src'; without exclusion BFS would return the bin copy.
        CreateSolution(Path.Combine("bin", "Generated.sln"));
        var real = CreateSolution(Path.Combine("src", "Real.sln"));

        SolutionDiscovery.Discover(_root).Should().Be(real);
    }

    [Test]
    public void Discover_prefers_enclosing_solution_over_nested_one()
    {
        // Working dir is ./src (no solution); an enclosing solution and a nested
        // solution both exist. The enclosing (upward) one wins.
        var enclosing = CreateSolution("Enclosing.sln");
        CreateSolution(Path.Combine("src", "proj", "Nested.sln"));
        var start = Path.Combine(_root, "src");
        Directory.CreateDirectory(start);

        SolutionDiscovery.Discover(start).Should().Be(enclosing);
    }

    [Test]
    public void Discover_skips_a_directory_it_cannot_list_instead_of_throwing()
    {
        // TOOL-011: GetFiles sat outside the DirectoryNotFound/UnauthorizedAccess guard, so a missing or
        // unreadable directory aborted discovery.
        var act = () => SolutionDiscovery.Discover(Path.Combine(_root, "gone"));

        act.Should().NotThrow();
    }

    [Test]
    public void Discover_returns_null_when_no_solution_exists_in_tree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "empty"));

        SolutionDiscovery.Discover(_root).Should().BeNull();
    }

    [Test]
    public void Discover_prefers_the_solution_declaring_the_most_projects_in_one_directory()
    {
        // WS-005: Electron.NET keeps ElectronNET.Lean.sln (4 projects) beside ElectronNET.sln (all of them); name
        // order picked the lean one and every query was blind to the test and app projects.
        File.WriteAllText(Path.Combine(_root, "A.Lean.sln"), SlnWith(1));
        var full = Path.Combine(_root, "B.Full.sln");
        File.WriteAllText(full, SlnWith(3));

        SolutionDiscovery.Discover(_root).Should().Be(full);
    }

    [Test]
    public void ProjectCount_counts_indented_csharp_declarations_only_and_reads_slnx()
    {
        // Codex review: MSBuild trims .sln lines, so an indented declaration is real; folders and non-C# projects
        // (.esproj) are not something the workspace loads, so they must not make a solution look bigger.
        var sln = Path.Combine(_root, "Folders.sln");
        File.WriteAllText(sln, SlnWith(1) +
            "  Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Indented\", \"Indented\\Indented.csproj\", \"{55555555-5555-5555-5555-555555555555}\"\r\nEndProject\r\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{33333333-3333-3333-3333-333333333333}\"\nEndProject\n" +
            // A folder may be named like a project, and a malformed line may mention one: neither is a declaration.
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Tools.csproj\", \"Tools.csproj\", \"{77777777-7777-7777-7777-777777777777}\"\nEndProject\n" +
            "Project(garbage.csproj\n" +
            "Project(\"{garbage}\") = \"Bogus\", \"Bogus.csproj\", \"{garbage}\" trailing junk\n" +
            "Project(\"{54A90642-561A-4BB1-A94E-469ADEE60C69}\") = \"web\", \"web\\web.esproj\", \"{66666666-6666-6666-6666-666666666666}\"\nEndProject\n" +
            // Real ones in other shapes: a name with spaces, lowercase GUIDs, a VB project.
            "Project(\"{f184b08f-c81c-45f6-a57f-5abd9991f28f}\") = \"My Vb Lib\", \"My Vb Lib\\My Vb Lib.vbproj\", \"{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}\"\nEndProject\n");
        SolutionDiscovery.ProjectCount(new FileInfo(sln)).Should().Be(3);

        var slnx = Path.Combine(_root, "X.slnx");
        File.WriteAllText(slnx,
            "<Solution><Folder Name=\"/src/\"><Project Path=\"a/a.csproj\" /></Folder><Project Path=\"b/b.csproj\" />" +
            "<Project Path=\"legacy.csproj/web.esproj\" /></Solution>");
        SolutionDiscovery.ProjectCount(new FileInfo(slnx)).Should().Be(2);
    }

    private static string SlnWith(int projects) =>
        "Microsoft Visual Studio Solution File, Format Version 12.00\n" + string.Concat(Enumerable.Range(0, projects).Select(i =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"P{i}\", \"P{i}\\P{i}.csproj\", \"{{{Guid.NewGuid()}}}\"\nEndProject\n"));
}
