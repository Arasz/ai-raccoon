using AiRaccoon.Core.Observability;

namespace AiRaccoon.Hosting.Watchdog;

/// <summary>
///     Shuts the host down cleanly once its own install directory disappears — e.g. `dotnet tool
///     update -g ai-raccoon` deleting the old `.store` version out from under a still-running
///     backend (ADR-0116, automates ADR-0022's manual `serve --restart`). The next proxy forward
///     re-acquires and starts the new install (BackendSessions).
/// </summary>
public sealed partial class InstallWatchdog : BackgroundService
{
    /// <summary>Span name and `operation` tag of one install check.</summary>
    internal const string OperationName = "install.check";

    private readonly Func<bool> _installDirectoryExists;
    private readonly string _installDirectory;
    private readonly TimeSpan _checkInterval;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOperationTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallWatchdog> _logger;

    public InstallWatchdog(string installDirectory, Func<bool> installDirectoryExists, TimeSpan checkInterval,
        IHostApplicationLifetime lifetime, IOperationTelemetry telemetry, TimeProvider timeProvider,
        ILogger<InstallWatchdog> logger)
    {
        _installDirectory = installDirectory;
        _installDirectoryExists = installDirectoryExists;
        _checkInterval = checkInterval;
        _lifetime = lifetime;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The production poll cadence — how quickly a replaced install is noticed.</summary>
    public static TimeSpan DefaultCheckInterval { get; } = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (RunOnce())
            {
                return;
            }
        }
    }

    /// <summary>One install check; true once the host has been asked to stop. Test seam.</summary>
    internal bool RunOnce()
    {
        using var pass = _telemetry.Begin(OperationName);
        try
        {
            if (_installDirectoryExists())
            {
                pass.Succeeded();
                return false;
            }

            pass.NoteWork();
            pass.Tag("install-removed", "true");
            Log.InstallDirectoryRemoved(_logger, _installDirectory);
            _lifetime.StopApplication();
            pass.Succeeded();
            return true;
        }
        catch (Exception ex)
        {
            // The loop survives a failed check, the same convention as IdleWatchdog: the failure
            // is metered, and the next poll tries again rather than leaving the server stuck either way.
            pass.Failed(ex);
            Log.CheckFailed(_logger, ex);
            return false;
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 613, Level = LogLevel.Warning,
            Message = "ai-raccoon: this server's install at '{InstallDirectory}' was removed, most likely replaced by 'dotnet tool update'; shutting down so the next MCP call starts the new version")]
        public static partial void InstallDirectoryRemoved(ILogger logger, string installDirectory);

        [LoggerMessage(EventId = 614, Level = LogLevel.Error, Message = "ai-raccoon: install watchdog check failed")]
        public static partial void CheckFailed(ILogger logger, Exception exception);
    }
}
