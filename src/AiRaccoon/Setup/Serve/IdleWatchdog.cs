using AiRaccoon.Core.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Shuts the host down after a period without /mcp activity: requests reset the deadline
///     (IActivitySignaler); background passes never count (docs/work/archive/2026-08-06-http-serve-design.md §3.3).
/// </summary>
public sealed partial class IdleWatchdog : BackgroundService, IActivitySignaler
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOperationTelemetry _telemetry;
    private readonly ILogger<IdleWatchdog> _logger;
    private long _lastActivityTicks;

    /// <summary>Span name and `operation` tag of one idle check.</summary>
    internal const string OperationName = "idle.check";

    public IdleWatchdog(TimeProvider timeProvider, TimeSpan timeout, IHostApplicationLifetime lifetime,
        IOperationTelemetry telemetry, ILogger<IdleWatchdog> logger)
    {
        _timeProvider = timeProvider;
        _timeout = timeout;
        _lifetime = lifetime;
        _telemetry = telemetry;
        _logger = logger;
        // Baseline: a fresh server lives a full timeout even with zero requests.
        _lastActivityTicks = timeProvider.GetUtcNow().UtcTicks;
        Log.Armed(logger, timeout);
    }

    /// <summary>Restarts the idle deadline from now.</summary>
    public void NotifyActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, _timeProvider.GetUtcNow().UtcTicks);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tick = min(60s, timeout/4) (docs/plans/2026-08-06-http-serve-mode-plan.md R2): a fixed
        // 60s tick would shut a 2s-timeout host down up to 62s late.
        var tick = _timeout / 4 < TimeSpan.FromSeconds(60) ? _timeout / 4 : TimeSpan.FromSeconds(60);
        using var timer = new PeriodicTimer(tick, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (RunOnce())
            {
                return;
            }
        }
    }

    /// <summary>One idle check; true once the host has been asked to stop. Test seam.</summary>
    internal bool RunOnce()
    {
        using var pass = _telemetry.Begin(OperationName);
        try
        {
            if (_timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastActivityTicks) <= _timeout.Ticks)
            {
                pass.Succeeded();
                return false;
            }

            pass.Tag("idle", "true");
            Log.ShuttingDownIdle(_logger, _timeout);
            _lifetime.StopApplication();
            pass.Succeeded();
            return true;
        }
        catch (Exception ex)
        {
            // The loop survives a failed tick, as it always has — the failure is now metered too.
            pass.Failed(ex);
            Log.TickFailed(_logger, ex);
            return false;
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 610, Level = LogLevel.Information, Message = "ai-raccoon: idle watchdog armed ({IdleTimeout})")]
        public static partial void Armed(ILogger logger, TimeSpan idleTimeout);

        [LoggerMessage(EventId = 611, Level = LogLevel.Information, Message = "ai-raccoon: shutting down after {IdleTimeout} without MCP activity")]
        public static partial void ShuttingDownIdle(ILogger logger, TimeSpan idleTimeout);

        [LoggerMessage(EventId = 612, Level = LogLevel.Error, Message = "ai-raccoon: idle watchdog tick failed")]
        public static partial void TickFailed(ILogger logger, Exception exception);
    }
}
