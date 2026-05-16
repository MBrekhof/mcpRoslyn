using Microsoft.CodeAnalysis;

namespace mcpRoslyn.Workspace;

public sealed class SymbolIndex
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

    public IReadOnlyList<Contracts.SymbolInfo> QueryAttribute(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<Contracts.SymbolInfo>();

    public IReadOnlyList<Contracts.SymbolInfo> QueryReturnType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<Contracts.SymbolInfo>();

    public IReadOnlyList<Contracts.SymbolInfo> QueryParameterType(string target, Solution currentSolution, CancellationToken ct = default)
        => Array.Empty<Contracts.SymbolInfo>();

    public sealed record IndexedSymbol(
        string SymbolId,
        IReadOnlySet<DocumentId> DeclaringDocs,
        Contracts.SymbolInfo Info);
}
