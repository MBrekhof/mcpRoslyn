using FluentAssertions;
using mcpRoslyn.Logging;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace mcpRoslyn.Tests;

[TestFixture]
public class FileLoggerProviderTests
{
    [Test]
    public void Writes_log_lines_to_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-test-{Guid.NewGuid()}.log");
        try
        {
            using (var provider = new FileLoggerProvider(path))
            {
                var logger = provider.CreateLogger("TestCategory");
                logger.LogInformation("Hello {Subject}", "world");
            }

            var content = File.ReadAllText(path);
            content.Should().Contain("TestCategory");
            content.Should().Contain("Hello world");
            content.Should().Contain("Information");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void Appends_to_existing_file_instead_of_truncating()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-test-{Guid.NewGuid()}.log");
        try
        {
            File.WriteAllText(path, "prior session content\n");

            using (var provider = new FileLoggerProvider(path))
            {
                provider.CreateLogger("Cat").LogInformation("new session line");
            }

            var content = File.ReadAllText(path);
            content.Should().StartWith("prior session content");
            content.Should().Contain("new session line");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void Creates_parent_directory_if_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-test-{Guid.NewGuid()}");
        var path = Path.Combine(dir, "logs", "session.log");
        try
        {
            using (var provider = new FileLoggerProvider(path))
            {
                provider.CreateLogger("Cat").LogWarning("created on demand");
            }

            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Contain("created on demand");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Writes_exception_below_message()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcpRoslyn-test-{Guid.NewGuid()}.log");
        try
        {
            using (var provider = new FileLoggerProvider(path))
            {
                var logger = provider.CreateLogger("Cat");
                logger.LogError(new InvalidOperationException("boom"), "operation failed");
            }

            var content = File.ReadAllText(path);
            content.Should().Contain("operation failed");
            content.Should().Contain("InvalidOperationException");
            content.Should().Contain("boom");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
