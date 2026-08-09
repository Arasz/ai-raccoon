namespace AiRaccoon.Setup;

/// <summary>
///     Appends formatted log lines to a fixed file; never touches stdout/stderr. An unwritable
///     path (missing permissions, or a directory occupying the file's own path) degrades to
///     silence rather than throwing — an observability misconfiguration must not take the server
///     down (the same principle as the OTLP endpoint guard).
/// </summary>
internal sealed class QuietFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    internal QuietFileLoggerProvider(string path)
    {
        _writer = TryOpen(path);
    }

    public ILogger CreateLogger(string categoryName) => new QuietFileLogger(categoryName, _writer, _gate);

    public void Dispose() => _writer?.Dispose();

    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class QuietFileLogger(string categoryName, StreamWriter? writer, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => writer is not null && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (writer is null)
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName}: {formatter(state, exception)}";
            lock (gate)
            {
                writer.WriteLine(line);
                if (exception is not null)
                {
                    writer.WriteLine(exception);
                }
            }
        }
    }
}
