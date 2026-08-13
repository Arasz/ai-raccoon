using System.Net;
using System.Net.Sockets;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     The probe is an internal mechanism: its expected connection-refused polling must not
///     reach the loud serve log as info-level HttpClient stack traces. The default HTTP logging
///     handlers are removed from the probe client; outcomes are logged by the launcher/restart
///     runners through [LoggerMessage] lines.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerProbeLoggingTests
{
    [Fact]
    public async Task Probe_ConnectionRefused_EmitsNoHttpClientLogs()
    {
        var recorder = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(recorder));
        services.RegisterNodeServices();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IServerProbe>();

        await probe.RespondsAsync(FreePort(), TestContext.Current.CancellationToken);

        recorder.Entries
            .Where(e => e.Category.StartsWith("System.Net.Http.HttpClient.ServerProbe", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
