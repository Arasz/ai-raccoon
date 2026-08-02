using AiRaccon.Core.Rating;
using Microsoft.Data.Sqlite;

namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>Local-only per-hash metadata row (spec §5.3); never synced.</summary>
public sealed record MetaEntry(
    string Hash,
    string ProjectId,
    string? Context,
    string? AgentId,
    long CreatedAt,
    int AccessCount,
    long? LastAccessedAt,
    double Rating,
    int? TtlDays);

/// <summary>CRUD over the local-only raccon_meta.db; rating bumps follow RatingPolicy from Core.</summary>
public sealed class MetaStore
{
    private readonly SqliteConnectionFactory _factory;

    public MetaStore(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<MetaEntry> UpsertAccessAsync(
        string projectId, string hash, string? agentId = null, string? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = await GetEntryCoreAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);

        var accessCount = existing is null ? 1 : existing.AccessCount + 1;
        var createdAt = existing?.CreatedAt ?? now;
        var ageDays = Math.Max(0, (now - createdAt) / 86_400.0);
        var rating = RatingPolicy.Rating(
            RatingPolicy.DefaultBaseScore, accessCount, ageDays, RatingPolicy.DefaultHalfLifeDays);

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO entries (hash, project_id, context, agent_id, created_at, access_count, last_accessed_at, rating, ttl_days)
            VALUES (@hash, @projectId, @context, @agentId, @createdAt, @accessCount, @lastAccessedAt, @rating, NULL)
            ON CONFLICT(hash) DO UPDATE SET
                context = COALESCE(@context, context),
                agent_id = COALESCE(@agentId, agent_id),
                access_count = @accessCount,
                last_accessed_at = @lastAccessedAt,
                rating = @rating
            """;
        upsert.Parameters.AddWithValue("@hash", hash);
        upsert.Parameters.AddWithValue("@projectId", projectId);
        upsert.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@agentId", (object?)agentId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@createdAt", createdAt);
        upsert.Parameters.AddWithValue("@accessCount", accessCount);
        upsert.Parameters.AddWithValue("@lastAccessedAt", now);
        upsert.Parameters.AddWithValue("@rating", rating);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MetaEntry(
            hash, projectId, context ?? existing?.Context, agentId ?? existing?.AgentId,
            createdAt, accessCount, now, rating, existing?.TtlDays);
    }

    public async Task<MetaEntry?> GetEntryAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);
        return await GetEntryCoreAsync(connection, null, hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetaEntry>> ListEntriesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, project_id, context, agent_id, created_at, access_count, last_accessed_at, rating, ttl_days
            FROM entries
            WHERE project_id = @projectId
            ORDER BY created_at DESC, rowid DESC
            """;
        command.Parameters.AddWithValue("@projectId", projectId);

        var entries = new List<MetaEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId";
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@projectId", projectId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS entries (
                hash TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                context TEXT,
                agent_id TEXT,
                created_at INTEGER NOT NULL,
                access_count INTEGER NOT NULL DEFAULT 0,
                last_accessed_at INTEGER,
                rating REAL NOT NULL DEFAULT 0.5,
                ttl_days INTEGER
            )
            """;
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_entries_project ON entries(project_id, context)";
        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MetaEntry?> GetEntryCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string hash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT hash, project_id, context, agent_id, created_at, access_count, last_accessed_at, rating, ttl_days
            FROM entries
            WHERE hash = @hash
            """;
        command.Parameters.AddWithValue("@hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    private static MetaEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt64(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetDouble(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8));
}
