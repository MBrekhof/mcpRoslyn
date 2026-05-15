using Microsoft.Extensions.Logging;

namespace mcpRoslyn.Options;

public sealed class McpRoslynOptions
{
    public required string SolutionPath { get; init; }
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
}
