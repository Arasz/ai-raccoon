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
    public async Task<int> SetBufferCapacityAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("capacity");
        if (!int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: buffer capacity must be a positive number of measurements");
            return ExitCode.InvalidArgument;
        }

        if (parsed > MetricsConfigKeys.MaxBufferCapacity)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: buffer capacity must be at most {MetricsConfigKeys.MaxBufferCapacity} measurements");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(MetricsConfigKeys.BufferCapacityGlobal, parsed.ToString(), cancellationToken);
        await streams.WriteOutputLineAsync(
            $"buffer capacity: {parsed} measurements (takes effect on the next server restart)");
        return 0;
    }

    public async Task<int> SetFlushIntervalAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("seconds");
        if (!int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: flush interval must be a positive number of seconds");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(MetricsConfigKeys.FlushIntervalSecondsGlobal, parsed.ToString(), cancellationToken);
        await streams.WriteOutputLineAsync($"flush interval: {parsed}s (takes effect on the next flush tick)");
        return 0;
    }

    public async Task<int> SetRetentionDaysAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("days");
        if (!int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: retention must be a positive number of days");
            return ExitCode.InvalidArgument;
        }

        if (parsed > MetricsConfigKeys.MaxRetentionDays)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: retention must be at most {MetricsConfigKeys.MaxRetentionDays} days");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(MetricsConfigKeys.RetentionDaysGlobal, parsed.ToString(), cancellationToken);
        await streams.WriteOutputLineAsync($"retention: {parsed} days (takes effect on the next maintenance pass)");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var bufferCapacity = MetricsConfigKeys.ParseBufferCapacity(
            await store.GetSettingAsync(MetricsConfigKeys.BufferCapacityGlobal, cancellationToken));
        var flushInterval = MetricsConfigKeys.ParseFlushIntervalSeconds(
            await store.GetSettingAsync(MetricsConfigKeys.FlushIntervalSecondsGlobal, cancellationToken));
        var retentionDays = MetricsConfigKeys.ParseRetentionDays(
            await store.GetSettingAsync(MetricsConfigKeys.RetentionDaysGlobal, cancellationToken));

        await streams.WriteOutputLineAsync(
            $"buffer capacity: {bufferCapacity} measurements (takes effect on the next server restart)");
        await streams.WriteOutputLineAsync($"flush interval: {flushInterval}s (takes effect on the next flush tick)");
        await streams.WriteOutputLineAsync($"retention: {retentionDays} days (takes effect on the next maintenance pass)");
        return 0;
    }
}
