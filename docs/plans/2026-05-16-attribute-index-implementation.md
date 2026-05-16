# SymbolIndex Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut `semantic_search has-attribute:` / `returns:` / `parameter-type:` from ~7.7 s to sub-100 ms by building a shared `SymbolIndex` once during warm-up; preserve always-fresh semantics via a dirty-doc set walked on each query.

**Architecture:** New `SymbolIndex` class in `mcpRoslyn.Workspace`. Owned by `WorkspaceService`, exposed via `IWorkspaceService.SymbolIndex`. Three dictionaries (attribute / return-type / parameter-type) keyed by display string AND fully-qualified metadata name. Built in parallel-per-project alongside `WarmupAsync`'s existing `GetCompilationAsync` calls. `GetFreshSolutionAsync` calls `SymbolIndex.MarkDirty(documentId)` whenever it replaces a document via `WithDocumentText`. Queries filter out entries whose `DeclaringDocs` intersect the dirty set, then walk just the dirty docs and merge.

**Tech Stack:** .NET 10, C#, Roslyn (`Microsoft.CodeAnalysis.Workspaces.MSBuild`), NUnit + FluentAssertions.

**Design reference:** [`2026-05-16-attribute-index-design.md`](./2026-05-16-attribute-index-design.md)

**Working directory:** `c:\projects\mcpRoslyn\`

---

## Conventions

- **Branch:** all work on `main` (matches v1 / v1.1 convention).
- **Solution file:** `mcpRoslyn.slnx` (NOT `.sln` — the project uses the new XML format).
- **Build command:** `dotnet build "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" -c Release` — must produce 0 errors before any commit.
- **Test command:** `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~<Class>" -c Release` — targeted at the touched class.
- **Commit style:** Conventional commits. Co-author trailer: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **stdout discipline:** never `Console.WriteLine`. Logs go to `ILogger` only.
- **Namespace casing:** the project uses lowercase `mcpRoslyn` as the C# namespace. All using directives and namespace declarations stay lowercase.
- **Push cadence:** push after each task's commit so progress is durable.

---

## File map

| File | Change | Why |
|---|---|---|
| `src/mcpRoslyn/Workspace/SymbolIndex.cs` | Create | New class + nested `IndexedSymbol` record |
| `src/mcpRoslyn/Workspace/IWorkspaceService.cs` | Modify | Add `SymbolIndex SymbolIndex { get; }` property |
| `src/mcpRoslyn/Workspace/WorkspaceService.cs` | Modify | Build index in `WarmupAsync`; call `MarkDirty` from `GetFreshSolutionAsync` |
| `src/mcpRoslyn/Tools/SemanticSearchTool.cs` | Modify | Delegate `has-attribute:` / `returns:` / `parameter-type:` to `SymbolIndex`; remove `WalkAllSymbols` and `MatchesTypeName` helpers (moved to `SymbolIndex`) |
| `tests/mcpRoslyn.Tests/SymbolIndexTests.cs` | Create | 6 new tests covering build, query, dirty walk, reload, partial-class |

`IndexedSymbol` lives as a nested record inside `SymbolIndex.cs` — it's an implementation detail, no need for a separate file.

---

## Task 1: Skeleton — `SymbolIndex` type, interface property, no-op `BuildAsync` / `MarkDirty`

This task establishes the seams. `SymbolIndex` exists with empty methods; `IWorkspaceService.SymbolIndex` returns it; `WorkspaceService` constructs one per load; `GetFreshSolutionAsync` calls `MarkDirty` but the implementation does nothing. Tests verify the property is non-null after load. Build will pass; behavior is unchanged.

**Files:**
- Create: `src/mcpRoslyn/Workspace/SymbolIndex.cs`
- Modify: `src/mcpRoslyn/Workspace/IWorkspaceService.cs`
- Modify: `src/mcpRoslyn/Workspace/WorkspaceService.cs`
- Create: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

```csharp
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
        var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

        await sut.LoadAsync();
        await sut.WarmupTask;

        sut.SymbolIndex.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails (compile error)**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SymbolIndex_property_is_available_after_load" -c Release`

Expected: BUILD FAIL — `'WorkspaceService' does not contain a definition for 'SymbolIndex'`.

- [ ] **Step 3: Create the skeleton `SymbolIndex` class**

Create `src/mcpRoslyn/Workspace/SymbolIndex.cs`:

```csharp
using Microsoft.CodeAnalysis;
using mcpRoslyn.Contracts;

namespace mcpRoslyn.Workspace;

internal sealed class SymbolIndex
{
    private readonly Dictionary<string, List<IndexedSymbol>> _byAttribute = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byReturnType = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byParameterType = new();
    private readonly HashSet<DocumentId> _dirty = new();
    private readonly object _gate = new();

    public Task BuildAsync(Solution solution, CancellationToken ct = default)
    {
        // Filled in by Task 2 (attributes) and Task 3 (return / parameter types).
        return Task.CompletedTask;
    }

    public void MarkDirty(DocumentId documentId)
    {
        lock (_gate) _dirty.Add(documentId);
    }

    public IReadOnlyList<SymbolInfo> QueryAttribute(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<SymbolInfo>();

    public IReadOnlyList<SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<SymbolInfo>();

    public IReadOnlyList<SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<SymbolInfo>();

    internal sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        SymbolInfo Info);
}
```

- [ ] **Step 4: Add the property to `IWorkspaceService`**

Modify `src/mcpRoslyn/Workspace/IWorkspaceService.cs`. Add the property declaration immediately after `Diagnostics`:

```csharp
/// <summary>
/// Shared symbol index supporting fast semantic_search has-attribute: / returns: / parameter-type: queries.
/// Built during warm-up; queries fall back to walking dirty documents to preserve always-fresh semantics.
/// Throws InvalidOperationException if accessed before LoadAsync completes.
/// </summary>
SymbolIndex SymbolIndex { get; }
```

