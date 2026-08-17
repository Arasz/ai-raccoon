using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The server side of <see cref="IMaintenanceStatsStore" /> (ADR-0075 amendment): opens its own
///     bank connection and reads page/freelist pragmas plus a PASSIVE checkpoint, exactly what
///     `settings maintenance list` used to do locally before this change — only the process that
///     does it moved.
/// </summary>
public sealed class SqliteMaintenanceStatsStore(ISqliteConnectionFactory factory) : IMaintenanceStatsStore
{
    public async Task<BankStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

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
}
