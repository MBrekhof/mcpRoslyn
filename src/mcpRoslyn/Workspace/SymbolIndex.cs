using Microsoft.CodeAnalysis;
using mcpRoslyn.Tools;

namespace mcpRoslyn.Workspace;

public sealed class SymbolIndex
{
    private readonly Dictionary<string, List<IndexedSymbol>> _byAttribute = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byReturnType = new();
    private readonly Dictionary<string, List<IndexedSymbol>> _byParameterType = new();
    private readonly List<IndexedSymbol> _all = new();
    private readonly HashSet<DocumentId> _dirty = new();
    private Solution? _solution;                  // holds the dirty documents' current text
    private readonly object _gate = new();        // guards everything above
    private readonly object _refreshGate = new(); // one refresh at a time
    private int _documentsWalked;

    /// <summary>Test seam: documents re-walked by refreshes so far.</summary>
    internal int DocumentsWalked => Volatile.Read(ref _documentsWalked);

    public async Task BuildAsync(Solution solution, CancellationToken ct = default)
    {
        lock (_gate) _solution ??= solution; // an Invalidate during warm-up already holds a newer one
        var tasks = solution.Projects.Select(async project =>
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) return;

            foreach (var sym in WalkAllSymbols(compilation))
            {
                ct.ThrowIfCancellationRequested();
                if (Pending(sym, solution) is { } pending)
                    lock (_gate) Insert(pending);
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Records changed documents together with the solution holding their new text, in one step, so a
    /// refresh never pairs a dirty marker with a solution that predates it.
    /// </summary>
    public void Invalidate(IEnumerable<DocumentId> changed, Solution solution)
    {
        lock (_gate)
        {
            _dirty.UnionWith(changed);
            _solution = solution;
        }
    }

    public IReadOnlyList<Contracts.SymbolInfo> QueryAttribute(string target, Solution currentSolution, CancellationToken ct = default)
        => Lookup(_byAttribute, target, currentSolution, ct);

    public IReadOnlyList<Contracts.SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
        => Lookup(_byReturnType, target, currentSolution, ct);

    public IReadOnlyList<Contracts.SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
        => Lookup(_byParameterType, target, currentSolution, ct);

    /// <summary>
    /// Every indexed symbol, after folding in edits since the build (IDX-001, IDX-004), with the solution
    /// those entries reflect: at least as new as <paramref name="currentSolution"/>, and newer if another
    /// call refreshed a file meanwhile. A caller resolving entries back to symbols must use that solution,
    /// or a symbol renamed in between is found in neither snapshot. Results are de-duplicated by symbol id,
    /// declaration file and declaring assembly, so the same symbol is listed once while same-named symbols
    /// from different assemblies stay apart (IDX-005).
    /// </summary>
    public IndexedSymbols AllSymbols(Solution currentSolution, CancellationToken ct = default)
    {
        lock (_refreshGate) // no refresh can swap entries between reading them and naming their solution
        {
            var solution = Refresh(ct) ?? currentSolution;
            List<IndexedSymbol> all;
            lock (_gate) all = new(_all);
            return new IndexedSymbols(Distinct(all, currentSolution), solution);
        }
    }

    private IReadOnlyList<Contracts.SymbolInfo> Lookup(
        Dictionary<string, List<IndexedSymbol>> buckets, string target, Solution currentSolution, CancellationToken ct)
    {
        List<IndexedSymbol> bucket;
        lock (_refreshGate)
        {
            Refresh(ct);
            lock (_gate) bucket = buckets.TryGetValue(target, out var list) ? new(list) : new();
        }
        // The solution only names each declaring project's assembly, which a refresh never changes.
        return Distinct(bucket, currentSolution).Select(e => e.Info).ToArray();
    }

    /// <summary>
    /// Folds changed documents into the index once, instead of re-walking every document edited since
    /// the build on every query — a cost that grew with the session's edit history (IDX-004). Entries a
    /// dirty document declares are replaced by a walk of every file they are declared in (a partial
    /// member's surviving part may live in a file that did not change), built outside the lock and
    /// swapped in under it: a concurrent query sees the old entries or the new ones, and a walk that
    /// throws (or is cancelled) leaves both entries and markers as they were. Markers are cleared only
    /// if no newer solution arrived during the walk; otherwise the next query walks again.
    /// ponytail: dirtiness is per document, but semantics are not — changing a global using alias or a
    /// type's namespace in one file changes the index keys of symbols in unchanged files. Those stay stale
    /// until those files change or reload_workspace rebuilds the index.
    /// </summary>
    /// <returns>The solution the entries now reflect; null before the build has begun.</returns>
    private Solution? Refresh(CancellationToken ct)
    {
        lock (_refreshGate)
        {
            HashSet<DocumentId> dirty;
            HashSet<DocumentId> walk;
            Solution solution;
            lock (_gate)
            {
                if (_dirty.Count == 0 || _solution is null) return _solution;
                dirty = new(_dirty);
                solution = _solution;
                walk = new(dirty);
                foreach (var entry in _all)
                    if (entry.DeclaringDocs.Overlaps(dirty)) walk.UnionWith(entry.DeclaringDocs);
            }

            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var fresh = new List<PendingEntry>();
            foreach (var docId in walk)
            {
                ct.ThrowIfCancellationRequested();
                var doc = solution.GetDocument(docId);
                if (doc is null) continue; // gone from the solution: its entries simply go
                // ponytail: sync-over-async — the query API is synchronous; make it async if a caller needs to.
                var semantic = doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
                if (semantic is null) continue;
                Interlocked.Increment(ref _documentsWalked);

                foreach (var sym in WalkDocumentTypes(semantic))
                {
                    // A partial type's members declared only in files outside the walk keep their entries.
                    if (!seen.Add(sym) || Pending(sym, solution) is not { } pending
                        || !pending.Entry.DeclaringDocs.Overlaps(walk)) continue;
                    fresh.Add(pending);
                }
            }

            lock (_gate)
            {
                // Every entry overlapping a walked document is re-found by that walk, so it goes first.
                bool Replaced(IndexedSymbol e) => e.DeclaringDocs.Overlaps(walk);
                _all.RemoveAll(Replaced);
                foreach (var buckets in new[] { _byAttribute, _byReturnType, _byParameterType })
                    foreach (var list in buckets.Values)
                        list.RemoveAll(Replaced);
                foreach (var pending in fresh) Insert(pending);
                // A newer solution's markers stay; the documents they name are unchanged in this one,
                // so the entries do reflect it.
                if (ReferenceEquals(_solution, solution)) _dirty.ExceptWith(dirty);
            }
            return solution;
        }
    }

    // ---------- Helpers ----------

    private sealed record PendingEntry(
        IndexedSymbol Entry,
        IReadOnlyList<string> AttributeKeys,
        IReadOnlyList<string> ReturnKeys,
        IReadOnlyList<string> ParameterKeys);

    private static PendingEntry? Pending(ISymbol sym, Solution solution)
    {
        var declaringDocs = AllPartLocations(sym)
            .Where(l => l.IsInSource && l.SourceTree is not null)
            .Select(l => solution.GetDocumentId(l.SourceTree))
            .Where(id => id is not null)
            .Cast<DocumentId>()
            .ToHashSet();
        if (declaringDocs.Count == 0) return null;

        var info = RoslynHelpers.ToSymbolInfo(sym);
        var entry = new IndexedSymbol(
            SymbolId: !string.IsNullOrEmpty(info.SymbolId) ? info.SymbolId : sym.ToDisplayString(),
            DeclaringDocs: declaringDocs,
            Info: info);
        var method = sym as IMethodSymbol;
        return new PendingEntry(
            entry,
            sym.GetAttributes().SelectMany(a => CandidateKeys(a.AttributeClass)).ToArray(),
            method is null ? [] : CandidateKeys(method.ReturnType).ToArray(),
            method is null ? [] : method.Parameters.SelectMany(p => CandidateKeys(p.Type)).ToArray());
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Insert(PendingEntry pending)
    {
        _all.Add(pending.Entry);
        foreach (var key in pending.AttributeKeys) AddTo(_byAttribute, key, pending.Entry);
        foreach (var key in pending.ReturnKeys) AddTo(_byReturnType, key, pending.Entry);
        foreach (var key in pending.ParameterKeys) AddTo(_byParameterType, key, pending.Entry);

        static void AddTo(Dictionary<string, List<IndexedSymbol>> buckets, string key, IndexedSymbol entry)
        {
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<IndexedSymbol>();
            list.Add(entry);
        }
    }

    /// <summary>
    /// A documentation-comment id carries no assembly identity: two projects each declaring Acme.Options
    /// share "T:Acme.Options" (every top-level-statements project's Program does), and a file linked into
    /// two projects declares it at the same path in both. So the key adds the declaration file AND the
    /// declaring assembly — which still collapses a multi-targeted project (Foo(net8.0)/Foo(net10.0)
    /// share an assembly name) (IDX-005). ponytail: two unrelated projects that link the same file AND
    /// share an assembly name still collapse; no solution we load does.
    /// </summary>
    private static (string SymbolId, string? FilePath, string? Assembly) KeyOf(IndexedSymbol entry, Solution solution)
    {
        var doc = entry.DeclaringDocs.FirstOrDefault();
        return (entry.SymbolId, entry.Info.PrimaryLocation?.FilePath,
            doc is null ? null : solution.GetProject(doc.ProjectId)?.AssemblyName);
    }

    /// <summary>
    /// One result per key. The same declaration seen again — another project of the same assembly (a
    /// multi-targeted project), or another partial file — keeps every declaring document, so callers
    /// can reach each copy (#if can make them differ). Merged sets are copied once and grown in place:
    /// the index's own sets are never mutated.
    /// </summary>
    private static List<IndexedSymbol> Distinct(IEnumerable<IndexedSymbol> entries, Solution solution)
    {
        var results = new List<IndexedSymbol>();
        var positions = new Dictionary<(string, string?, string?), int>();
        var mergedDocs = new Dictionary<int, HashSet<DocumentId>>();
        foreach (var entry in entries)
        {
            var key = KeyOf(entry, solution);
            if (positions.TryGetValue(key, out var at))
            {
                if (!mergedDocs.TryGetValue(at, out var docs))
                {
                    docs = new HashSet<DocumentId>(results[at].DeclaringDocs);
                    mergedDocs[at] = docs;
                    results[at] = results[at] with { DeclaringDocs = docs };
                }
                docs.UnionWith(entry.DeclaringDocs);
            }
            else
            {
                positions[key] = results.Count;
                results.Add(entry);
            }
        }
        return results;
    }

    /// <summary>
    /// A partial member's symbol carries only its own part's location, but either part's file can
    /// change its attributes; both must dirty the entry, or editing the implementation alone leaves a
    /// stale match.
    /// </summary>
    private static IEnumerable<Location> AllPartLocations(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { PartialImplementationPart: { } implementation } => symbol.Locations.Concat(implementation.Locations),
        IPropertySymbol { PartialImplementationPart: { } implementation } => symbol.Locations.Concat(implementation.Locations),
        IEventSymbol { PartialImplementationPart: { } implementation } => symbol.Locations.Concat(implementation.Locations),
        _ => symbol.Locations
    };

    private static IEnumerable<string> CandidateKeys(ITypeSymbol? type)
    {
        if (type is null) yield break;
        var display = type.ToDisplayString();
        yield return display;
        var ns = type.ContainingNamespace?.ToDisplayString();
        var metadata = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";
        if (metadata != display) yield return metadata;
    }

    /// <summary>
    /// Walks the compilation's OWN assembly, not <c>Compilation.GlobalNamespace</c>. The latter
    /// merges every referenced assembly, so this walked the whole BCL and every package on each
    /// project only to discard the results — the entries kept are identical either way, because a
    /// symbol with no source-declaring document is dropped (PERF-001).
    /// </summary>
    private static IEnumerable<ISymbol> WalkAllSymbols(Compilation compilation)
    {
        foreach (var sym in WalkNamespace(compilation.Assembly.GlobalNamespace)) yield return sym;

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
    }

    /// <summary>
    /// The build's walk, limited to the types a document touches: every outermost type it declares, or
    /// holds top-level statements for, with all members. Walking declarations one by one instead missed
    /// members with no syntax of their own — implicit constructors, record plumbing, the getter of an
    /// expression-bodied property — so a refresh dropped entries the build had (IDX-004).
    /// </summary>
    private static IEnumerable<ISymbol> WalkDocumentTypes(SemanticModel semantic)
    {
        var types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var node in semantic.SyntaxTree.GetRoot().DescendantNodesAndSelf())
        {
            var declared = semantic.GetDeclaredSymbol(node);
            var type = declared as INamedTypeSymbol ?? declared?.ContainingType;
            if (type is null) continue;
            while (type.ContainingType is { } outer) type = outer;
            types.Add(type);
        }
        foreach (var type in types)
        {
            yield return type;
            foreach (var member in WalkType(type)) yield return member;
        }
    }

    private static IEnumerable<ISymbol> WalkType(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            yield return member;
            if (member is INamedTypeSymbol nested)
                foreach (var inner in WalkType(nested)) yield return inner;
        }
    }

    public sealed record IndexedSymbols(IReadOnlyList<IndexedSymbol> Symbols, Solution Solution);

    public sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        Contracts.SymbolInfo Info);
}
