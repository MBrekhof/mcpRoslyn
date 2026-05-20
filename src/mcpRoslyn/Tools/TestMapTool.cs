using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record TestMapCandidate(
    string TestSymbol,
    Contracts.SymbolLocation? Location,
    string Confidence,    // "high" | "medium"
    string Via);          // "reference" | "name-match"

public sealed record TestMapResult(
    IReadOnlyList<TestMapCandidate> Candidates,
    IReadOnlyList<string> TestProjectsScanned);

[McpServerToolType]
internal sealed class TestMapTool(IWorkspaceService ws, ILogger<TestMapTool> log)
    : ToolBase(ws, log)
{
    private static readonly string[] TestFrameworkMarkers = { "xunit", "nunit", "MSTest" };
    private static readonly string[] TestNameSuffixes = { "Tests", "Test", "Spec", "IntegrationTests" };

    [McpServerTool(Name = "test_map")]
    [Description("Maps a production symbol to likely tests via project-graph + reference scan (high confidence) and name-match scan (medium confidence). Returns empty when no candidates found.")]
    public Task<Contracts.ToolResult<TestMapResult>> InvokeAsync(
        string? symbolId = null,
        string? filePath = null, int? line = null, int? column = null,
        int maxResults = 10,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);

            ISymbol? symbol = null;
            if (!string.IsNullOrWhiteSpace(symbolId))
            {
                symbol = await RoslynHelpers.ResolveSymbolByIdAsync(solution, symbolId, ct2);
                if (symbol is null)
                    return Contracts.ToolResult<TestMapResult>.Fail(
                        "SYMBOL_NOT_FOUND", $"Symbol ID '{symbolId}' did not resolve.");
            }
            else if (filePath is not null && line is int l && column is int c)
            {
                var doc = RoslynHelpers.FindDocument(solution, filePath);
                if (doc is null) return Contracts.ToolResult<TestMapResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");
                symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, l, c, ct2);
                if (symbol is null) return Contracts.ToolResult<TestMapResult>.Fail(
                    "SYMBOL_NOT_FOUND", $"No symbol at {filePath}:{l}:{c}.");
            }
            else
            {
                return Contracts.ToolResult<TestMapResult>.Fail(
                    "POSITION_INVALID", "Must provide either symbolId OR (filePath, line, column).");
            }

            // 1. Find containing project of the production symbol
            var prodProject = solution.GetProject(symbol.ContainingAssembly);
            if (prodProject is null)
            {
                // Fallback: use Locations
                var loc = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                if (loc?.SourceTree is not null) prodProject = solution.GetDocument(loc.SourceTree)?.Project;
            }
            if (prodProject is null) return Contracts.ToolResult<TestMapResult>.Ok(
                new TestMapResult(Array.Empty<TestMapCandidate>(), Array.Empty<string>()));

            // 2. Identify test projects that transitively reference the production project
            var testProjects = solution.Projects
                .Where(p => LooksLikeTestProject(p))
                .Where(p => TransitivelyReferences(p, prodProject.Id, solution))
                .ToArray();

            var candidates = new List<TestMapCandidate>();
            var seen = new HashSet<string>();

            // 3. Reference scan — high confidence
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct2);
            foreach (var rs in refs)
            {
                foreach (var rl in rs.Locations)
                {
                    var doc = rl.Document;
                    if (!testProjects.Any(tp => tp.Id == doc.Project.Id)) continue;
                    var loc = RoslynHelpers.ToLocation(rl.Location);
                    if (loc is null) continue;
                    var key = $"{doc.Project.Name}:{loc.FilePath}:{loc.Line}";
                    if (!seen.Add(key)) continue;
                    candidates.Add(new TestMapCandidate(
                        TestSymbol: $"{doc.Project.Name}.{Path.GetFileNameWithoutExtension(loc.FilePath)}",
                        Location: loc,
                        Confidence: "high",
                        Via: "reference"));
                }
            }

            // 4. Name-match scan — medium confidence
            var simpleName = symbol.Name;
            var nameMatchPatterns = new[]
            {
                $"{simpleName}Tests",
                $"{simpleName}Spec",
                $"Test{simpleName}",
                $"Tests{simpleName}"
            };
            foreach (var tp in testProjects)
            {
                var compilation = await tp.GetCompilationAsync(ct2);
                if (compilation is null) continue;
                foreach (var pattern in nameMatchPatterns)
                {
                    foreach (var found in compilation.GetSymbolsWithName(pattern, SymbolFilter.Type, ct2))
                    {
                        var info = RoslynHelpers.ToSymbolInfo(found);
                        var key = info.SymbolId.Length > 0 ? info.SymbolId : info.Signature;
                        if (!seen.Add(key)) continue;
                        candidates.Add(new TestMapCandidate(
                            TestSymbol: found.ToDisplayString(),
                            Location: info.PrimaryLocation,
                            Confidence: "medium",
                            Via: "name-match"));
                    }
                }
            }

            // 5. Sort high → medium, cap at maxResults
            candidates = candidates
                .OrderBy(c => c.Confidence == "high" ? 0 : 1)
                .ThenBy(c => c.TestSymbol)
                .Take(maxResults)
                .ToList();

            var result = new TestMapResult(
                Candidates: candidates,
                TestProjectsScanned: testProjects.Select(p => p.Name).ToArray());
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
                return Contracts.ToolResult<TestMapResult>.OkSummary($"{result.Candidates.Count} candidate tests");
            return Contracts.ToolResult<TestMapResult>.Ok(result);
        }, ct);

    private static bool LooksLikeTestProject(Project p)
    {
        // Name-based: ends with Tests, Test, Spec, IntegrationTests (with or without leading dot)
        if (TestNameSuffixes.Any(s => p.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        // Reference-based: project references a well-known test framework assembly
        foreach (var r in p.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (r.FilePath is null) continue;
            if (TestFrameworkMarkers.Any(m => r.FilePath.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool TransitivelyReferences(Project project, ProjectId targetId, Solution sol)
    {
        var visited = new HashSet<ProjectId>();
        var queue = new Queue<ProjectId>();
        queue.Enqueue(project.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current == targetId) return true;
            var p = sol.GetProject(current);
            if (p is null) continue;
            foreach (var r in p.ProjectReferences) queue.Enqueue(r.ProjectId);
        }
        return false;
    }
}
