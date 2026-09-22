using Microsoft.Extensions.Logging;

namespace mcpRoslyn.Options;

public sealed record McpRoslynOptions
{
    /// <summary>Null when none was given and discovery found none; the startup load records that (WS-008).</summary>
    public required string? SolutionPath { get; init; }
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
    public string? LogFile { get; init; }
    /// <summary>Why MSBuild could not be registered at startup (no usable .NET SDK); the startup load reports it (WS-008).</summary>
    public Exception? MSBuildFailure { get; init; }
}