Full file should be:

```csharp
using Microsoft.CodeAnalysis;
using mcpRoslyn.Contracts;

namespace mcpRoslyn.Workspace;

public interface IWorkspaceService
{
    Task LoadAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default);
    int LoadedProjectCount { get; }

    /// <summary>
    /// Background pre-compilation task kicked off by the most recent <see cref="LoadAsync"/>
    /// or <see cref="ReloadAsync"/>. Production tool code does not await this; it exists for
    /// tests and for future hybrid-reload modes that want to block until projects are hot.
    /// </summary>
    Task WarmupTask { get; }

    /// <summary>
    /// MSBuildWorkspace diagnostics raised during the most recent load/reload —
    /// typically projects that failed to evaluate (missing SDK, missing referenced csproj, etc.).
    /// Cleared at the start of each <see cref="LoadAsync"/>/<see cref="ReloadAsync"/>.
    /// </summary>
    IReadOnlyList<WorkspaceLoadDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Shared symbol index supporting fast semantic_search has-attribute: / returns: / parameter-type: queries.
    /// Built during warm-up; queries fall back to walking dirty documents to preserve always-fresh semantics.
    /// Throws InvalidOperationException if accessed before LoadAsync completes.
    /// </summary>
    SymbolIndex SymbolIndex { get; }
}
```

- [ ] **Step 5: Wire into `WorkspaceService`**

Modify `src/mcpRoslyn/Workspace/WorkspaceService.cs`. Three changes:

**(a) Add field + property** after the `_diagnosticsLock` field block:

```csharp
private SymbolIndex? _symbolIndex;

public SymbolIndex SymbolIndex
    => _symbolIndex ?? throw new InvalidOperationException("Workspace not loaded.");
```

**(b) Construct a fresh `SymbolIndex` at the top of `LoadUnsafeAsync`**, immediately after the diagnostics clear:

```csharp
private async Task LoadUnsafeAsync(CancellationToken ct)
{
    lock (_diagnosticsLock) _diagnostics.Clear();
    _symbolIndex = new SymbolIndex();
    // ... rest unchanged ...
}
```

**(c) Hook `MarkDirty` into `GetFreshSolutionAsync`**. Replace the existing refresh loop body:

```csharp
public async Task<Solution> GetFreshSolutionAsync(CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        if (_solution is null) throw new InvalidOperationException("Workspace not loaded.");

        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents).ToList())
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
            var diskMtime = File.GetLastWriteTimeUtc(doc.FilePath);
            if (_mtimeCache.TryGetValue(doc.Id, out var cachedMtime) && cachedMtime >= diskMtime)
                continue;

            var text = await File.ReadAllTextAsync(doc.FilePath, ct);
            _solution = _solution.WithDocumentText(
                doc.Id,
                Microsoft.CodeAnalysis.Text.SourceText.From(text));
            _mtimeCache[doc.Id] = diskMtime;
            _symbolIndex?.MarkDirty(doc.Id);
        }

        return _solution;
    }
    finally { _gate.Release(); }
}
```

**(d) Update `WarmupAsync` to also build the index after compilations finish**. Replace `WarmupAsync` with:

```csharp
private async Task WarmupAsync(Solution solution, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tasks = solution.Projects.Select(async project =>
    {
        var projectSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var compilation = await project.GetCompilationAsync(ct);
            log.LogInformation(
                "Warmed {Project} in {Elapsed} ms ({DiagCount} diagnostics)",
                project.Name,
                projectSw.ElapsedMilliseconds,
                compilation?.GetDiagnostics(ct).Length ?? 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Warm-up failed for project {Project}", project.Name);
        }
    });
    await Task.WhenAll(tasks);
    log.LogInformation(
        "Warm-up complete: {ProjectCount} projects in {Elapsed} ms",
        solution.Projects.Count(),
        sw.ElapsedMilliseconds);

    // Index build — runs after all compilations are cached so per-project
    // walks reuse the warmed state. Failures isolated per-project; one bad
    // project doesn't poison the whole index.
    if (_symbolIndex is not null)
    {
        var indexSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _symbolIndex.BuildAsync(solution, ct);
            log.LogInformation(
                "Symbol index built in {Elapsed} ms",
                indexSw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Symbol index build failed; semantic_search will fall back to live walks");
        }
    }
}
```

- [ ] **Step 6: Build + run the test**

Run: `dotnet build "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" -c Release`

Expected: 0 errors.

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SymbolIndexTests" -c Release`

Expected: 1 test, PASS.

- [ ] **Step 7: Run the full `WorkspaceServiceTests` to confirm no regression**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~WorkspaceServiceTests" -c Release`

Expected: 7 tests, all PASS (the existing tests in that class — load, mtime refresh, warmup populates compilations, returns-before-warmup, clean fixture empty diagnostics, broken solution diagnostics, reload clears diagnostics).

- [ ] **Step 8: Commit + push**

```
git add src/mcpRoslyn/Workspace/SymbolIndex.cs src/mcpRoslyn/Workspace/IWorkspaceService.cs src/mcpRoslyn/Workspace/WorkspaceService.cs tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "feat: SymbolIndex skeleton wired into WorkspaceService lifecycle"
git push origin main
```

---

## Task 2: `BuildAsync` populates `_byAttribute`

Implement the attribute branch of the index build. Verify against the fixture's `[MyMarker]` attribute usages (`MarkedType` + `MarkedMethod`).

