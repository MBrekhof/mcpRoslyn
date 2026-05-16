using Microsoft.CodeAnalysis;
using mcpRoslyn.Tools;

namespace mcpRoslyn.Workspace;

public sealed class SymbolIndex
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
            ct);
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
            ct);
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
            ct);
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

    private List<Contracts.SymbolInfo> MergeWithDirtyWalk(
        List<IndexedSymbol> bucket,
        HashSet<DocumentId> dirty,
        Solution currentSolution,
        Func<ISymbol, bool> predicate,
        CancellationToken ct)
    {
        var results = new List<Contracts.SymbolInfo>();
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

    public sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        Contracts.SymbolInfo Info);
}
