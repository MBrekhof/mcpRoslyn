namespace mcpRoslyn.Workspace;

/// <summary>
/// Locates a solution file (.sln/.slnx) given a starting directory.
/// First walks <em>up</em> the directory tree (the closest enclosing solution wins);
/// if none is found, searches <em>down</em> breadth-first so the shallowest nested
/// solution wins. Returns the full path, or <c>null</c> when nothing is found.
/// </summary>
public static class SolutionDiscovery
{
    /// <summary>Directories never descended into during the downward search.</summary>
    private static readonly string[] ExcludedDirectories =
        { "bin", "obj", "node_modules", "packages" };

    /// <summary>How deep the downward search descends below the start directory.</summary>
    private const int MaxDownwardDepth = 8;

    public static string? Discover(string startDirectory)
    {
        var start = new DirectoryInfo(startDirectory);
        return FindUpward(start) ?? FindDownward(start);
    }

    private static string? FindUpward(DirectoryInfo? dir)
    {
        for (; dir is not null; dir = dir.Parent)
        {
            var match = SolutionsIn(dir).FirstOrDefault();
            if (match is not null) return match.FullName;
        }
        return null;
    }

    private static string? FindDownward(DirectoryInfo root)
    {
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            var match = SolutionsIn(dir).FirstOrDefault();
            if (match is not null) return match.FullName;

            if (depth >= MaxDownwardDepth) continue;

            DirectoryInfo[] children;
            try
            {
                children = dir.GetDirectories();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var child in children
                         .Where(IsSearchable)
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                queue.Enqueue((child, depth + 1));
            }
        }
        return null;
    }

    private static IEnumerable<FileInfo> SolutionsIn(DirectoryInfo dir) =>
        dir.GetFiles("*.sln")
            .Concat(dir.GetFiles("*.slnx"))
            .OrderBy(f => f.Extension, StringComparer.OrdinalIgnoreCase) // .sln before .slnx
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsSearchable(DirectoryInfo dir) =>
        (dir.Attributes & FileAttributes.ReparsePoint) == 0          // don't follow symlinks/junctions
        && !dir.Name.StartsWith('.')                                  // skip .git, .vs, .github, ...
        && !ExcludedDirectories.Contains(dir.Name, StringComparer.OrdinalIgnoreCase);
}