**Files:**
- Modify: `src/mcpRoslyn/Workspace/SymbolIndex.cs`
- Modify: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/mcpRoslyn.Tests/SymbolIndexTests.cs` (inside the `SymbolIndexTests` class):

```csharp
[Test]
public async Task QueryAttribute_returns_fixture_MyMarker_matches()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

    await sut.LoadAsync();
    await sut.WarmupTask;

    var solution = await sut.GetFreshSolutionAsync();

    // Fully-qualified form
    var qualified = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", solution);
    qualified.Should().HaveCount(2);
    qualified.Select(m => m.Name).Should().BeEquivalentTo("MarkedType", "MarkedMethod");

    // Display-string form (without the Attribute suffix is NOT supported today;
    // MatchesTypeName matches on display string OR fully-qualified metadata name).
    // The display string of an attribute class is "TestLib.MyMarkerAttribute".
    // We do NOT test "MyMarker" alone here — that's a future enhancement, not the spec.
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryAttribute_returns_fixture_MyMarker_matches" -c Release`

Expected: FAIL — `Expected qualified to contain 2 item(s), but found 0`.

- [ ] **Step 3: Implement the attribute-indexing branch of `BuildAsync` + helpers**

Replace the entire contents of `src/mcpRoslyn/Workspace/SymbolIndex.cs` with:

```csharp
using Microsoft.CodeAnalysis;
using mcpRoslyn.Contracts;

namespace mcpRoslyn.Workspace;

internal sealed class SymbolIndex
{
    private readonly Dictionary<string, List<IndexedSymbol>> _byAttribute = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byReturnType = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byParameterType = new();
    private readonly HashSet<DocumentId> _dirty = new();
    private readonly object _gate = new();

    public async Task BuildAsync(Solution solution, CancellationToken ct = default)
    {
        var tasks = solution.Projects.Select(async project =>
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) return;

            foreach (var sym in WalkAllSymbols(compilation))
            {
                ct.ThrowIfCancellationRequested();
                var declaringDocs = sym.Locations
                    .Where(l => l.IsInSource && l.SourceTree is not null)
                    .Select(l => solution.GetDocumentId(l.SourceTree))
                    .Where(id => id is not null)
                    .Cast<DocumentId>()
                    .ToHashSet();
                if (declaringDocs.Count == 0) continue;

                var info = RoslynHelpers.ToSymbolInfo(sym);
                var entry = new IndexedSymbol(
                    SymbolId: !string.IsNullOrEmpty(info.SymbolId) ? info.SymbolId : sym.ToDisplayString(),
                    DeclaringDocs: declaringDocs,
                    Info: info);

                foreach (var attr in sym.GetAttributes())
                {
                    foreach (var key in CandidateKeys(attr.AttributeClass))
                        Add(_byAttribute, key, entry);
                }
            }
        });
        await Task.WhenAll(tasks);
    }

    public void MarkDirty(DocumentId documentId)
    {
        lock (_gate) _dirty.Add(documentId);
    }

    public IReadOnlyList<SymbolInfo> QueryAttribute(string target, Solution currentSolution, CancellationToken ct = default)
    {
        List<IndexedSymbol> bucket;
        HashSet<DocumentId> dirty;
        lock (_gate)
        {
            bucket = _byAttribute.TryGetValue(target, out var list) ? new(list) : new();
            dirty = new(_dirty);
        }

        return MergeWithDirtyWalk(
            bucket, dirty, currentSolution,
            predicate: sym => sym.GetAttributes().Any(a => MatchesTypeName(a.AttributeClass, target)),
            ct);
    }

    public IReadOnlyList<SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<SymbolInfo>();   // Task 3 fills this in

    public IReadOnlyList<SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<SymbolInfo>();   // Task 3 fills this in

    // ---------- Helpers ----------

    private void Add(Dictionary<string, List<IndexedSymbol>> dict, string key, IndexedSymbol entry)
    {
        lock (_gate)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<IndexedSymbol>();
                dict[key] = list;
            }
            list.Add(entry);
        }
    }

    private List<SymbolInfo> MergeWithDirtyWalk(
        List<IndexedSymbol> bucket,
        HashSet<DocumentId> dirty,
        Solution currentSolution,
        Func<ISymbol, bool> predicate,
        CancellationToken ct)
    {
        var results = new List<SymbolInfo>();
        var seen = new HashSet<string>();

        foreach (var entry in bucket)
        {
            if (entry.DeclaringDocs.Overlaps(dirty)) continue;
            if (seen.Add(entry.SymbolId)) results.Add(entry.Info);
        }

        foreach (var docId in dirty)
        {
            var doc = currentSolution.GetDocument(docId);
            if (doc is null) continue;

            // Synchronous walk over this document's syntax + semantic model.
            // Same predicate is applied to every symbol whose primary location is in this doc.
            var semantic = doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
            if (semantic is null) continue;

            foreach (var sym in WalkDocumentSymbols(semantic))
            {
                if (!predicate(sym)) continue;
                var info = RoslynHelpers.ToSymbolInfo(sym);
                var key = !string.IsNullOrEmpty(info.SymbolId) ? info.SymbolId : sym.ToDisplayString();
                if (seen.Add(key)) results.Add(info);
            }
        }

        return results;
    }

    private static IEnumerable<string> CandidateKeys(ITypeSymbol? type)
    {
        if (type is null) yield break;
        var display = type.ToDisplayString();
        yield return display;
        var ns = type.ContainingNamespace?.ToDisplayString();
        var metadata = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";
        if (metadata != display) yield return metadata;
    }

    private static bool MatchesTypeName(ITypeSymbol? type, string target)
    {
        if (type is null) return false;
        if (type.ToDisplayString() == target) return true;
        var ns = type.ContainingNamespace?.ToDisplayString();
        var metadata = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";
        return metadata == target;
    }

    private static IEnumerable<ISymbol> WalkAllSymbols(Compilation compilation)
    {
        foreach (var sym in WalkNamespace(compilation.GlobalNamespace)) yield return sym;

        static IEnumerable<ISymbol> WalkNamespace(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child)
                    foreach (var nested in WalkNamespace(child)) yield return nested;
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in WalkType(type)) yield return nested;
                }
            }
        }

        static IEnumerable<ISymbol> WalkType(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                yield return member;
                if (member is INamedTypeSymbol nested)
                    foreach (var inner in WalkType(nested)) yield return inner;
            }
        }
    }

    private static IEnumerable<ISymbol> WalkDocumentSymbols(SemanticModel semantic)
    {
        var root = semantic.SyntaxTree.GetRoot();
        foreach (var node in root.DescendantNodesAndSelf())
        {
            var sym = semantic.GetDeclaredSymbol(node);
            if (sym is not null) yield return sym;
        }
    }

    internal sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        SymbolInfo Info);
}
```

