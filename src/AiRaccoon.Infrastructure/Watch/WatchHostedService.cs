using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Background re-watch loop: on a poll, load registrations; disabled projects keep their
///     registrations but start no checking (docs/plans/file-watcher-implementation.md §10 decision 3);
///     enabled ones get a watcher + catch-up scan; removed or disabled-flipped ones stop it.
/// </summary>
public sealed partial class WatchHostedService : BackgroundService
{
    /// <summary>Span name and `operation` tag of one reconcile pass.</summary>
    internal const string OperationName = "watch.reconcile";

    private readonly HashSet<WatchKey> _active = [];
    private readonly Lock _activeGate = new();
    private readonly WatchCatchUp _catchUp;
    private readonly WatchEventSource _eventSource;
    private readonly ILogger<WatchHostedService> _logger;
    private readonly IMemoryStore _memory;
    private readonly WatchPipeline _pipeline;
    private readonly HashSet<WatchKey> _registered = [];
    private readonly IWatchStore _store;
    private readonly IOperationTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _nextHealAt;
    private CancellationToken _stopping;

    public WatchHostedService(IMemoryStore memory, IWatchStore store, WatchPipeline pipeline,
        WatchEventSource eventSource, WatchCatchUp catchUp, TimeProvider timeProvider,
        IOperationTelemetry telemetry, ILogger<WatchHostedService> logger)
    {
        _memory = memory;
        _store = store;
        _pipeline = pipeline;
        _eventSource = eventSource;
        _catchUp = catchUp;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
        // The one removal choke point (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-1): every
        // removal drops the key here instantly, so a remove-then-re-add never reads as continuously active.
        _pipeline.Unregistered += OnWatchUnregistered;
        _pipeline.Recovered += OnWatchRecovered;
    }

    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How often every enabled, non-stopped watch gets a changed-files scan, so a change whose
    ///     event never arrived heals without a restart. One pruned walk of the owner's roots costs about 0.1 CPU-s.
    /// </summary>
    public static TimeSpan HealInterval { get; } = TimeSpan.FromMinutes(5);

    private void OnWatchRecovered(string projectId, string path)
    {
        Log.RecoveredScanQueued(_logger, projectId, path);
        _catchUp.EnqueueChangedFiles(projectId, path, _stopping);
    }

    private void OnWatchUnregistered(string projectId, string path)
    {
        lock (_activeGate)
        {
            _active.Remove(new WatchKey(projectId, path));
            _registered.Remove(new WatchKey(projectId, path));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        // The 1s digest tick is a second loop beside reconciliation: without it, events
        // enqueue into the channel but nothing drains them in production.
        var pipelineLoop = _pipeline.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
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
                await Task.Delay(PollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await pipelineLoop;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Must run before the poll loop is considered stopped: without it, a scan enqueued by the
        // last reconcile keeps walking the tree and enqueueing into a pipeline nothing drains.
        _catchUp.CancelAllScans();
        _eventSource.StopAll();
        lock (_activeGate)
        {
            _active.Clear();
            _registered.Clear();
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    ///     One reconcile pass: every registration gets pipeline runtime state; enabled ones get a
    ///     watcher + catch-up scan on first sight; removed/disabled ones drop their watcher.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _telemetry.RunPassAsync(OperationName, ReconcilePassAsync, cancellationToken);
    }

    private async Task ReconcilePassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        var registrations = await _store.ListWatchesAsync(cancellationToken);
        pass.Tag("registrations", registrations.Count.ToString());
        var seen = new HashSet<WatchKey>();
        foreach (var registration in registrations)
        {
            var key = new WatchKey(registration.ProjectId, registration.Path);
            seen.Add(key);
            _pipeline.RegisterWatch(registration.ProjectId, registration.Path);
            lock (_activeGate)
            {
                _registered.Add(key);
            }

            if (!await IsEnabledAsync(registration.ProjectId, cancellationToken))
            {
                bool wasActive;
                lock (_activeGate)
                {
                    wasActive = _active.Remove(key);
                }

                if (wasActive)
                {
                    _eventSource.Stop(registration.ProjectId, registration.Path);
                    pass.NoteWork();
                }

                continue;
            }

            bool started;
            lock (_activeGate)
            {
                started = _active.Add(key);
            }

            if (!started)
            {
                continue;
            }

            pass.NoteWork();
            _eventSource.Start(registration.ProjectId, registration.Path);
            if (registration.LastChangeTs == 0)
            {
                _catchUp.EnqueueInitialScan(registration.ProjectId, registration.Path, cancellationToken);
            }
            else
            {
                _catchUp.EnqueueChangedFiles(registration.ProjectId, registration.Path, cancellationToken);
            }
        }

        WatchKey[] stale;
        lock (_activeGate)
        {
            stale = [.. _registered.Where(k => !seen.Contains(k))];
        }

        HealIfDue(pass, cancellationToken);

        foreach (var s in stale)
        {
            pass.NoteWork();
            _eventSource.Stop(s.ProjectId, s.Path);
            // Raises WatchPipeline.Unregistered — the same removal choke point (D-1,
            // docs/plans/2026-08-07-watch-scan-runaway-fix.md) OnWatchUnregistered also handles.
            _pipeline.UnregisterWatch(s.ProjectId, s.Path);
            Log.StaleRegistrationUnregistered(_logger, s.ProjectId, s.Path);
        }
    }

    /// <summary>
    ///     Once per <see cref="HealInterval" />, queues a changed-files scan for every active watch that
    ///     has not stopped. The scan guard joins a scan already running instead of starting another.
    /// </summary>
    private void HealIfDue(IOperationScope pass, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_nextHealAt is null)
        {
            _nextHealAt = now + HealInterval;
            return;
        }

        if (now < _nextHealAt)
        {
            return;
        }

        _nextHealAt = now + HealInterval;
        WatchKey[] active;
        lock (_activeGate)
        {
            active = [.. _active];
        }

        var queued = 0;
        foreach (var key in active.Where(k => !_pipeline.IsStopped(k.ProjectId, k.Path)))
        {
            _catchUp.EnqueueChangedFiles(key.ProjectId, key.Path, cancellationToken);
            queued++;
        }

        if (queued > 0)
        {
            pass.NoteWork();
            Log.HealScansQueued(_logger, queued);
        }
    }

    private async Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in new[] { WatchConfigKeys.EnabledProject(projectId), WatchConfigKeys.EnabledGlobal })
        {
            values[key] = await _memory.GetSettingAsync(key, cancellationToken);
        }

        return WatchConfig.Resolve(projectId, key => values.GetValueOrDefault(key)).Enabled;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "Watch re-watch reconcile pass failed")]
        public static partial void ReconcileError(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 321, Level = LogLevel.Information,
            Message = "Stale watch registration for project {ProjectId} at {Path} unregistered from the pipeline")]
        public static partial void StaleRegistrationUnregistered(ILogger logger, string projectId, string path);

        [LoggerMessage(EventId = 322, Level = LogLevel.Debug,
            Message = "Watch heal queued a changed-files scan for {Count} watches")]
        public static partial void HealScansQueued(ILogger logger, int count);

        [LoggerMessage(EventId = 323, Level = LogLevel.Information,
            Message = "Watch for project {ProjectId} at {Path} digested again after a failure; scanning it for changed files")]
        public static partial void RecoveredScanQueued(ILogger logger, string projectId, string path);
    }
}
