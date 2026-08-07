using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Bank maintenance loop: WAL checkpoint (TRUNCATE) at startup, shutdown and on the
///     checkpoint cadence; VACUUM + ANALYZE on the vacuum cadence. See the design doc.
/// </summary>
public sealed partial class BankMaintenanceHostedService : BackgroundService
{
    /// <summary>Contended checkpoints defer quickly instead of blocking the maintenance connection.</summary>
    private const int CheckpointBusyTimeoutMs = 250;

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BankMaintenanceHostedService> _logger;
    private DateTimeOffset? _lastVacuumUtc;

    public BankMaintenanceHostedService(SqliteConnectionFactory factory, TimeProvider timeProvider,
        ILogger<BankMaintenanceHostedService> logger)
    {
        _factory = factory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup pass: one checkpoint before any traffic bounds the WAL for short-lived
        // processes; a failure logs and must not abort startup.
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.RunFailed(_logger, ex);
        }

        using var timer = new PeriodicTimer(await ReadCheckpointIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.RunFailed(_logger, ex);
            }

            // Re-read the interval so config changes apply without a restart.
            timer.Period = await ReadCheckpointIntervalSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RestoreBusyTimeoutAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.ShutdownCheckpointFailed(_logger, ex);
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>One maintenance pass: WAL checkpoint, then vacuum+analyze if due. Test seam.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);

            var now = _timeProvider.GetUtcNow();
            if (_lastVacuumUtc is null)
            {
                // Seed on first run: short-lived processes never vacuum (the clock resets per process).
                _lastVacuumUtc = now;
                return;
            }

            var vacuumIntervalDays = await ReadVacuumIntervalDaysSafeAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (now - _lastVacuumUtc.Value < TimeSpan.FromDays(vacuumIntervalDays))
            {
                return;
            }

            try
            {
                await using (var vacuum = connection.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM";
                    await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
            {
                // A contended VACUUM defers like a contended checkpoint: Warning, clock
                // untouched so the next tick retries — never an Error.
                Log.VacuumDeferred(_logger);
                return;
            }

            // VACUUM drops sqlite_stat1, so ANALYZE must run after it.
            await using (var analyze = connection.CreateCommand())
            {
                analyze.CommandText = "ANALYZE";
                await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The VACUUM rewrote the whole file through the WAL; truncate it now, not at the next tick.
            await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);

            _lastVacuumUtc = now;
            Log.Vacuum(_logger);
        }
        finally
        {
            // The connection returns to the pool: restore the factory's busy timeout so a
            // future borrower never inherits the maintenance connection's short one.
            await RestoreBusyTimeoutAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Wal-checkpoint (TRUNCATE), reading the returned busy|log|checkpointed tuple:
    ///     busy&gt;0 means readers/writers pin the WAL — defer (Warning), never an error.
    /// </summary>
    private async Task RunCheckpointAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // A short busy timeout defers a contended checkpoint instead of blocking the
        // maintenance connection for seconds; the next tick retries.
        await using (var busy = connection.CreateCommand())
        {
            busy.CommandText = $"PRAGMA busy_timeout={CheckpointBusyTimeoutMs}";
            await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var busyFrames = reader.GetInt32(0);
        if (busyFrames > 0)
        {
            Log.CheckpointDeferred(_logger, busyFrames);
        }
        else
        {
            Log.Checkpoint(_logger);
        }
    }

    /// <summary>Re-applies the factory's busy timeout (5000 ms) before the connection returns to the pool.</summary>
    private static async Task RestoreBusyTimeoutAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimeSpan> ReadCheckpointIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            var minutes = await ReadIntervalAsync(connection,
                BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal,
                BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes, cancellationToken).ConfigureAwait(false);
            return TimeSpan.FromMinutes(minutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(_logger, ex);
            return TimeSpan.FromMinutes(BankMaintenanceConfigKeys.DefaultCheckpointIntervalMinutes);
        }
    }

    private async Task<int> ReadVacuumIntervalDaysSafeAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadIntervalAsync(connection, BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal,
                BankMaintenanceConfigKeys.ParseVacuumIntervalDays, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(_logger, ex);
            return BankMaintenanceConfigKeys.DefaultVacuumIntervalDays;
        }
    }

    private static async Task<int> ReadIntervalAsync(SqliteConnection connection, string key,
        Func<string?, int> parse, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        return parse(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 510, Level = LogLevel.Information, Message = "Bank WAL checkpoint complete")]
        public static partial void Checkpoint(ILogger logger);

        [LoggerMessage(EventId = 511, Level = LogLevel.Warning,
            Message = "Bank WAL checkpoint deferred: {Busy} frames busy (readers or a writer pin the WAL)")]
        public static partial void CheckpointDeferred(ILogger logger, int busy);

        [LoggerMessage(EventId = 512, Level = LogLevel.Information, Message = "Bank vacuum + analyze complete")]
        public static partial void Vacuum(ILogger logger);

        [LoggerMessage(EventId = 516, Level = LogLevel.Warning,
            Message = "Bank vacuum deferred: the bank is busy (retries next tick)")]
        public static partial void VacuumDeferred(ILogger logger);

        [LoggerMessage(EventId = 513, Level = LogLevel.Error, Message = "Bank maintenance run failed")]
        public static partial void RunFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 514, Level = LogLevel.Warning,
            Message = "Maintenance interval read failed; falling back to the default")]
        public static partial void IntervalReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 515, Level = LogLevel.Warning, Message = "Shutdown checkpoint failed")]
        public static partial void ShutdownCheckpointFailed(ILogger logger, Exception exception);
    }
}
