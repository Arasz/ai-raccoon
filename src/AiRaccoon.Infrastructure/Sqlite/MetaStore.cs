using AiRaccoon.Core.Rating;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

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

/// <summary>CRUD over the local-only raccoon_meta.db; rating bumps follow RatingPolicy from Core.</summary>
public class MetaStore(SqliteConnectionFactory factory)
{
    private const string SelectByHash = """
                                        SELECT hash AS Hash, project_id AS ProjectId, context AS Context, agent_id AS AgentId,
                                               created_at AS CreatedAt, access_count AS AccessCount, last_accessed_at AS LastAccessedAt,
                                               rating AS Rating, ttl_days AS TtlDays
                                        FROM entries
                                        WHERE hash = @hash
                                        """;

    private const string ListByProject = """
                                         SELECT hash AS Hash, project_id AS ProjectId, context AS Context, agent_id AS AgentId,
                                                created_at AS CreatedAt, access_count AS AccessCount, last_accessed_at AS LastAccessedAt,
                                                rating AS Rating, ttl_days AS TtlDays
                                         FROM entries
                                         WHERE project_id = @projectId
                                         ORDER BY created_at DESC, rowid DESC
                                         """;

    public async Task<MetaEntry> UpsertAccessAsync(
        string projectId, string hash, string? agentId = null, string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = await connection.QueryFirstOrDefaultAsync<MetaRow>(
                new CommandDefinition(SelectByHash, new { hash }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var accessCount = existing is null ? 1 : existing.AccessCount + 1;
        var createdAt = existing?.CreatedAt ?? now;
        var ageDays = Math.Max(0, (now - createdAt) / 86_400.0);
        var rating = RatingPolicy.Rating(
            RatingPolicy.DefaultBaseScore, accessCount, ageDays, RatingPolicy.DefaultHalfLifeDays);

        await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO entries (hash, project_id, context, agent_id, created_at, access_count, last_accessed_at, rating, ttl_days)
                    VALUES (@hash, @projectId, @context, @agentId, @createdAt, @accessCount, @lastAccessedAt, @rating, NULL)
                    ON CONFLICT(hash) DO UPDATE SET
                        context = COALESCE(@context, context),
                        agent_id = COALESCE(@agentId, agent_id),
                        access_count = @accessCount,
                        last_accessed_at = @lastAccessedAt,
                        rating = @rating
                    """,
                    new { hash, projectId, context, agentId, createdAt, accessCount, lastAccessedAt = now, rating },
                    transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MetaEntry(
            hash, projectId, context ?? existing?.Context, agentId ?? existing?.AgentId,
            createdAt, accessCount, now, rating, existing?.TtlDays);
    }

    public virtual async Task<MetaEntry?> GetEntryAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<MetaRow>(
                new CommandDefinition(SelectByHash, new { hash }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    public async Task<IReadOnlyList<MetaEntry>> ListEntriesAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<MetaRow>(
            new CommandDefinition(
                ListByProject,
                new { projectId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToEntry)];
    }

    public virtual async Task<bool> DeleteAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId",
                new { hash, projectId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
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
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "CREATE INDEX IF NOT EXISTS idx_entries_project ON entries(project_id, context)",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static MetaEntry ToEntry(MetaRow row) =>
        new(
            row.Hash, row.ProjectId, row.Context, row.AgentId,
            row.CreatedAt, row.AccessCount, row.LastAccessedAt, row.Rating, row.TtlDays);

    /// <summary>Dapper materialization target: settable properties map nullable columns cleanly (the record's nullable params do not).</summary>
    private sealed class MetaRow
    {
        public string Hash { get; set; } = "";

        public string ProjectId { get; set; } = "";

        public string? Context { get; set; }

        public string? AgentId { get; set; }

        public long CreatedAt { get; set; }

        public int AccessCount { get; set; }

        public long? LastAccessedAt { get; set; }

        public double Rating { get; set; }

        public int? TtlDays { get; set; }
    }
}
