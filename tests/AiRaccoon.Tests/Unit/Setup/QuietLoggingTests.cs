using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Pins the --quiet behavior itself: a Quiet host drops Information logs while
///     warnings survive; a non-quiet host keeps Information. A recorder provider
///     registered on the host's factory observes the host's configured filter.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class QuietLoggingTests
{
    private static ServerConfig Config(bool quiet) => new(
        DefaultOptions.Port, McpTransport.Stdio,
        new InfrastructureOptions { DataRoot = Path.GetTempPath(), Scope = InstallScope.User, Quiet = quiet });

    private static List<string> Capture(Action<ILogger> emit, ServerConfig config)
    {
        using var host = McpServerSetup.CreateServerHost(config);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var recorder = new RecorderProvider();
        factory.AddProvider(recorder);
        emit(factory.CreateLogger("quiet-test"));
        return recorder.Entries;
    }

    [Fact]
    public void QuietHost_DropsInformation_KeepsWarning()
    {
        var entries = Capture(logger =>
        {
            logger.LogInformation("quiet-info-marker");
            logger.LogWarning("quiet-warn-marker");
        }, Config(quiet: true));

        entries.ShouldNotContain(e => e.Contains("quiet-info-marker", StringComparison.Ordinal));
        entries.ShouldContain(e => e.Contains("quiet-warn-marker", StringComparison.Ordinal));
    }

    [Fact]
    public void NonQuietHost_KeepsInformation()
    {
        var entries = Capture(logger => logger.LogInformation("loud-info-marker"), Config(quiet: false));

        entries.ShouldContain(e => e.Contains("loud-info-marker", StringComparison.Ordinal));
    }

    private sealed class RecorderProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecorderLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecorderLogger(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
