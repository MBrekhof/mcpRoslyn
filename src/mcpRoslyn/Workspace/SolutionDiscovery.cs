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

    /// <summary>
    /// The solutions in one directory, the one declaring the most projects first: a repo with a lean and a
    /// full solution side by side should load the full one, or every query is blind to the projects only it
    /// declares (WS-005). Ties keep .sln before .slnx, then name order. An unreadable or vanished directory
    /// holds no solution; it must not abort discovery (TOOL-011).
    /// </summary>
    private static IEnumerable<FileInfo> SolutionsIn(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFiles("*.sln")
                .Concat(dir.GetFiles("*.slnx"))
                .OrderByDescending(ProjectCount)
                .ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase) // .sln before .slnx
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private const string GuidPattern = "[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}";

    /// <summary><c>Project("{type}") = "Name", "path", "{id}"</c>, whole line — capturing the type GUID and the path.</summary>
    private static readonly System.Text.RegularExpressions.Regex SlnProjectDeclaration = new(
        "^Project\\(\"\\{(" + GuidPattern + ")\\}\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]+)\"\\s*,\\s*\"\\{" + GuidPattern + "\\}\"\\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Solution folders are declared like projects, under their own type GUID — and may be named anything.</summary>
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    /// <summary>
    /// The C#/VB projects a solution declares — what the workspace can load; solution folders, malformed lines and
    /// .esproj/.sqlproj entries don't count. MSBuild trims each .sln line before recognising a declaration, so
    /// indented ones count. ponytail: declared projects, not the project-reference closure — a lean solution whose
    /// projects reference others still scores only its own.
    /// </summary>
    internal static int ProjectCount(FileInfo solution)
    {
        try
        {
            var text = File.ReadAllText(solution.FullName);
            var paths = solution.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? System.Xml.Linq.XDocument.Parse(text).Descendants("Project").Select(p => (string?)p.Attribute("Path"))
                : text.Split('\n')
                    .Select(line => SlnProjectDeclaration.Match(line.Trim()))
                    .Where(m => m.Success && !m.Groups[1].Value.Equals(SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
                    .Select(m => (string?)m.Groups[2].Value);
            return paths.Count(path => Path.GetExtension(path?.Trim()) is { } extension
                                       && (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                                           || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return 0; // unreadable: still a candidate, just never preferred
        }
    }

    private static bool IsSearchable(DirectoryInfo dir) =>
        (dir.Attributes & FileAttributes.ReparsePoint) == 0          // don't follow symlinks/junctions
        && !dir.Name.StartsWith('.')                                  // skip .git, .vs, .github, ...
        && !ExcludedDirectories.Contains(dir.Name, StringComparer.OrdinalIgnoreCase);
}
