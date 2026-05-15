using FluentAssertions;
using mcpRoslyn.Tools;

namespace mcpRoslyn.Tests;

[TestFixture]
public class EchoToolTests
{
    [Test]
    public void Echo_returns_prefixed_input()
    {
        // EchoTool is a static class with a static Echo method (see src/mcpRoslyn/Tools/EchoTool.cs).
        // Calling it through the ProjectReference verifies the test project compiles
        // and links against the src project end-to-end.
        var result = EchoTool.Echo("hello");

        result.Should().Be("Echo: hello");
    }
}
