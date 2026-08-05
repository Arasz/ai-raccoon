using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Background re-watch loop: on a poll, load registrations; disabled projects keep their
///     registrations but start no checking (decision §10.3); enabled ones get a watcher + catch-up
///     scan; removed or disabled-flipped registrations stop their watcher. Restart re-watch
///     starts from persisted state.
/// </summary>
public sealed partial class WatchHostedService : BackgroundService
{
    private readonly IMemoryStore _memory;
    private readonly IWatchStore _store;
    private readonly WatchPipeline _pipeline;
    private readonly WatchEventSource _eventSource;
    private readonly WatchCatchUp _catchUp;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WatchHostedService> _logger;
    private readonly HashSet<(string ProjectId, string Path)> _active = new(WatchKeyComparer.Instance);

    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    public WatchHostedService(IMemoryStore memory, IWatchStore store, WatchPipeline pipeline,
        WatchEventSource eventSource, WatchCatchUp catchUp, TimeProvider timeProvider,
        ILogger<WatchHostedService> logger)
    {
        _memory = memory;
        _store = store;
        _pipeline = pipeline;
        _eventSource = eventSource;
        _catchUp = catchUp;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The 1s digest tick is a second loop beside reconciliation: without it, events
        // enqueue into the channel but nothing drains them in production.
        var pipelineLoop = _pipeline.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ReconcileError(_logger, ex);
            }

            try
            {
                await Task.Delay(PollInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await pipelineLoop.ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSource.StopAll();
        _active.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     One reconcile pass: every registration gets pipeline runtime state; enabled ones get a
    ///     watcher + catch-up scan on first sight; removed/disabled ones drop their watcher.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var registrations = await _store.ListWatchesAsync(cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<(string ProjectId, string Path)>(WatchKeyComparer.Instance);
        foreach (var registration in registrations)
        {
            var key = (registration.ProjectId, registration.Path);
            seen.Add(key);
            _pipeline.RegisterWatch(registration.ProjectId, registration.Path);

            if (!await IsEnabledAsync(registration.ProjectId, cancellationToken).ConfigureAwait(false))
            {
                if (_active.Remove(key))
                {
                    _eventSource.Stop(registration.ProjectId, registration.Path);
                }

                continue;
            }

            if (!_active.Add(key))
            {
                continue;
            }

            _eventSource.Start(registration.ProjectId, registration.Path);
            if (registration.LastChangeTs == 0)
            {
                _catchUp.EnqueueInitialScan(registration.ProjectId, registration.Path);
            }
            else
            {
                _catchUp.EnqueueChangedSince(registration.ProjectId, registration.Path, registration.LastChangeTs);
            }
        }

        foreach (var stale in _active.Where(k => !seen.Contains(k)).ToArray())
        {
            _eventSource.Stop(stale.ProjectId, stale.Path);
            _active.Remove(stale);
        }
    }

    private async Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in new[] { WatchConfigKeys.EnabledProject(projectId), WatchConfigKeys.EnabledGlobal })
        {
            values[key] = await _memory.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return WatchConfig.Resolve(projectId, key => values.GetValueOrDefault(key)).Enabled;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 320, Level = LogLevel.Error, Message = "Watch re-watch reconcile pass failed")]
        public static partial void ReconcileError(ILogger logger, Exception exception);
    }
}
