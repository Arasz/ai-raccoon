using AiRaccoon.Infrastructure.Sqlite;
using Dapper;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>One registered watch row: identity + catch-up watermark (timestamps are Unix epoch seconds).</summary>
public sealed record WatchRegistration(string ProjectId, string Path, long CreatedAt, long LastChangeTs);

/// <summary>Persistence seam for watches + per-file fingerprints (S4 unit tests fake this).</summary>
public interface IWatchStore
{
    Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default);

    Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default);

    Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default);

    Task<string?> GetFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper impl of IWatchStore over the watches/watch_files tables (MemorySql consts).</summary>
public sealed class WatchStore(SqliteConnectionFactory factory) : IWatchStore
{
    public async Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.InsertWatchIfAbsent,
                    new { projectId, path, createdAt, lastChangeTs }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.DeleteWatch, new { projectId, path },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<WatchRegistration>(
                new CommandDefinition(MemorySql.SelectWatches, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.ToArray();
    }

    public async Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpdateWatchLastChange, new { projectId, path, lastChangeTs },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetFileHashAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(MemorySql.SelectWatchFile, new { projectId, path },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpsertWatchFile,
                    new { projectId, path, fileHash, updatedAt }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
