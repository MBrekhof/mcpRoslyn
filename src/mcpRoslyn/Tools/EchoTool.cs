using System.ComponentModel;
using ModelContextProtocol.Server;

namespace mcpRoslyn.Tools;

/// <summary>
/// Placeholder echo tool to verify MCP SDK wiring is alive.
/// This tool will be replaced by Roslyn-backed code intelligence tools.
/// </summary>
[McpServerToolType]
public static class EchoTool
{
    /// <summary>
    /// Echoes the provided message back to the caller.
    /// Used to verify the MCP server is running and tools are discoverable.
    /// </summary>
    /// <param name="message">The message to echo back.</param>
    /// <returns>The same message prefixed with "Echo: ".</returns>
    [McpServerTool(Name = "echo"), Description("Echoes the input message back. Placeholder to verify MCP SDK wiring.")]
    public static string Echo(
        [Description("The message to echo back")] string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return $"Echo: {message}";
    }
}