- [ ] **Step 4: Build + run the new test**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryAttribute_returns_fixture_MyMarker_matches" -c Release`

Expected: PASS.

- [ ] **Step 5: Run all SymbolIndexTests**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SymbolIndexTests" -c Release`

Expected: 2 tests, all PASS.

- [ ] **Step 6: Commit + push**

```
git add src/mcpRoslyn/Workspace/SymbolIndex.cs tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "feat: SymbolIndex.QueryAttribute backed by per-project parallel build"
git push origin main
```

---

## Task 3: `QueryReturnType` + `QueryParameterType`

Add the return-type and parameter-type branches to `BuildAsync` and implement the two query methods. Same pattern as `QueryAttribute`.

**Files:**
- Modify: `src/mcpRoslyn/Workspace/SymbolIndex.cs`
- Modify: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

```csharp
[Test]
public async Task QueryReturnType_int_finds_partial_class_methods()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

    await sut.LoadAsync();
    await sut.WarmupTask;

    var solution = await sut.GetFreshSolutionAsync();
    var matches = sut.SymbolIndex.QueryReturnType("int", solution);

    matches.Should().HaveCountGreaterThanOrEqualTo(2);
    matches.Select(m => m.Name).Should().Contain(new[] { "Foo", "Bar" });
}

[Test]
public async Task QueryParameterType_string_finds_both_Greet_methods()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);

    await sut.LoadAsync();
    await sut.WarmupTask;

    var solution = await sut.GetFreshSolutionAsync();
    var matches = sut.SymbolIndex.QueryParameterType("string", solution);

    // IGreeter.Greet, EnglishGreeter.Greet, DutchGreeter.Greet — 3 methods, all
    // taking a string parameter. May also include the implicit `Greet` on the
    // interface itself plus the two implementations.
    matches.Should().HaveCountGreaterThanOrEqualTo(3);
    matches.Select(m => m.Name).Distinct().Should().Contain("Greet");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryReturnType_int_finds_partial_class_methods|FullyQualifiedName~QueryParameterType_string_finds_both_Greet_methods" -c Release`

Expected: 2 tests FAIL — both currently return empty.

- [ ] **Step 3: Add return-type + parameter-type indexing to `BuildAsync`**

In `src/mcpRoslyn/Workspace/SymbolIndex.cs`, find the `foreach (var attr in sym.GetAttributes())` block inside the `BuildAsync` per-project task. Immediately AFTER that block (still inside the `foreach (var sym in ...)` loop), add:

```csharp
                if (sym is IMethodSymbol method)
                {
                    foreach (var key in CandidateKeys(method.ReturnType))
                        Add(_byReturnType, key, entry);

                    foreach (var param in method.Parameters)
                    {
                        foreach (var key in CandidateKeys(param.Type))
                            Add(_byParameterType, key, entry);
                    }
                }
```

- [ ] **Step 4: Implement `QueryReturnType` and `QueryParameterType`**

In the same file, replace the two stub methods with:

```csharp
    public IReadOnlyList<SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
    {
        List<IndexedSymbol> bucket;
        HashSet<DocumentId> dirty;
        lock (_gate)
        {
            bucket = _byReturnType.TryGetValue(target, out var list) ? new(list) : new();
            dirty = new(_dirty);
        }

        return MergeWithDirtyWalk(
            bucket, dirty, currentSolution,
            predicate: sym => sym is IMethodSymbol m && MatchesTypeName(m.ReturnType, target),
            ct);
    }

    public IReadOnlyList<SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
    {
        List<IndexedSymbol> bucket;
        HashSet<DocumentId> dirty;
        lock (_gate)
        {
            bucket = _byParameterType.TryGetValue(target, out var list) ? new(list) : new();
            dirty = new(_dirty);
        }

        return MergeWithDirtyWalk(
            bucket, dirty, currentSolution,
            predicate: sym => sym is IMethodSymbol m && m.Parameters.Any(p => MatchesTypeName(p.Type, target)),
            ct);
    }
```

- [ ] **Step 5: Run both tests**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryReturnType_int_finds_partial_class_methods|FullyQualifiedName~QueryParameterType_string_finds_both_Greet_methods" -c Release`

Expected: 2 tests PASS.

- [ ] **Step 6: Run all SymbolIndexTests**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SymbolIndexTests" -c Release`

Expected: 4 tests, all PASS.

- [ ] **Step 7: Commit + push**

```
git add src/mcpRoslyn/Workspace/SymbolIndex.cs tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "feat: SymbolIndex covers returns: and parameter-type:"
git push origin main
```

---

## Task 4: Dirty walk — added attribute is reflected in next query

Verify the dirty walk picks up changes. Mutate `Partial1.cs` to add `[MyMarker]` to `PartialThing`. After `GetFreshSolutionAsync` calls `MarkDirty`, the next `QueryAttribute("TestLib.MyMarkerAttribute", ...)` should include `PartialThing` even though the original index build didn't see the attribute.

