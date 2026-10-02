using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot metrics-config verb handlers: buffer-capacity, flush-interval, retention, list —
///     the CLI-only channel for the metrics writer (MetricsFlusher) and reaper (MetricsRetentionJob).
///     The three knobs take effect on different cadences (docs/how-to/configure-ai-raccoon-server.md),
///     so every setter and list states its own timing rather than leaving the operator to guess.
/// </summary>
public sealed class PerformanceCommands
{
    private static readonly IntSetting BufferCapacity = new(MetricsConfigKeys.BufferCapacityGlobal, "capacity",
        "buffer capacity", "measurements",
        value => $"buffer capacity: {value} measurements (takes effect on the next server restart)",
        MetricsConfigKeys.MaxBufferCapacity);

    private static readonly IntSetting FlushInterval = new(MetricsConfigKeys.FlushIntervalSecondsGlobal, "seconds",
        "flush interval", "seconds", value => $"flush interval: {value}s (takes effect on the next flush tick)");

    private static readonly IntSetting RetentionDays = new(MetricsConfigKeys.RetentionDaysGlobal, "days",
        "retention", "days", value => $"retention: {value} days (takes effect on the next maintenance pass)",
        MetricsConfigKeys.MaxRetentionDays);

    public Task<int> SetBufferCapacityAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken) =>
        BufferCapacity.SetAsync(parseResult, store, streams, cancellationToken);

    public Task<int> SetFlushIntervalAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken) =>
        FlushInterval.SetAsync(parseResult, store, streams, cancellationToken);

    public Task<int> SetRetentionDaysAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken) =>
        RetentionDays.SetAsync(parseResult, store, streams, cancellationToken);

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var bufferCapacity = MetricsConfigKeys.ParseBufferCapacity(
            await store.GetSettingAsync(MetricsConfigKeys.BufferCapacityGlobal, cancellationToken));
        var flushInterval = MetricsConfigKeys.ParseFlushIntervalSeconds(
            await store.GetSettingAsync(MetricsConfigKeys.FlushIntervalSecondsGlobal, cancellationToken));
        var retentionDays = MetricsConfigKeys.ParseRetentionDays(
            await store.GetSettingAsync(MetricsConfigKeys.RetentionDaysGlobal, cancellationToken));

        await streams.WriteOutputLineAsync(BufferCapacity.Confirmation(bufferCapacity));
        await streams.WriteOutputLineAsync(FlushInterval.Confirmation(flushInterval));
        await streams.WriteOutputLineAsync(RetentionDays.Confirmation(retentionDays));
        return 0;
    }
}
