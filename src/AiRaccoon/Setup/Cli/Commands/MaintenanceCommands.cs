using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot bank-maintenance verb handlers: interval, vacuum-interval, embed-rows-per-run, list
///     — the CLI-only channel for the maintenance service. `list` additionally reports live bank
///     disk stats (db/WAL sizes, reclaimable bytes, delta vs the previous check via a stats
///     sidecar) — a thin client over <see cref="IMaintenanceStatsStore" /> (ADR-0075 amendment):
///     the stats are computed server-side and the CLI never opens the bank for them.
/// </summary>
public sealed class MaintenanceCommands(IMaintenanceStatsStore maintenanceStats, InfrastructureOptions options)
{
    private string StatsSidecarPath =>
        Path.Combine(Path.GetDirectoryName(SqliteConnectionFactory.BankPathFor(options))!, "maintenance-stats.json");

    public async Task<int> SetCheckpointIntervalAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var minutes = parseResult.GetValue<string>("minutes");
        if (!int.TryParse(minutes, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: checkpoint interval must be a positive number of minutes");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, parsed.ToString(),
            cancellationToken);
        await streams.WriteOutputLineAsync($"checkpoint interval: {parsed} min");
        return 0;
    }

    public async Task<int> SetVacuumIntervalAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var days = parseResult.GetValue<string>("days");
        if (!int.TryParse(days, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: vacuum interval must be a positive number of days");
            return ExitCode.InvalidArgument;
        }

        if (parsed > BankMaintenanceConfigKeys.MaxVacuumIntervalDays)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: vacuum interval must be at most {BankMaintenanceConfigKeys.MaxVacuumIntervalDays} days");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, parsed.ToString(),
            cancellationToken);
        await streams.WriteOutputLineAsync($"vacuum interval: {parsed} days");
        return 0;
    }

    /// <summary>
    ///     WP11-C (owner gate G18): the "cheaper first move" — takes effect on the drain's next
    ///     pass, no restart. Review finding 4 (#517): validity is Core's one rule
    ///     (<see cref="BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun" />) — the CLI calls it
    ///     rather than restating "positive" or the ceiling itself; a present-but-invalid value is
    ///     rejected here rather than silently clamped, unlike a stored setting read at drain time.
    /// </summary>
    public async Task<int> SetEmbedRowsPerRunAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("rows");
        if (!BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(raw, out var parsed))
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: embed rows per run must be a positive integer, at most {BankMaintenanceConfigKeys.MaxEmbedRowsPerRun}");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal, parsed.ToString(),
            cancellationToken);
        await streams.WriteOutputLineAsync($"embed rows per run: {parsed}");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var checkpoint = BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, cancellationToken));
        var vacuum = BankMaintenanceConfigKeys.ParseVacuumIntervalDays(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, cancellationToken));
        var embedRowsPerRun = BankMaintenanceConfigKeys.ParseEmbedRowsPerRun(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal, cancellationToken));

        var current = await maintenanceStats.GetStatsAsync(cancellationToken);
        var previous = ReadPreviousStats();
        var delta = previous is null
            ? "no previous measurement"
            : FormatDelta(current.TotalBytes - previous.Value.TotalBytes, previous.Value.TotalBytes);

        await streams.WriteOutputLineAsync($"checkpoint interval: {checkpoint} min");
        await streams.WriteOutputLineAsync($"vacuum interval: {vacuum} days");
        await streams.WriteOutputLineAsync($"embed rows per run: {embedRowsPerRun}");
        await streams.WriteOutputLineAsync($"db file: {FormatBytes(current.DbBytes)}");
        await streams.WriteOutputLineAsync($"wal: {FormatBytes(current.WalBytes)}");
        await streams.WriteOutputLineAsync($"shm: {FormatBytes(current.ShmBytes)}");
        await streams.WriteOutputLineAsync($"on-disk total: {FormatBytes(current.TotalBytes)}");
        await streams.WriteOutputLineAsync(
            $"reclaimable: {FormatBytes(current.ReclaimableBytes)} (freelist {FormatBytes(current.FreelistBytes)}, " +
            $"uncheckpointed wal {FormatBytes(current.UncheckpointedWalBytes)})");
        await streams.WriteOutputLineAsync($"since last check: {delta}");

        WriteSidecar(current);
        return 0;
    }

    private (long TotalBytes, string Timestamp)? ReadPreviousStats()
    {
        try
        {
            if (!File.Exists(StatsSidecarPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(StatsSidecarPath));
            var root = doc.RootElement;
            return (root.GetProperty("totalBytes").GetInt64(), root.GetProperty("ts").GetString() ?? "?");
        }
        catch (Exception)
        {
            return null; // missing/corrupt sidecar: report "no previous measurement"
        }
    }

    private void WriteSidecar(BankStats stats)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            dbBytes = stats.DbBytes,
            walBytes = stats.WalBytes,
            totalBytes = stats.TotalBytes
        });
        File.WriteAllText(StatsSidecarPath, payload);
    }

    private static string FormatDelta(long deltaBytes, long previousTotalBytes)
    {
        var direction = deltaBytes switch
        {
            < 0 => "smaller",
            > 0 => "larger",
            _ => "unchanged"
        };
        var percent = previousTotalBytes == 0
            ? 0
            : Math.Abs(deltaBytes) * 100.0 / previousTotalBytes;
        return $"{FormatBytes(Math.Abs(deltaBytes))} {direction} " +
               $"({percent.ToString("0.0", CultureInfo.InvariantCulture)}%)";
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
            < 1024L * 1024 * 1024 =>
                $"{(bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
            _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} GB"
        };
}
