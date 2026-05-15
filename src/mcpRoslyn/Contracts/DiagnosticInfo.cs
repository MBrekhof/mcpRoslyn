namespace mcpRoslyn.Contracts;

public sealed record DiagnosticInfo(
    string Severity,   // "Error", "Warning", "Info", "Hidden"
    string Code,       // e.g. "CS1002"
    string Message,
    SymbolLocation Location);
