using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Core.Watch;
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
    private readonly IMemoryStore _memory;
    private readonly IWatchStore _store;
    private readonly WatchPipeline _pipeline;
    private readonly WatchEventSource _eventSource;
    private readonly WatchCatchUp _catchUp;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationTelemetry _telemetry;
    private readonly ILogger<WatchHostedService> _logger;
    private readonly Lock _activeGate = new();
    private readonly HashSet<(string ProjectId, string Path)> _active = new(WatchKeyComparer.Instance);
    private readonly HashSet<(string ProjectId, string Path)> _registered = new(WatchKeyComparer.Instance);

    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Span name and `operation` tag of one reconcile pass.</summary>
    internal const string OperationName = "watch.reconcile";

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
    }

    private void OnWatchUnregistered(string projectId, string path)
    {
        lock (_activeGate)
        {
            _active.Remove((projectId, path));
            _registered.Remove((projectId, path));
        }
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
        // Must run before the poll loop is considered stopped: without it, a scan enqueued by the
        // last reconcile keeps walking the tree and enqueueing into a pipeline nothing drains.
        _catchUp.CancelAllScans();
        _eventSource.StopAll();
        lock (_activeGate)
        {
            _active.Clear();
            _registered.Clear();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     One reconcile pass: every registration gets pipeline runtime state; enabled ones get a
    ///     watcher + catch-up scan on first sight; removed/disabled ones drop their watcher.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var pass = _telemetry.Begin(OperationName);
        try
        {
            await ReconcilePassAsync(pass, cancellationToken).ConfigureAwait(false);
            pass.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown cut the pass short: abandoned, not failed
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            throw;
        }
    }

    private async Task ReconcilePassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        var registrations = await _store.ListWatchesAsync(cancellationToken).ConfigureAwait(false);
        pass.Tag("registrations", registrations.Count.ToString());
        var seen = new HashSet<(string ProjectId, string Path)>(WatchKeyComparer.Instance);
        foreach (var registration in registrations)
        {
            var key = (registration.ProjectId, registration.Path);
            seen.Add(key);
            _pipeline.RegisterWatch(registration.ProjectId, registration.Path);
            lock (_activeGate)
            {
                _registered.Add(key);
            }

            if (!await IsEnabledAsync(registration.ProjectId, cancellationToken).ConfigureAwait(false))
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
                _catchUp.EnqueueChangedSince(registration.ProjectId, registration.Path, registration.LastChangeTs,
                    cancellationToken);
            }
        }

        (string ProjectId, string Path)[] stale;
        lock (_activeGate)
        {
            stale = _registered.Where(k => !seen.Contains(k)).ToArray();
        }

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

        [LoggerMessage(EventId = 321, Level = LogLevel.Information,
            Message = "Stale watch registration for project {ProjectId} at {Path} unregistered from the pipeline")]
        public static partial void StaleRegistrationUnregistered(ILogger logger, string projectId, string path);
    }
}
