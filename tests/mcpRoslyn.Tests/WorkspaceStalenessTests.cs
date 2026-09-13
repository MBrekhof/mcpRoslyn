using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mcpRoslyn.Tests.TestHelpers;
using mcpRoslyn.Tools;
using mcpRoslyn.Workspace;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

/// <summary>WS-007: a workspace that no longer matches the disk says so on every tool result.</summary>
[TestFixture]
public sealed class WorkspaceStalenessTests
{
    private static string TestLibDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestSolution", "TestLib");

    [Test]
    public async Task A_new_source_file_or_a_changed_project_file_warns_until_reload()
    {
        // Reported from a real session: find_references missed a caller in a file added after load, and the
        // result looked complete.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var tool = new ProjectOverviewTool(host.Workspace, NullLogger<ProjectOverviewTool>.Instance);
        var added = Path.Combine(TestLibDirectory, $"AddedAfterLoad{Guid.NewGuid():N}.cs");
        var addedName = Path.GetFileName(added);
        var project = Path.Combine(TestLibDirectory, "TestLib.csproj");
        var projectTime = File.GetLastWriteTimeUtc(project);
        try
        {
            (await tool.InvokeAsync()).Warnings.Should().BeNull();

            await File.WriteAllTextAsync(added, "namespace TestLib;\npublic sealed class AddedAfterLoad { }\n");
            await WaitForStaleAsync(host.Workspace, r => r.Contains($"{addedName} added"));
            (await tool.InvokeAsync()).Warnings.Should().ContainSingle()
                .Which.Should().Contain($"{addedName} added").And.Contain("reload_workspace");

            File.SetLastWriteTimeUtc(project, projectTime.AddSeconds(5));
            host.Workspace.StaleReasons.Should().Contain(r => r.Contains("TestLib.csproj changed"));

            await host.Workspace.ReloadAsync();
            host.Workspace.StaleReasons.Should().BeEmpty("the reloaded generation compares against its own load");
            (await tool.InvokeAsync()).Warnings.Should().BeNull();
        }
        finally
        {
            if (File.Exists(added)) File.Delete(added);
            File.SetLastWriteTimeUtc(project, projectTime);
        }
    }

    [Test]
    public async Task A_deleted_source_file_warns_until_restored_and_an_editor_backup_save_stays_quiet()
    {
        // Deletions come from the per-call refresh, which also covers linked files outside every watched directory.
        // A save that renames the file to a backup (Foo.cs~) is raised because the old name matched *.cs, and must
        // not read as an added file; once the file is back, the next call carries no warning at all.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var tool = new ProjectOverviewTool(host.Workspace, NullLogger<ProjectOverviewTool>.Instance);
        var path = Path.Combine(TestLibDirectory, "DutchGreeter.cs");
        var backupPath = path + "~";
        var sentinel = Path.Combine(TestLibDirectory, $"Sentinel{Guid.NewGuid():N}.cs");
        var backup = await File.ReadAllTextAsync(path);
        try
        {
            File.Move(path, backupPath);
            (await tool.InvokeAsync()).Warnings.Should().ContainSingle().Which.Should().Contain("DutchGreeter.cs deleted");

            await File.WriteAllTextAsync(path, backup);
            // One watcher delivers its events in order: once the sentinel written after them has been seen, the
            // backup rename and the re-created file have been handled too.
            await File.WriteAllTextAsync(sentinel, "namespace TestLib;\n");
            (await WaitForStaleAsync(host.Workspace, r => r.Contains(Path.GetFileName(sentinel))))
                .Should().Contain(r => r.Contains(Path.GetFileName(sentinel)), "the assertion below means nothing until the events have arrived");
            File.Delete(sentinel);

            (await tool.InvokeAsync()).Warnings.Should().BeNull("the file is back, and neither the backup nor the sentinel remains");
        }
        finally
        {
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, backup);
            File.Delete(backupPath);
            File.Delete(sentinel);
        }
    }

    [Test]
    public async Task A_populated_directory_moved_into_a_project_warns_for_the_files_inside_it()
    {
        // A directory move raises one event for the directory and none for the files it carries.
        await using var host = await TestHost.CreateWorkspaceAsync();
        var name = $"MovedFeature{Guid.NewGuid():N}";
        // Staged beside the solution directory: outside every watched root, yet on the same volume, which a
        // directory move requires.
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TestLibDirectory)!)!, name);
        var inside = Path.Combine(TestLibDirectory, name);
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "Deeper"));
            await File.WriteAllTextAsync(Path.Combine(staging, "Deeper", "MovedIn.cs"),
                "namespace TestLib;\npublic sealed class MovedIn { }\n");
            Directory.Move(staging, inside);

            (await WaitForStaleAsync(host.Workspace, r => r.Contains("MovedIn.cs added")))
                .Should().Contain(r => r.Contains("MovedIn.cs added"));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(inside)) Directory.Delete(inside, recursive: true);
        }
    }

    /// <summary>File-system events arrive asynchronously.</summary>
    private static async Task<IReadOnlyList<string>> WaitForStaleAsync(IWorkspaceService workspace, Func<string, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!workspace.StaleReasons.Any(until) && DateTime.UtcNow < deadline) await Task.Delay(50);
        return workspace.StaleReasons;
    }
}