**Files:**
- Modify: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

No production code changes — Task 1 already wired `MarkDirty` into `GetFreshSolutionAsync`, and Tasks 2/3's `MergeWithDirtyWalk` already runs the predicate against dirty docs.

- [ ] **Step 1: Write the failing test**

Append to `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

```csharp
[Test]
public async Task QueryAttribute_dirty_walk_picks_up_newly_added_attribute()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
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
            "the dirty walk should have surfaced the newly-attributed partial class");
    }
    finally
    {
        File.WriteAllText(doc.FilePath!, backup);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryAttribute_dirty_walk_picks_up_newly_added_attribute" -c Release`

Expected: PASS. (Production code already implements the dirty walk via `MergeWithDirtyWalk`.)

If FAIL: re-check that `GetFreshSolutionAsync` is calling `_symbolIndex?.MarkDirty(doc.Id)` from Task 1, and that `MergeWithDirtyWalk` is iterating `dirty` — both should be in place from Tasks 1 and 2.

- [ ] **Step 3: Commit + push**

```
git add tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "test: dirty walk picks up newly-added attribute via GetFreshSolutionAsync"
git push origin main
```

---

## Task 5: Dirty walk — removed attribute is excluded

Mutate the fixture to remove `[MyMarker]` from `MarkedType` (in `MyAttribute.cs`). After refresh + query, `MarkedType` should NOT appear in results — the cached entry is filtered out by `Overlaps(dirty)`, and the dirty walk doesn't re-add it because the attribute is gone.

**Files:**
- Modify: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

```csharp
[Test]
public async Task QueryAttribute_dirty_walk_excludes_removed_attribute()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
    await sut.LoadAsync();
    await sut.WarmupTask;

    var solution = await sut.GetFreshSolutionAsync();
    var doc = solution.Projects
        .SelectMany(p => p.Documents)
        .First(d => d.Name == "MyAttribute.cs");
    var backup = File.ReadAllText(doc.FilePath!);

    try
    {
        // Strip the [MyMarker] above MarkedType. The text in the fixture is
        // "[MyMarker]\npublic class MarkedType"; replacing just the attribute line
        // leaves the class intact.
        var mutated = backup.Replace("[MyMarker]\npublic class MarkedType", "public class MarkedType");
        File.WriteAllText(doc.FilePath!, mutated);
        File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));

        var refreshed = await sut.GetFreshSolutionAsync();
        var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", refreshed);

        matches.Should().NotContain(m => m.Name == "MarkedType",
            "the dirty walk should exclude the entry whose attribute was removed");
        // MarkedMethod is still attributed (its [MyMarker] line was not touched).
        matches.Should().Contain(m => m.Name == "MarkedMethod");
    }
    finally
    {
        File.WriteAllText(doc.FilePath!, backup);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~QueryAttribute_dirty_walk_excludes_removed_attribute" -c Release`

Expected: PASS.

- [ ] **Step 3: Commit + push**

```
git add tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "test: dirty walk excludes entry whose attribute was removed"
git push origin main
```

---

## Task 6: Reload clears dirty + partial-class DeclaringDocs

Two related tests:

1. After mutating a file and refreshing (so `_dirty` is non-empty), calling `ReloadAsync` constructs a fresh `SymbolIndex` and the dirty set is gone. The new index reflects the on-disk state at reload time.
2. Partial classes: an `IndexedSymbol` for a partial type lists ALL declaring documents in `DeclaringDocs`. Mutating ANY of those documents (even one without the attribute declaration) correctly invalidates the cached entry.

**Files:**
- Modify: `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`

- [ ] **Step 1: Write the reload test**

Append to `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

```csharp
[Test]
public async Task ReloadAsync_constructs_fresh_index_with_empty_dirty_set()
{
    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
    await sut.LoadAsync();
    await sut.WarmupTask;

    var doc = (await sut.GetFreshSolutionAsync()).Projects
        .SelectMany(p => p.Documents)
        .First(d => d.Name == "EnglishGreeter.cs");
    var backup = File.ReadAllText(doc.FilePath!);

    try
    {
        // Mark the doc dirty by mutating + refreshing.
        File.WriteAllText(doc.FilePath!, backup + "\n// touched\n");
        File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddSeconds(1));
        await sut.GetFreshSolutionAsync();

        // Capture the index reference before reload — it should be replaced.
        var indexBefore = sut.SymbolIndex;

        await sut.ReloadAsync();
        await sut.WarmupTask;

        var indexAfter = sut.SymbolIndex;
        indexAfter.Should().NotBeSameAs(indexBefore,
            "ReloadAsync should construct a fresh SymbolIndex");

        // A query on the freshly-loaded index should NOT trigger the dirty walk
        // (the new index has no dirty entries). We can't observe _dirty directly,
        // but we can sanity-check that querying still returns expected attribute matches.
        var solution = await sut.GetFreshSolutionAsync();
        var matches = indexAfter.QueryAttribute("TestLib.MyMarkerAttribute", solution);
        matches.Should().HaveCount(2);
    }
    finally
    {
        File.WriteAllText(doc.FilePath!, backup);
    }
}
```

- [ ] **Step 2: Write the partial-class test**

Append:

```csharp
[Test]
public async Task QueryAttribute_partial_class_entry_lists_all_declaring_docs()
{
    // Setup: mutate Partial1.cs to add [MyMarker] to PartialThing, then RELOAD
    // so the index sees the attribute at build time. After reload, the indexed
    // entry for PartialThing should have BOTH Partial1.cs and Partial2.cs in
    // its DeclaringDocs set (partial type spans both files).
    //
    // Then mutate Partial2.cs (the file WITHOUT the attribute declaration). The
    // cached entry should be invalidated (its DeclaringDocs intersects dirty),
    // and the dirty walk on Partial2.cs should re-find PartialThing because
    // the partial type still has the attribute via Partial1.cs.

    var options = new McpRoslynOptions { SolutionPath = FixturePaths.TestSolutionPath };
    var sut = new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
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
        // Stage 1: write the [MyMarker] into Partial1.cs and RELOAD
        var partial1Mutated = partial1Backup.Replace(
            "public partial class PartialThing",
            "[MyMarker]\npublic partial class PartialThing");
        File.WriteAllText(partial1.FilePath!, partial1Mutated);
        File.SetLastWriteTimeUtc(partial1.FilePath!, DateTime.UtcNow.AddSeconds(1));

        await sut.ReloadAsync();
        await sut.WarmupTask;

        // Stage 2: touch Partial2.cs (mtime bump, append a harmless comment)
        File.WriteAllText(partial2.FilePath!, partial2Backup + "\n// touched\n");
        File.SetLastWriteTimeUtc(partial2.FilePath!, DateTime.UtcNow.AddSeconds(2));
        var afterPartial2Touch = await sut.GetFreshSolutionAsync();

        // Query: PartialThing should STILL appear via the dirty walk on Partial2.cs
        var matches = sut.SymbolIndex.QueryAttribute("TestLib.MyMarkerAttribute", afterPartial2Touch);
        matches.Should().Contain(m => m.Name == "PartialThing",
            "DeclaringDocs should span both partial files; touching Partial2.cs should " +
            "invalidate the cached entry, and the dirty walk on Partial2.cs should re-find PartialThing");
    }
    finally
    {
        File.WriteAllText(partial1.FilePath!, partial1Backup);
        File.WriteAllText(partial2.FilePath!, partial2Backup);
    }
}
```

- [ ] **Step 3: Run both new tests**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~ReloadAsync_constructs_fresh_index|FullyQualifiedName~QueryAttribute_partial_class_entry" -c Release`

