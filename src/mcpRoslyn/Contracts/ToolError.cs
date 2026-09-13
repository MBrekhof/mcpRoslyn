namespace mcpRoslyn.Contracts;

public sealed record ToolError(string Code, string Message, string? Hint = null);

/// <param name="Warnings">Caveats that apply to the answer without failing it — a workspace that may be stale (WS-007).</param>
public sealed record ToolResult<T>(
    T? Result = null, ToolError? Error = null, string? Summary = null, IReadOnlyList<string>? Warnings = null) where T : class
{
    public static ToolResult<T> Ok(T value) => new(Result: value);
    public static ToolResult<T> OkSummary(string summary) => new(Summary: summary);
    public static ToolResult<T> Fail(string code, string message, string? hint = null)
        => new(Error: new ToolError(code, message, hint));
}
