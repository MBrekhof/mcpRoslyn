# SymbolIndex (semantic_search perf) — Design

- **Date:** 2026-05-16
- **Status:** Approved, pre-implementation
- **Driver:** Last open v1.1 perf item — `semantic_search has-attribute:` measured at 7.7 s on duetGPT in [`docs/acceptance/2026-05-16-v1.1-warmup-acceptance.md`](../acceptance/2026-05-16-v1.1-warmup-acceptance.md). `returns:` and `parameter-type:` use the same O(symbols) walk and would be similarly slow on any large XAF or web codebase (wlncentral: 462 .cs files across 8 projects; duetGPT: 598 / 4).

## Goal

Eliminate the per-call symbol walk for the three slow `semantic_search` patterns (`has-attribute:`, `returns:`, `parameter-type:`). Build a shared `SymbolIndex` once during warm-up; query it in O(matches). Preserve today's always-fresh semantics by re-walking only the documents that have changed since the index was built.

## Non-goals

- Not touching `derives-from:` / `implements:` — already fast via Roslyn's `FindDerivedClassesAsync` / `FindImplementationsAsync`.
- Not changing matching semantics. `parameter-type:IList<T>` continues to NOT match `parameter-type:IList<string>` — generic-instantiation reform is a separate concern.
- Not persisting the index across process restarts. Roslyn compilations don't survive either; same lifetime.
- Not building composite indices (e.g. "has-attribute AND returns"). YAGNI.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│ WorkspaceService                                             │
│  ├─ _solution (Roslyn Solution)                              │
│  ├─ _warmupTask                                              │
│  ├─ _diagnostics  (existing, from v1.1)                      │
│  └─ _symbolIndex ◄── new: rebuilt per Load/Reload            │
│         ▲                                                    │
│         │ MarkDirty(documentId) from GetFreshSolutionAsync   │
│         │   when mtime walk replaces a document              │
└─────────┼────────────────────────────────────────────────────┘
          │
          ▼
┌──────────────────────────────────────────────────────────────┐
│ SymbolIndex                                                  │
│  ├─ _byAttribute     : Dict<string, List<IndexedSymbol>>     │
│  ├─ _byReturnType    : Dict<string, List<IndexedSymbol>>     │
│  ├─ _byParameterType : Dict<string, List<IndexedSymbol>>     │
│  └─ _dirty           : HashSet<DocumentId>                   │
│                                                              │
│  BuildAsync(Solution)     — called from WarmupAsync          │
│  MarkDirty(DocumentId)    — called from GetFreshSolutionAsync│
│  QueryAttribute(target, currentSolution) → IndexedSymbol[]   │
│  QueryReturnType(target, currentSolution) → IndexedSymbol[]  │
│  QueryParameterType(target, currentSolution) → IndexedSymbol[]│
└──────────────────────────────────────────────────────────────┘
          ▲
          │ queries via IWorkspaceService.SymbolIndex
          │
┌─────────┴────────────────────────────────────────────────────┐
│ SemanticSearchTool                                           │
│  has-attribute:   → SymbolIndex.QueryAttribute               │
│  returns:         → SymbolIndex.QueryReturnType              │
│  parameter-type:  → SymbolIndex.QueryParameterType           │
│  derives-from:    → unchanged (Roslyn API already fast)      │
│  implements:      → unchanged                                │
└──────────────────────────────────────────────────────────────┘
```

## Data types

```csharp
internal sealed record IndexedSymbol(
    string SymbolId,                        // Roslyn DocumentationCommentId
    IReadOnlySet<DocumentId> DeclaringDocs, // partial classes can span multiple files
    Contracts.SymbolInfo Info);              // pre-rendered DTO; reused on query

internal sealed class SymbolIndex
{
    private readonly Dictionary<string, List<IndexedSymbol>> _byAttribute = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byReturnType = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byParameterType = new();
    private readonly HashSet<DocumentId> _dirty = new();
    private readonly object _gate = new();