Expected: 2 tests PASS. If either fails:

- The reload test may fail if `LoadUnsafeAsync` doesn't create a new `SymbolIndex` instance on each call. Re-check Task 1's `LoadUnsafeAsync` change — `_symbolIndex = new SymbolIndex();` must be at the top, before any cancellable awaits.
- The partial-class test relies on Roslyn's `GetSemanticModelAsync` for `Partial2.cs` returning a model whose declared-symbol walk yields `PartialThing`. The partial type's `PartialThing` symbol is reachable from EITHER syntax tree (Roslyn merges partials at the symbol level). `WalkDocumentSymbols` calls `GetDeclaredSymbol(node)` on syntax nodes; partial class declarations exist as `ClassDeclarationSyntax` in both files, and each yields the SAME merged `INamedTypeSymbol`. So the dirty walk on Partial2.cs DOES re-find `PartialThing`.

- [ ] **Step 4: Run full SymbolIndexTests + WorkspaceServiceTests**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SymbolIndexTests|FullyQualifiedName~WorkspaceServiceTests" -c Release`

Expected: 7 + 7 = 14 tests PASS (6 new SymbolIndexTests, 1 still-passing skeleton test = 7 in SymbolIndexTests; 7 in WorkspaceServiceTests).

- [ ] **Step 5: Commit + push**

```
git add tests/mcpRoslyn.Tests/SymbolIndexTests.cs
git commit -m "test: reload constructs fresh index; partial-class DeclaringDocs span all files"
git push origin main
```

---

## Task 7: Integrate `SymbolIndex` into `SemanticSearchTool`

Replace the three slow `case` blocks in `SemanticSearchTool` with delegations to `SymbolIndex`. Remove the now-redundant `WalkAllSymbols` and `MatchesTypeName` static helpers. `derives-from:` / `implements:` / invalid-pattern paths are untouched.

The existing `SemanticSearchToolTests` (5 patterns + 1 invalid) is the regression check — they all must still pass.

**Files:**
- Modify: `src/mcpRoslyn/Tools/SemanticSearchTool.cs`

- [ ] **Step 1: Replace the contents of `SemanticSearchTool.cs`**

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record SemanticSearchResult(IReadOnlyList<Contracts.SymbolInfo> Matches);

