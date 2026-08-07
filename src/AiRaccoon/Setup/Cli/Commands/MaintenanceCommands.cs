using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot bank-maintenance verb handlers: interval, vacuum-interval, list — the CLI-only
///     channel for the maintenance service. list additionally reports live bank disk stats
///     (db/WAL sizes, reclaimable bytes, delta vs the previous check via a stats sidecar).
/// </summary>
public sealed class MaintenanceCommands(SqliteConnectionFactory factory) : IMaintenanceCommands
{
    private string StatsSidecarPath => Path.Combine(Path.GetDirectoryName(factory.BankPath)!, "maintenance-stats.json");

    public async Task<int> SetCheckpointIntervalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var minutes = parseResult.GetValue<string>("minutes");
        if (!int.TryParse(minutes, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: checkpoint interval must be a positive number of minutes");
            return 1;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"checkpoint interval: {parsed} min");
        return 0;
    }

    public async Task<int> SetVacuumIntervalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var days = parseResult.GetValue<string>("days");
        if (!int.TryParse(days, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: vacuum interval must be a positive number of days");
            return 1;
        }

        if (parsed > BankMaintenanceConfigKeys.MaxVacuumIntervalDays)
        {
            await stderr.WriteLineAsync(
                $"ai-raccoon: vacuum interval must be at most {BankMaintenanceConfigKeys.MaxVacuumIntervalDays} days");
            return 1;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"vacuum interval: {parsed} days");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, TextWriter stdout, CancellationToken cancellationToken)
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

        await stdout.WriteLineAsync($"checkpoint interval: {checkpoint} min");
        await stdout.WriteLineAsync($"vacuum interval: {vacuum} days");
        await stdout.WriteLineAsync($"db file: {FormatBytes(current.DbBytes)}");
        await stdout.WriteLineAsync($"wal: {FormatBytes(current.WalBytes)}");
        await stdout.WriteLineAsync($"shm: {FormatBytes(current.ShmBytes)}");
        await stdout.WriteLineAsync($"on-disk total: {FormatBytes(current.TotalBytes)}");
        await stdout.WriteLineAsync(
            $"reclaimable: {FormatBytes(current.ReclaimableBytes)} (freelist {FormatBytes(current.FreelistBytes)}, " +
            $"uncheckpointed wal {FormatBytes(current.UncheckpointedWalBytes)})");
        await stdout.WriteLineAsync($"since last check: {delta}");

        WriteSidecar(current);
        return 0;
    }

    private async Task<BankStats> ReadStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);

        long PageSize() => Convert.ToInt64(Scalar("PRAGMA page_size"));
        long PageCount() => Convert.ToInt64(Scalar("PRAGMA page_count"));
        long FreelistCount() => Convert.ToInt64(Scalar("PRAGMA freelist_count"));

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
            DbBytes: pageCount * pageSize,
            WalBytes: File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            ShmBytes: File.Exists(shmPath) ? new FileInfo(shmPath).Length : 0,
            FreelistBytes: freelistCount * pageSize,
            UncheckpointedWalBytes: uncheckpointedFrames * pageSize);
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
        return $"{FormatBytes(Math.Abs(deltaBytes))} {direction} ({percent:0.0}%)";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
    };

    private sealed record BankStats(
        long DbBytes, long WalBytes, long ShmBytes, long FreelistBytes, long UncheckpointedWalBytes)
    {
        public long TotalBytes => DbBytes + WalBytes + ShmBytes;
        public long ReclaimableBytes => FreelistBytes + UncheckpointedWalBytes;
    }
}