    public Task BuildAsync(Solution solution, CancellationToken ct);
    public void MarkDirty(DocumentId documentId);
    public IReadOnlyList<Contracts.SymbolInfo> QueryAttribute(string target, Solution currentSolution);
    public IReadOnlyList<Contracts.SymbolInfo> QueryReturnType(string target, Solution currentSolution);
    public IReadOnlyList<Contracts.SymbolInfo> QueryParameterType(string target, Solution currentSolution);
}
```

`DeclaringDocs` is a set (not single `DocumentId`) so partial classes are handled correctly — if ANY declaring file is dirty, the cached entry is invalidated and re-walked.

## Build algorithm

`BuildAsync(Solution)` runs once per Load/Reload, kicked from inside the existing `WarmupAsync`'s per-project parallel block. After each project's `GetCompilationAsync` returns, the same project's symbols are walked and inserted into the three dictionaries.

```
foreach project in solution.Projects (parallel via Task.WhenAll, same as warm-up):
    compilation = await project.GetCompilationAsync(ct)
    foreach symbol in WalkAllSymbols(compilation):
        declaringDocs = symbol.Locations
                              .Select(l => solution.GetDocumentId(l.SourceTree))
                              .Where(id => id is not null)
                              .ToHashSet()
        symbolInfo = RoslynHelpers.ToSymbolInfo(symbol)
        indexed = new IndexedSymbol(symbolInfo.SymbolId, declaringDocs, symbolInfo)

        foreach attr in symbol.GetAttributes():
            foreach key in CandidateKeys(attr.AttributeClass):
                under lock: _byAttribute[key].Add(indexed)

        if symbol is IMethodSymbol m:
            foreach key in CandidateKeys(m.ReturnType):
                under lock: _byReturnType[key].Add(indexed)
            foreach p in m.Parameters:
                foreach key in CandidateKeys(p.Type):
                    under lock: _byParameterType[key].Add(indexed)
```

`CandidateKeys` returns BOTH the display string AND the fully-qualified metadata name — mirrors the existing `MatchesTypeName` fallback so queries can hit either form. Doing the fan-out at index time (not query time) keeps queries O(matches).

`WalkAllSymbols` is the existing helper, moved out of `SemanticSearchTool` into `SymbolIndex` (private). No semantic change to the walk itself.

Dictionary mutations happen under `_gate` because per-project work is parallel. Reads at query time also acquire `_gate` briefly to snapshot the list (lists themselves are not mutated after build returns, so reads can release immediately).

## Query algorithm

```
QueryAttribute(target, currentSolution):
    lock (_gate):
        bucket = _byAttribute.GetValueOrDefault(target, [])
        dirtySnapshot = _dirty.ToImmutable()

    results = []
    seen = new HashSet<string>()  // dedup by SymbolId

    foreach entry in bucket:
        if entry.DeclaringDocs.Overlaps(dirtySnapshot): continue   // stale
        if seen.Add(entry.SymbolId): results.Add(entry.Info)

    foreach docId in dirtySnapshot:
        doc = currentSolution.GetDocument(docId)
        if doc is null: continue
        semantic = await doc.GetSemanticModelAsync(ct)
        // walk doc's syntax root, find symbols with matching attribute,
        // build SymbolInfo, dedup against seen, add to results

    return results