[McpServerToolType]
internal sealed class SemanticSearchTool(IWorkspaceService ws, ILogger<SemanticSearchTool> log)
    : ToolBase(ws, log)
{
    [McpServerTool(Name = "semantic_search")]
    [Description("Pattern queries Roslyn can answer but Grep cannot. Patterns: " +
                 "derives-from:Namespace.Type, implements:Namespace.IInterface, " +
                 "has-attribute:Namespace.MyAttribute, returns:Namespace.Type, " +
                 "parameter-type:Namespace.Type. Type can also be a primitive alias like 'int' or 'string'.")]
    public Task<Contracts.ToolResult<SemanticSearchResult>> InvokeAsync(
        string pattern,
        CancellationToken ct)
        => ExecuteAsync(async ct2 =>
        {
            var colonIdx = pattern.IndexOf(':');
            if (colonIdx <= 0)
                return Contracts.ToolResult<SemanticSearchResult>.Fail(
                    "INVALID_PATTERN", $"Pattern must be of form 'kind:target': {pattern}");

            var kind = pattern[..colonIdx];
            var target = pattern[(colonIdx + 1)..];
            var solution = await Workspace.GetFreshSolutionAsync(ct2);

            switch (kind)
            {
                case "derives-from":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var derived = await SymbolFinder.FindDerivedClassesAsync(named, solution, transitive: true, projects: null, ct2);
                    var matches = new List<Contracts.SymbolInfo>();
                    var dedup = new HashSet<string>();
                    foreach (var d in derived) AddIfNew(matches, dedup, d);
                    return Contracts.ToolResult<SemanticSearchResult>.Ok(new SemanticSearchResult(matches));
                }
                case "implements":
                {
                    var targetSym = await FindTypeByDisplayNameAsync(solution, target, ct2);
                    if (targetSym is not INamedTypeSymbol named)
                        return Contracts.ToolResult<SemanticSearchResult>.Fail(
                            "SYMBOL_NOT_FOUND", $"Target type not found: {target}");
                    var impls = await SymbolFinder.FindImplementationsAsync(named, solution, transitive: true, projects: null, ct2);
                    var matches = new List<Contracts.SymbolInfo>();
                    var dedup = new HashSet<string>();
                    foreach (var i in impls) AddIfNew(matches, dedup, i);
                    return Contracts.ToolResult<SemanticSearchResult>.Ok(new SemanticSearchResult(matches));
                }
                case "has-attribute":
                    return Contracts.ToolResult<SemanticSearchResult>.Ok(
                        new SemanticSearchResult(Workspace.SymbolIndex.QueryAttribute(target, solution, ct2)));
                case "returns":
                    return Contracts.ToolResult<SemanticSearchResult>.Ok(
                        new SemanticSearchResult(Workspace.SymbolIndex.QueryReturnType(target, solution, ct2)));
                case "parameter-type":
                    return Contracts.ToolResult<SemanticSearchResult>.Ok(
                        new SemanticSearchResult(Workspace.SymbolIndex.QueryParameterType(target, solution, ct2)));
                default:
                    return Contracts.ToolResult<SemanticSearchResult>.Fail(
                        "INVALID_PATTERN", $"Unknown pattern kind: {kind}");
            }
        }, ct);

    private static void AddIfNew(List<Contracts.SymbolInfo> matches, HashSet<string> dedup, ISymbol sym)
    {
        var info = RoslynHelpers.ToSymbolInfo(sym);
        if (dedup.Add(info.SymbolId.Length > 0 ? info.SymbolId : sym.ToDisplayString()))
            matches.Add(info);
    }

    private static async Task<ISymbol?> FindTypeByDisplayNameAsync(
        Solution solution, string target, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var sym = compilation.GetTypeByMetadataName(target);
            if (sym is not null) return sym;
        }
        return null;
    }
}
```

Removed: `WalkAllSymbols`, `MatchesTypeName`, and the inline walks inside the three patterns. They live in `SymbolIndex` now.

- [ ] **Step 2: Build**

Run: `dotnet build "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" -c Release`

Expected: 0 errors.

- [ ] **Step 3: Run `SemanticSearchToolTests` — regression check**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" --filter "FullyQualifiedName~SemanticSearchToolTests" -c Release`

Expected: all existing test cases PASS — 5 parameterized cases (one per pattern) + 1 invalid-pattern test = 6 tests.

If FAIL: the most likely cause is that the index's `MatchesTypeName` / `CandidateKeys` don't precisely match the v1 walk's matching behavior. Re-check both helpers against the original `SemanticSearchTool` (commit `93d918a` for the v1 baseline).

- [ ] **Step 4: Run full test suite**

Run: `dotnet test "C:/Projects/mcpRoslyn/mcpRoslyn.slnx" -c Release`

Expected: all PASS. Suite size after this task: 44 tests (38 prior + 6 new SymbolIndexTests).

- [ ] **Step 5: Commit + push**

```
git add src/mcpRoslyn/Tools/SemanticSearchTool.cs
git commit -m "feat: route has-attribute/returns/parameter-type via SymbolIndex"
git push origin main
```

---

## Task 8: Documentation closeout

Update `TODO.md` to mark the attribute index done; update `ARCHITECTURE.md` to mention `SymbolIndex`; refresh `SESSION_HANDOFF.md`.

**Files:**
- Modify: `TODO.md`
- Modify: `ARCHITECTURE.md`
- Modify: `SESSION_HANDOFF.md`

- [ ] **Step 1: Mark the index done in `TODO.md`**

In `TODO.md`, find the `semantic_search attribute walk is O(symbols)` bullet under "v1.1 follow-ups" and replace its content with:

```markdown
- [x] ~~**`semantic_search` attribute walk is O(symbols).**~~ Shipped. New `SymbolIndex` (built in parallel during warm-up) backs `has-attribute:` / `returns:` / `parameter-type:` with O(matches) lookups; always-fresh semantics preserved via per-query dirty-doc walk. Design: [`docs/plans/2026-05-16-attribute-index-design.md`](docs/plans/2026-05-16-attribute-index-design.md).
```

- [ ] **Step 2: Add `SymbolIndex` to the architecture summary in `ARCHITECTURE.md`**

In `ARCHITECTURE.md`, find the `## WorkspaceService` section. Append a paragraph at the end of that section:

```markdown
A sibling `SymbolIndex` (built during warm-up, owned by `WorkspaceService`) backs the `has-attribute:`, `returns:`, and `parameter-type:` patterns of `semantic_search`. Three dictionaries (keyed by attribute / return-type / parameter-type display string and metadata name) populated by a parallel-per-project walk after compilations finish. Queries hit the dictionary in O(matches); always-fresh semantics preserved via a dirty-doc set populated whenever `GetFreshSolutionAsync` calls `WithDocumentText` — the query merges indexed entries (filtered to exclude dirty-doc declarations) with a fresh walk of just the dirty documents.
```

- [ ] **Step 3: Refresh `SESSION_HANDOFF.md`**

Replace the existing `SESSION_HANDOFF.md` content with one updated for the v1.2 shipping state. Use this template, filling in the actual commit shas after Task 7's commit:

