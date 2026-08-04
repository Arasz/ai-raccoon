using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Background re-watch loop: on a poll, load registrations; disabled projects keep their
///     registrations but start no checking (decision §10.3); enabled ones get a watcher + catch-up
///     scan; removed or disabled-flipped registrations stop their watcher. StopAsync disposes
///     every FileSystemWatcher (restart re-watch starts from persisted state on the next boot).
/// </summary>
public sealed class WatchHostedService(
    IMemoryStore memory,
    IWatchStore store,
    WatchPipeline pipeline,
    WatchEventSource eventSource,
    WatchCatchUp catchUp,
    TimeProvider timeProvider,
    ILogger<WatchHostedService> logger) : BackgroundService
{
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = (memory, store, pipeline, eventSource, catchUp, timeProvider, logger, stoppingToken);
        throw new NotImplementedException();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = (memory, store, pipeline, eventSource, catchUp, timeProvider, logger);
        await base.StopAsync(cancellationToken);
    }

    internal Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        _ = (memory, store, pipeline, eventSource, catchUp, timeProvider, logger, cancellationToken);
        throw new NotImplementedException();
    }
}