```

Same shape for `QueryReturnType` and `QueryParameterType` — only the predicate changes.

The dirty walk pays O(symbols-in-changed-doc), typically 0–3 docs in practice. Empty `_dirty` (the 95% case) means step 3 is skipped entirely and the query is sub-millisecond.

## Lifecycle

| Event | Effect |
|---|---|
| `LoadAsync` / `ReloadAsync` | New `SymbolIndex` instance; old one GC'd. `_dirty` empty at construction. Build kicks off in `WarmupAsync`. |
| `GetFreshSolutionAsync` mutates a document via `WithDocumentText` | `_dirty.Add(documentId)` |
| `SemanticSearchTool` query | Reads index, walks dirty docs, returns merged |
| Process shutdown | Standard GC; nothing to flush |

`WarmupTask` semantics expand slightly: it now completes only after BOTH compilations AND index builds finish. Tests that already `await WarmupTask` to assert compilations are cached will continue to work; they may take marginally longer.

## Public surface change

`IWorkspaceService` gains one property:

```csharp
SymbolIndex SymbolIndex { get; }
```

Implementation throws `InvalidOperationException` if accessed before `LoadAsync` completes (matches the existing `GetFreshSolutionAsync` behavior).

`SymbolIndex` is `internal` and lives in `mcpRoslyn.Workspace`. Tests reach it via `InternalsVisibleTo mcpRoslyn.Tests`.

`SemanticSearchTool` changes: the three slow `case` blocks delegate to the index. The `WalkAllSymbols` and `MatchesTypeName` helpers move into `SymbolIndex` (private). `FindTypeByDisplayNameAsync` and `AddIfNew` stay where they are.

## Testing

All 5 existing `SemanticSearchToolTests` cases (the parameterized `[TestCase]` covering 5 patterns + the invalid-pattern test) MUST still pass without change. The index is a transparent perf optimization.

New tests in `tests/mcpRoslyn.Tests/SymbolIndexTests.cs`:

1. **Build correctness.** Load fixture, await warmup, assert each index has expected entries — `[MyMarker]` indexed under both `"TestLib.MyMarkerAttribute"` and `"MyMarkerAttribute"` (display-name fallback); `int` return types under both `"int"` and `"System.Int32"`; `string` parameter types similarly.
2. **Query equivalence.** For each of the three patterns, calling `SymbolIndex.QueryX` returns the same set as the v1 walk-based implementation against the fixture.
3. **Dirty-walk freshness.** Load fixture, query `has-attribute:TestLib.MyMarkerAttribute` (returns 2: `MarkedType` + `MarkedMethod`). Mutate a fixture file on disk to add `[MyMarker]` to a previously-unmarked class. Call `GetFreshSolutionAsync` (this triggers `MarkDirty`). Query again: returns 3.
4. **Dirty-walk excludes stale entries.** Load fixture, query — receives `MarkedType`. Mutate the file containing `MarkedType` to remove the attribute. Refresh + query: `MarkedType` no longer in results (excluded via dirty filter and not re-added by the dirty walk because it no longer has the attribute).
5. **Reload clears dirt.** After test #3's mutation persists, call `ReloadAsync`. `_dirty` is empty afterwards; the new file content is reflected in the freshly-built index.
6. **Partial class.** Add `[MyMarker]` to `PartialThing` via `Partial1.cs` only. Both `Partial1.cs` and `Partial2.cs` should be in the entry's `DeclaringDocs` set; making either dirty correctly invalidates the entry.

Tests #3–#6 use the existing fixture-mutation pattern from `WorkspaceServiceTests.GetFreshSolutionAsync_picks_up_file_changes_via_mtime` (mutate → refresh → restore in `finally`).

## Performance expectations

| Phase | v1.1 (current) | v1.2 (with index) |
|---|---|---|
| `LoadAsync` return | ~2 s (cache-warm) — unchanged | unchanged |
| Warm-up total (background) | ~8 s on duetGPT | ~12–16 s (compile + walk per project, parallel) |
| First `has-attribute:` query | 7.7 s | sub-100 ms |
| First `returns:` / `parameter-type:` query | unmeasured, projected ~7 s on duetGPT | sub-100 ms |
| Query after N dirty docs | walk = O(all symbols) | O(matches) + N × ~10 ms walk |
| Memory cost of index | n/a | ~10s of MB on duetGPT-sized solutions (3 dicts × ~5000 entries × small payload). Acceptable. |

All warm-up extra cost stays in the background — `LoadAsync` returns at the same time. The cost is amortized across all `semantic_search` queries in a session.

## Risks

- **Race between `BuildAsync` and `MarkDirty`.** If `GetFreshSolutionAsync` runs (and calls `MarkDirty`) while `BuildAsync` is still populating the index, the dirty doc may be added BEFORE the index entry exists. Resolution: `MarkDirty` is idempotent and tolerated to fire pre-build; the dirty doc just gets walked at query time even though there's nothing stale to invalidate. No correctness issue.
- **Index build doubles warm-up CPU.** Per-project warm-up does compile + walk in series. CPU saturation possible on small machines. Mitigation: same `Task.WhenAll` cap on parallelism as existing warm-up — no new parallelism added. If reports come in, add a `SemaphoreSlim` cap; out of scope for v1.2.
- **`Compilation.GetTypeByMetadataName` failures.** Some symbols (anonymous types, lambdas) have `DocumentationCommentId == ""`. We dedupe by `SymbolId`; empty IDs would over-dedupe. Mitigation: when `SymbolId` is empty, fall back to `symbol.ToDisplayString()` as the dedup key (matches the existing `AddIfNew` behavior in `SemanticSearchTool`).
- **Memory growth.** Three dictionaries × all symbols in all projects can be big on very large solutions. Mitigation: storing `IndexedSymbol` (a pointer-heavy record with a pre-rendered `SymbolInfo`) is fine for typical sizes; if a future user hits >1 GB indices on a >100-project solution, we revisit (lazy-build by pattern, or just-in-time index for the rarely-queried patterns).

## Open questions / deferred

- **Generic-type-instantiation matching.** Today `parameter-type:IList<T>` won't match `IList<string>`. The index preserves this. A separate enhancement could add `parameter-type:IList<*>` wildcard support — out of scope for v1.2.
- **Surface index health via `reload_workspace` output.** Nice-to-have: report `{ attributeKeys: 42, returnTypeKeys: 38, parameterTypeKeys: 117, buildDurationMs: 7300 }` alongside the existing `Diagnostics` field. Easy to add later; not blocking.
- **Lazy build per pattern.** If only `has-attribute:` is ever queried, we wasted CPU building the other two. Mitigation if observed: skip return/param indices until first query for those patterns. YAGNI for now — XAF context suggests `parameter-type:` will see use.
