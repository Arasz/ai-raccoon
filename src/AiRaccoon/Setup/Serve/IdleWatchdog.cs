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
    private readonly ILogger<IdleWatchdog> _logger;
    private long _lastActivityTicks;

    public IdleWatchdog(TimeProvider timeProvider, TimeSpan timeout, IHostApplicationLifetime lifetime,
        ILogger<IdleWatchdog> logger)
    {
        _timeProvider = timeProvider;
        _timeout = timeout;
        _lifetime = lifetime;
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
            try
            {
                if (_timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastActivityTicks) > _timeout.Ticks)
                {
                    Log.ShuttingDownIdle(_logger, _timeout);
                    _lifetime.StopApplication();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.TickFailed(_logger, ex);
            }
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
