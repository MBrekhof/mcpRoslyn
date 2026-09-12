using Microsoft.CodeAnalysis;

namespace mcpRoslyn.Workspace;

/// <summary>
/// A refreshed solution together with the indexes built by the same workspace generation, so a
/// tool never pairs one load's solution with another load's index (WS-006) and never reads an
/// index before it is built (IDX-002). Each index property throws
/// <see cref="IndexUnavailableException"/> when that index's build failed.
/// </summary>
public sealed class IndexedSolution(
    Solution solution,
    SymbolIndex symbolIndex, Exception? symbolIndexFailure,
    InvocationIndex invocationIndex, Exception? invocationIndexFailure)
{
    public Solution Solution { get; } = solution;

    public SymbolIndex SymbolIndex => symbolIndexFailure is null
        ? symbolIndex
        : throw new IndexUnavailableException($"Symbol index build failed: {symbolIndexFailure.Message}", symbolIndexFailure);

    public InvocationIndex InvocationIndex => invocationIndexFailure is null
        ? invocationIndex
        : throw new IndexUnavailableException($"Invocation index build failed: {invocationIndexFailure.Message}", invocationIndexFailure);
}

/// <summary>An index build failed for the current workspace generation; surfaced as INDEX_UNAVAILABLE (IDX-002).</summary>
public sealed class IndexUnavailableException(string message, Exception inner) : Exception(message, inner);