```markdown
# Session Handoff

**Last updated:** 2026-05-16 (end of SymbolIndex shipping session)

## Where things stand

- **v1.2 shipped:** `SymbolIndex` cuts `semantic_search has-attribute:` (and `returns:` / `parameter-type:`) from ~7.7 s to sub-100 ms on duetGPT-sized solutions. Always-fresh semantics preserved via dirty-doc walk.
- Branch `main`, working tree clean, fully pushed to `origin`.
- Tests: 44 passing (was 38 before this session, +6 new in `SymbolIndexTests`).

## What shipped this session

(Fill in actual commit shas after Task 7 lands.)

- `<sha>` feat: SymbolIndex skeleton wired into WorkspaceService lifecycle
- `<sha>` feat: SymbolIndex.QueryAttribute backed by per-project parallel build
- `<sha>` feat: SymbolIndex covers returns: and parameter-type:
- `<sha>` test: dirty walk picks up newly-added attribute via GetFreshSolutionAsync
- `<sha>` test: dirty walk excludes entry whose attribute was removed
- `<sha>` test: reload constructs fresh index; partial-class DeclaringDocs span all files
- `<sha>` feat: route has-attribute/returns/parameter-type via SymbolIndex
- `<sha>` docs: TODO + ARCHITECTURE + handoff for v1.2

## What's next

All v1.1 acceptance-driven follow-ups are now closed. Remaining open items in `TODO.md`:

1. **Re-measure on duetGPT.** Republish exe, run a fresh Claude Code session, time `has-attribute:McpServerToolType` and `find_implementations` again. v1.2 prediction: `has-attribute:` drops from 7.7 s to sub-100 ms; the `find_implementations` "regression" can be re-checked too.
2. **Nice-to-haves spotted along the way** (see TODO.md):
   - Extract `ProjectName` from `WorkspaceLoadDiagnostic.Message`.
   - Migrate off the obsolete `Workspace.WorkspaceFailed` to `RegisterWorkspaceFailedHandler`.
3. **Real-session validation.** Use mcpRoslyn in one duetGPT feature task; capture friction.

## Useful commands

(unchanged from prior handoff — see `mcpRoslyn.slnx`, `dotnet test`, `dotnet publish`, `--log-file` flag)
```

- [ ] **Step 4: Commit + push**

```
git add TODO.md ARCHITECTURE.md SESSION_HANDOFF.md
git commit -m "docs: close attribute-index TODO; document SymbolIndex in ARCHITECTURE"
git push origin main
```

---

## Task summary

| # | Task | Type | Commits |
|---|---|---|---|
| 1 | Skeleton — types + interface property + lifecycle wiring | Foundation (TDD) | 1 |
| 2 | `QueryAttribute` + per-project parallel `BuildAsync` | Feature (TDD) | 1 |
| 3 | `QueryReturnType` + `QueryParameterType` | Feature (TDD) | 1 |
| 4 | Dirty walk — added attribute | Test | 1 |
| 5 | Dirty walk — removed attribute | Test | 1 |
| 6 | Reload + partial-class | Test | 1 |
| 7 | `SemanticSearchTool` integration | Refactor + regression | 1 |
| 8 | Docs closeout | Docs | 1 |

**Estimated commit count:** 8. **Estimated tests after Task 7:** 44 total (38 prior + 6 new in `SymbolIndexTests`).

---

## Risks & traps

- **`_symbolIndex` field nullability.** The field is `SymbolIndex?` and set in `LoadUnsafeAsync` before warm-up kicks off, but the public property throws if accessed before load. Some test paths construct a `WorkspaceService` and immediately query `SymbolIndex` without awaiting load — those will throw. Test plan handles this correctly by always awaiting `LoadAsync` first.
- **Concurrent `BuildAsync` and `MarkDirty` calls.** `LoadUnsafeAsync` constructs a new `SymbolIndex` before kicking off the warm-up `Task.Run`. If `GetFreshSolutionAsync` fires before warm-up completes, `MarkDirty` runs against an in-flight `BuildAsync`. Both methods use `_gate` so the operation is safe; the dirty doc is recorded even if its entries don't exist in the index yet (and the query-time dirty walk picks it up regardless). No race.
- **`GetSemanticModelAsync` in the dirty walk uses `.GetAwaiter().GetResult()`.** Synchronous over async is generally an anti-pattern but `SemanticModel` retrieval is cheap (compilation already cached) and the surrounding `MergeWithDirtyWalk` is intentionally sync (we hold `_gate` snapshot data on the heap, not across awaits). If a future change makes this hot, switch to an async `MergeWithDirtyWalkAsync`.
- **`AddIfNew` semantics in `SemanticSearchTool` differ subtly from index dedup.** The current `AddIfNew` (kept in `SemanticSearchTool` for `derives-from:` / `implements:`) uses `info.SymbolId.Length > 0 ? info.SymbolId : sym.ToDisplayString()` as the key. The index does the SAME thing in `BuildAsync` (when constructing the `IndexedSymbol`) AND in `MergeWithDirtyWalk` (when deduping). Consistent.
- **Existing `SemanticSearchToolTests` count expectations.** The v1 tests use `[TestCase(... expected count)]` with specific counts (2 for `derives-from:Shape`, 2 for `implements:IGreeter`, 2 for `has-attribute:MyMarkerAttribute`, 2 for `returns:int`, 2 for `parameter-type:string`). The index must return EXACTLY 2 for each — same as today's walk. If the walk in `SymbolIndex` discovers more symbols than `SemanticSearchTool`'s old walk did (e.g., interface members vs implementations), counts could shift. Mitigation: `WalkAllSymbols` is moved verbatim from `SemanticSearchTool`, so the symbol set is identical. The dedup logic is also identical. Counts should match exactly.
- **Partial-class test reliability.** `GetSemanticModelAsync(partial2.cs)` returning a model whose declared-symbol walk yields `PartialThing` depends on Roslyn's syntax-tree → semantic-model resolution. This has been stable for ~7 years of Roslyn releases; very low risk.
