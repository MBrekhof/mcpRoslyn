namespace mcpRoslyn.Contracts;

public sealed record SymbolInfo(
    string Name,
    string Kind,                  // "Class", "Method", "Property", etc.
    string SymbolId,              // DocumentationCommentId
    string? ContainingType,
    string Accessibility,
    string Signature,
    SymbolLocation? PrimaryLocation = null);
