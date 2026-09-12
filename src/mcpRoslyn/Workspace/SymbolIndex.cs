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
                var declaringDocs = AllPartLocations(sym)
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

                lock (_gate) _all.Add(entry);

                foreach (var attr in sym.GetAttributes())
                {
                    foreach (var key in CandidateKeys(attr.AttributeClass))
                        Add(_byAttribute, key, entry);
                }

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
            }
        });
        await Task.WhenAll(tasks);
    }

    public void MarkDirty(DocumentId documentId)
    {
        lock (_gate) _dirty.Add(documentId);
    }

    public IReadOnlyList<Contracts.SymbolInfo> QueryAttribute(string target, Solution currentSolution, CancellationToken ct = default)
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
            ct).Select(e => e.Info).ToArray();
    }

    public IReadOnlyList<Contracts.SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
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
            ct).Select(e => e.Info).ToArray();
    }

    public IReadOnlyList<Contracts.SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
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
            ct).Select(e => e.Info).ToArray();
    }

    /// <summary>
    /// Every indexed symbol, through the same dirty-walk the pattern queries use: entries whose
    /// declaring documents have changed since the build are dropped and re-walked live (IDX-001).
    /// Results are de-duplicated by symbol id, declaration file and declaring assembly, so the same
    /// symbol is listed once while same-named symbols from different assemblies stay apart (IDX-005).
    /// </summary>
    public IReadOnlyList<IndexedSymbol> AllSymbols(Solution currentSolution, CancellationToken ct = default)
    {
        List<IndexedSymbol> all;
        HashSet<DocumentId> dirty;
        lock (_gate)
        {
            all = new(_all);
            dirty = new(_dirty);
        }

        return MergeWithDirtyWalk(all, dirty, currentSolution, predicate: _ => true, ct);
    }

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

    private List<IndexedSymbol> MergeWithDirtyWalk(
        List<IndexedSymbol> bucket,
        HashSet<DocumentId> dirty,
        Solution currentSolution,
        Func<ISymbol, bool> predicate,
        CancellationToken ct)
    {
        var results = new List<IndexedSymbol>();
        // A documentation-comment id carries no assembly identity: two projects each declaring
        // Acme.Options share "T:Acme.Options" (every top-level-statements project's Program does), and a
        // file linked into two projects declares it at the same path in both. So the key adds the
        // declaration file AND the declaring assembly — which still collapses a multi-targeted project
        // (Foo(net8.0)/Foo(net10.0) share an assembly name) (IDX-005). ponytail: two unrelated projects
        // that link the same file AND share an assembly name still collapse; no solution we load does.
        var positions = new Dictionary<(string SymbolId, string? FilePath, string? Assembly), int>();
        (string, string?, string?) Key(string symbolId, Contracts.SymbolInfo info, DocumentId? doc)
            => (symbolId, info.PrimaryLocation?.FilePath,
                doc is null ? null : currentSolution.GetProject(doc.ProjectId)?.AssemblyName);
        var mergedDocs = new Dictionary<int, HashSet<DocumentId>>();
        void AddOrMerge((string, string?, string?) key, IndexedSymbol entry)
        {
            if (positions.TryGetValue(key, out var at))
            {
                // The same declaration seen again — another project of the same assembly (a
                // multi-targeted project), or another file of a partial type: one entry, but every
                // declaring document kept, so callers can reach each copy (#if can make them differ).
                // Copied once and then grown in place: a partial type spread over many files would
                // otherwise re-copy the whole set on each of its files.
                if (!mergedDocs.TryGetValue(at, out var docs))
                {
                    docs = new HashSet<DocumentId>(results[at].DeclaringDocs); // the cached set is the index's own
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

        // Re-walk live: the dirty documents, plus every other file declaring an entry a dirty file
        // invalidated — delete a partial method's optional implementation, and its surviving
        // definition lives in a file that did not change.
        var walk = new HashSet<DocumentId>(dirty);
        foreach (var entry in bucket)
        {
            if (entry.DeclaringDocs.Overlaps(dirty))
            {
                walk.UnionWith(entry.DeclaringDocs);
                continue;
            }
            AddOrMerge(Key(entry.SymbolId, entry.Info, entry.DeclaringDocs.FirstOrDefault()), entry);
        }

        foreach (var docId in walk)
        {
            var doc = currentSolution.GetDocument(docId);
            if (doc is null) continue;

            var semantic = doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
            if (semantic is null) continue;

            foreach (var declared in WalkDocumentSymbols(semantic))
            {
                // Each part of a partial member is its own symbol with its own location; the build
                // indexes the definition part, so the walk must too or one method reads as two.
                var sym = DefinitionPart(declared);
                if (!predicate(sym)) continue;
                var info = RoslynHelpers.ToSymbolInfo(sym);
                var key = !string.IsNullOrEmpty(info.SymbolId) ? info.SymbolId : sym.ToDisplayString();
                AddOrMerge(Key(key, info, docId), new IndexedSymbol(key, new HashSet<DocumentId> { docId }, info));
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

    private static ISymbol DefinitionPart(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
        IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
        IEventSymbol { PartialDefinitionPart: { } definition } => definition,
        _ => symbol
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

    private static bool MatchesTypeName(ITypeSymbol? type, string target)
    {
        if (type is null) return false;
        if (type.ToDisplayString() == target) return true;
        var ns = type.ContainingNamespace?.ToDisplayString();
        var metadata = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";
        return metadata == target;
    }

    /// <summary>
    /// Walks the compilation's OWN assembly, not <c>Compilation.GlobalNamespace</c>. The latter
    /// merges every referenced assembly, so this walked the whole BCL and every package on each
    /// project only to discard the results — the entries kept are identical either way, because a
    /// symbol with no source-declaring document is dropped a few lines below (PERF-001).
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

    public sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        Contracts.SymbolInfo Info);
}
