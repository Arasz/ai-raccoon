using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Seeds the two vec0 legs (<c>vec_entries</c>/<c>vec_structure</c>) for
///     <c>ProjectIdCensus</c>'s join-plan and attribution tests. <c>alpha</c> owns two
///     content-embedded rows and one structure-embedded row; <c>beta</c> owns one of each —
///     asymmetric on purpose, so a leg-swap mutation between <c>VecEntriesCountSql</c> and
///     <c>VecStructureCountSql</c> flips a count and is caught (a 1/1 split on both ids could
///     not distinguish the legs). A project_id-NULL embedded row and a vec-less entry prove
///     exclusion and zero-count handling. Shared by <c>ProjectIdCensusTests</c> and
///     <c>RepairEndpointTests</c> so the two seedings cannot drift apart.
/// </summary>
public static class VecLegSeeder
{
    public static async Task SeedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var vector = EmbeddingBlob.ToBytes(new float[384]);

        async Task<long> InsertEntry(string hash, string? projectId, string scope = "project")
        {
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', @scope, @projectId, 'ctx', @now, @now, 'pending') RETURNING id",
                new { hash, scope, projectId, now }, cancellationToken: cancellationToken));
        }

        async Task EmbedContent(long id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET embedding = @embedding, embed_state = 'embedded' WHERE id = @id",
                new { embedding = vector, id }, cancellationToken: cancellationToken));
        }

        async Task EmbedStructure(long id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET structure_embedding = @embedding WHERE id = @id",
                new { embedding = vector, id }, cancellationToken: cancellationToken));
        }

        await EmbedContent(await InsertEntry("vec-alpha-content-1", "alpha"));
        await EmbedContent(await InsertEntry("vec-alpha-content-2", "alpha"));
        await EmbedStructure(await InsertEntry("vec-alpha-structure", "alpha"));

        await EmbedContent(await InsertEntry("vec-beta-content", "beta"));
        await EmbedStructure(await InsertEntry("vec-beta-structure", "beta"));

        // Embedded but project_id IS NULL: must be excluded from every count. Scope is 'shared' --
        // ContextKeyExpression's 'project'/'custom' branches concatenate project_id and go NULL
        // (rejected by vec0's TEXT metadata column) when project_id itself is NULL.
        await EmbedContent(await InsertEntry("vec-null-content", null, "shared"));

        // Never embedded at all: must not appear in either vec leg's counts.
        await InsertEntry("vec-gamma-no-vec", "gamma");
    }
}
