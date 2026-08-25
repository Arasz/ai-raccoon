using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     The probe is an internal mechanism: its expected connection-refused polling must not
///     reach the loud serve log as info-level HttpClient stack traces. The default HTTP logging
///     handlers are removed from the probe client; outcomes are logged by the launcher/restart
///     runners through [LoggerMessage] lines.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerProbeLoggingTests
{
    [RetryFact]
    public async Task Probe_ConnectionRefused_EmitsNoHttpClientLogs()
    {
        using var lease = LoopbackPort.Reserve();
        var recorder = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(recorder));
        services.RegisterNodeServices();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IServerProbe>();

        // Connection refused needs the number empty: give the reservation up only now.
        lease.ReleaseForBind();
        await probe.RespondsAsync(lease.Port, TestContext.Current.CancellationToken);

        recorder.Entries
            .Where(e => e.Category.StartsWith("System.Net.Http.HttpClient.ServerProbe", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }


    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, List<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((category, logLevel, formatter(state, exception)));
        }
    }
}
