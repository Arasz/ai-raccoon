using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AiRaccoon.Setup.Logging;

/// <summary>
///     Drops the MCP SDK's "request handler failed" record when its exception is a cancellation. The
///     caller gave up, ToolRefusals already logged that (914/915), and the SDK's record adds only a stack
///     trace — one no tool result can prevent, since the SDK sends the response on the dead request token.
/// </summary>
internal static class CancelledRequestLogging
{
    /// <summary>The SDK's generated event name (its LoggerMessage method name); pinned by a test against the SDK.</summary>
    internal const string SdkEventName = "LogRequestHandlerException";

    private const string SdkCategoryPrefix = "ModelContextProtocol";

    internal static void Add(ILoggingBuilder loggingBuilder) =>
        loggingBuilder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new Factory(new LoggerFactory(
            sp.GetServices<ILoggerProvider>(),
            sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
            sp.GetService<IOptions<LoggerFactoryOptions>>(),
            sp.GetService<IExternalScopeProvider>()))));

    private sealed class Factory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName)
        {
            var logger = inner.CreateLogger(categoryName);
            return categoryName.StartsWith(SdkCategoryPrefix, StringComparison.Ordinal) ? new Logger(logger) : logger;
        }

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

        public void Dispose() => inner.Dispose();
    }

    private sealed class Logger(ILogger inner) : ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is OperationCanceledException && eventId.Name == SdkEventName)
            {
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);
        }

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    }
}
