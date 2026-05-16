using Microsoft.Extensions.Logging;

namespace mcpRoslyn.Logging;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;

    public FileLoggerProvider(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(TextWriter writer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            writer.WriteLine($"{DateTime.UtcNow:O} {logLevel,-11} {category} - {message}");
            if (exception is not null) writer.WriteLine(exception);
        }
    }
}
