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
    public void Discover_returns_null_when_no_solution_exists_in_tree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "empty"));

        SolutionDiscovery.Discover(_root).Should().BeNull();
    }
}
