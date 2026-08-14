using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot bank-maintenance verb handlers: interval, vacuum-interval, list — the CLI-only
///     channel for the maintenance service. list additionally reports live bank disk stats
///     (db/WAL sizes, reclaimable bytes, delta vs the previous check via a stats sidecar).
/// </summary>
public sealed class MaintenanceCommands(ISqliteConnectionFactory factory)
{
    private string StatsSidecarPath => Path.Combine(Path.GetDirectoryName(factory.BankPath)!, "maintenance-stats.json");

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

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var checkpoint = BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, cancellationToken));
        var vacuum = BankMaintenanceConfigKeys.ParseVacuumIntervalDays(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, cancellationToken));

        var current = await ReadStatsAsync(cancellationToken);
        var previous = ReadPreviousStats();
        var delta = previous is null
            ? "no previous measurement"
            : FormatDelta(current.TotalBytes - previous.Value.TotalBytes, previous.Value.TotalBytes);

        await streams.WriteOutputLineAsync($"checkpoint interval: {checkpoint} min");
        await streams.WriteOutputLineAsync($"vacuum interval: {vacuum} days");
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

    private async Task<BankStats> ReadStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);

        long PageSize()
        {
            return Convert.ToInt64(Scalar("PRAGMA page_size"));
        }

        long PageCount()
        {
            return Convert.ToInt64(Scalar("PRAGMA page_count"));
        }

        long FreelistCount()
        {
            return Convert.ToInt64(Scalar("PRAGMA freelist_count"));
        }

        object? Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        var pageSize = PageSize();
        var pageCount = PageCount();
        var freelistCount = FreelistCount();

        // PASSIVE checkpoint: non-blocking, reports busy|log|checkpointed without truncating;
        // log = frames not yet checkpointed, i.e. the WAL content a TRUNCATE would still apply.
        long uncheckpointedFrames;
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            uncheckpointedFrames = reader.GetInt64(1);
        }

        var dbPath = factory.BankPath;
        var walPath = dbPath + "-wal";
        var shmPath = dbPath + "-shm";
        return new BankStats(
            pageCount * pageSize,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            File.Exists(shmPath) ? new FileInfo(shmPath).Length : 0,
            freelistCount * pageSize,
            uncheckpointedFrames * pageSize);
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

    private sealed record BankStats(
        long DbBytes,
        long WalBytes,
        long ShmBytes,
        long FreelistBytes,
        long UncheckpointedWalBytes)
    {
        public long TotalBytes => DbBytes + WalBytes + ShmBytes;
        public long ReclaimableBytes => FreelistBytes + UncheckpointedWalBytes;
    }
}
