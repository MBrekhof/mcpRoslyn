namespace mcpRoslyn.Contracts;

public sealed record ToolError(string Code, string Message, string? Hint = null);

public sealed record ToolResult<T>(T? Result = null, ToolError? Error = null) where T : class
{
    public static ToolResult<T> Ok(T value) => new(Result: value);
    public static ToolResult<T> Fail(string code, string message, string? hint = null)
        => new(Error: new ToolError(code, message, hint));
}
