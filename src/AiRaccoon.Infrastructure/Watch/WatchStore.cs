using AiRaccoon.Infrastructure.Sqlite;
using Dapper;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>One registered watch row: identity + catch-up watermark (timestamps are Unix epoch seconds).</summary>
public sealed record WatchRegistration(string ProjectId, string Path, long CreatedAt, long LastChangeTs);

/// <summary>Persistence seam for watches + per-file fingerprints (docs/plans/file-watcher-implementation.md S4 unit tests fake this).</summary>
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

    /// <summary>Lists every fingerprinted file path for the project (catch-up reconciliation).</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string projectId, CancellationToken cancellationToken = default);
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
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var pathPrefix = LikePattern.Escape(path) + "/%";
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.DeleteWatchFilesByProjectPathCascade,
                        new { projectId, path, pathPrefix }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.DeleteWatch, new { projectId, path },
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<WatchRegistration>(
                new CommandDefinition(MemorySql.SelectWatches, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
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

    public async Task<IReadOnlyList<string>> ListFilesAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>(
                new CommandDefinition(MemorySql.SelectWatchFilesByProject, new { projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }
}
