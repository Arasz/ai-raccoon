using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     #190: SyncService merge-reindex invalidates the content vector (embed_state -&gt;
///     'pending', embedding -&gt; NULL) but must also clear the structure vector, or structure
///     search returns entries whose content vector was deliberately invalidated.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SyncReindexStructureClearTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("sync-reindex-structure");
    private readonly SqliteConnectionFactory _factory;

    public SyncReindexStructureClearTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    [Fact]
    public async Task MergeReindex_InvalidatingContentVector_AlsoClearsTheStructureVector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);

        var contentVector = EmbeddingBlob.ToBytes(new float[384]);
        var structureVector = EmbeddingBlob.ToBytes(new float[384]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state)
            VALUES ('row-a', 'a.md', 'content A', 'a.md', 'project', 'acme', @now, @now, 'pending')
            RETURNING id
            """, new { now }, cancellationToken: ct));

        // Seed the structure vector (fires vec_structure_au), then the content vector (fires vec_entries_au) —
        // the shape merge-reindex is meant to invalidate.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET heading_path = @headingPath, structure_embedding = @structureVector WHERE id = @id",
            new { headingPath = "Guide > Prerequisites", structureVector, id }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embedding = @contentVector, embed_state = 'embedded' WHERE id = @id",
            new { contentVector, id }, cancellationToken: ct));

        (await VecRowCount(connection, "vec_entries", id, ct)).ShouldBe(1);
        (await VecRowCount(connection, "vec_structure", id, ct)).ShouldBe(1);

        // The exact merge-reindex UPDATE (SyncService.cs SyncCycleAsync, reindex step).
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE entries
            SET embed_state = 'pending', embedding = NULL, structure_embedding = NULL, heading_path = NULL
            WHERE id = @id
            """, new { id }, cancellationToken: ct));

        (await VecRowCount(connection, "vec_entries", id, ct)).ShouldBe(0);
        (await VecRowCount(connection, "vec_structure", id, ct)).ShouldBe(0,
            "merge-reindex must clear the structure vector alongside the content vector (#190)");

        var row = await connection.QuerySingleAsync<EntryRow>(new CommandDefinition(
            "SELECT structure_embedding AS StructureEmbedding, heading_path AS HeadingPath FROM entries WHERE id = @id",
            new { id }, cancellationToken: ct));
        row.StructureEmbedding.ShouldBeNull();
        row.HeadingPath.ShouldBeNull("an invalidated row must return to the structure-heal candidate state");

        var vecStructureCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM vec_structure", cancellationToken: ct));
        var structureEmbeddingCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM entries WHERE structure_embedding IS NOT NULL", cancellationToken: ct));
        vecStructureCount.ShouldBe(structureEmbeddingCount,
            "vec_structure must stay in lockstep with entries.structure_embedding");
    }

    [Fact]
    public async Task MarkStructure_AfterAConcurrentReindexClearedTheRow_DoesNotResurrectTheVecStructureRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state, embedding)
            VALUES ('row-b', 'b.md', 'content B', 'b.md', 'project', 'acme', @now, @now, 'embedded', @embedding)
            RETURNING id
            """, new { now, embedding = EmbeddingBlob.ToBytes(new float[384]) }, cancellationToken: ct));

        // The heal candidate SELECT would have picked this row up here (embedded, heading_path
        // IS NULL). Simulate a concurrent sync merge-reindex clearing it before the heal write lands.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE entries
            SET embed_state = 'pending', embedding = NULL, structure_embedding = NULL, heading_path = NULL
            WHERE id = @id
            """, new { id }, cancellationToken: ct));

        // The heal pass's write, arriving after the reindex.
        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.MarkStructure,
            new { id, headingPath = "Guide > Prerequisites", structureEmbedding = EmbeddingBlob.ToBytes(new float[384]) },
            cancellationToken: ct));

        (await VecRowCount(connection, "vec_structure", id, ct)).ShouldBe(0,
            "MarkStructure must not resurrect vec_structure for a row the clear-arm already invalidated");

        var row = await connection.QuerySingleAsync<EntryRow>(new CommandDefinition(
            "SELECT structure_embedding AS StructureEmbedding, heading_path AS HeadingPath FROM entries WHERE id = @id",
            new { id }, cancellationToken: ct));
        row.StructureEmbedding.ShouldBeNull();
        row.HeadingPath.ShouldBeNull();
    }

    private static Task<long> VecRowCount(SqliteConnection connection, string table, long id, CancellationToken ct) =>
        connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM {table} WHERE rowid = @id", new { id }, cancellationToken: ct));

    private sealed class EntryRow
    {
        public byte[]? StructureEmbedding { get; set; }

        public string? HeadingPath { get; set; }
    }
}
