namespace mcpRoslyn.Contracts;

public sealed record SymbolLocation(
    string FilePath,
    int Line,           // 1-based
    int Column,         // 1-based
    int EndLine,
    int EndColumn,
    string? Snippet = null);
