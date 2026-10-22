using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     v20→v21 (ADR-0141): the memory vector tables gain the row's declared kind as a second vec0
///     partition key. The tables are rebuilt from the stored embedding blobs, because a native
///     partition key cannot be updated in place — so the vectors are already paid for and the
///     rebuild must not lose one: the same rows, the same bytes, the same dimension, each in the
///     partition its row belongs to.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedEntryMigrationTests
{
    private sealed record KindRow(string Hash, string? Kind);
    private sealed record VectorBlobRow(long Rowid, byte[] Embedding);

    private const int Dimension = 384;

    /// <summary>One seeded row: its kind, and the vector it carries.</summary>
    private sealed record SeedRow(string Hash, string Path, string? SourceFile, EntryKind Kind, byte[] Embedding);

    [RetryFact]
    public async Task EnsureAsync_UpgradesAV20Bank_TypedAndWithoutLosingAVector()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var fileEmbedding = Embedding(1f);
        var memoryEmbedding = Embedding(2f);
        await SeedAsync(connection,
            new SeedRow("file-hash", "docs/guide.md", "docs/guide.md", EntryKind.File, fileEmbedding),
            new SeedRow("note-hash", "9f2c.md", null, EntryKind.Memory, memoryEmbedding));

        // A bank at v20: the kind column, the vec tables without the kind partition.
        await RebuildVecTablesUntypedAsync(connection);
        await StampAsync(connection, 20);

        var before = await ReadVectorsAsync(connection);
        before.Count.ShouldBe(2, "the bank holds two vectors before the upgrade");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var after = await ReadVectorsAsync(connection);
        after.Count.ShouldBe(2, "the upgrade must not lose a vector");
        after.Keys.ShouldBe(before.Keys, "the upgrade must not change which rows carry a vector");
        foreach (var (rowid, blob) in before)
        {
            after[rowid].ShouldBe(blob, $"the upgrade must not change the bytes of row {rowid}");
        }

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_entries'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull();
        sql.Contains("type TEXT partition key", StringComparison.Ordinal).ShouldBeTrue(
            "the memory vector table carries the kind as a native partition key");
    }

    /// <summary>
    ///     Each vector lands in the partition its ROW is in: the kind the vector is filed under is
    ///     the row's own <c>entry_type</c>, read back from the table's partition column. Checked on
    ///     the UPGRADED bank, because the rebuild is what files the vectors there.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_PlacesEachVectorInItsRowsKindPartition()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await SeedAsync(connection,
            new SeedRow("file-hash", "docs/guide.md", "docs/guide.md", EntryKind.File, Embedding(1f)),
            new SeedRow("note-hash", "9f2c.md", null, EntryKind.Memory, Embedding(2f)));

        await RebuildVecTablesUntypedAsync(connection);
        await StampAsync(connection, 20);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var kinds = await ReadVectorKindsAsync(connection);
        kinds.Values.ShouldContain(EntryKindWireNames.File);
        kinds.Values.ShouldContain(EntryKindWireNames.Memory);

        var rowKinds = await connection.QueryAsync<KindRow>(
            "SELECT hash, entry_type AS Kind FROM entries");
        foreach (var (hash, kind) in rowKinds)
        {
            kinds.ShouldContainKey(hash);
            kinds[hash].ShouldBe(kind, $"the vector of {hash} must sit in its row's kind partition");
        }
    }

    /// <summary>
    ///     On a bank that already has the typed tables, the embed transition files the vector under
    ///     its row's kind — the placement the typed layout depends on, and the one a later search
    ///     restriction reads.
    /// </summary>
    [RetryFact]
    public async Task TheEmbedTransition_FilesTheVectorUnderItsRowsKind()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await SeedAsync(connection,
            new SeedRow("file-hash", "docs/guide.md", "docs/guide.md", EntryKind.File, Embedding(1f)),
            new SeedRow("note-hash", "9f2c.md", null, EntryKind.Memory, Embedding(2f)));

        var kinds = await ReadVectorKindsAsync(connection);
        kinds["file-hash"].ShouldBe(EntryKindWireNames.File);
        kinds["note-hash"].ShouldBe(EntryKindWireNames.Memory);
    }

    /// <summary>
    ///     The upgrade is re-runnable: a second pass finds the tables already typed and leaves the
    ///     vectors alone.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_Twice_LeavesTheTypedVectorsAlone()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await SeedAsync(connection,
            new SeedRow("file-hash", "docs/guide.md", "docs/guide.md", EntryKind.File, Embedding(1f)),
            new SeedRow("note-hash", "9f2c.md", null, EntryKind.Memory, Embedding(2f)));

        await RebuildVecTablesUntypedAsync(connection);
        await StampAsync(connection, 20);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var afterFirst = await ReadVectorsAsync(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var afterSecond = await ReadVectorsAsync(connection);

        afterSecond.Keys.ShouldBe(afterFirst.Keys);
        foreach (var (rowid, blob) in afterFirst)
        {
            afterSecond[rowid].ShouldBe(blob, $"a second pass must not change the bytes of row {rowid}");
        }
    }

    /// <summary>
    ///     A bank that went through the first v20 build carries a NULLABLE kind column and its NULL
    ///     rows, and the ladder never returns early for it — so the v21 step classifies them. A row
    ///     whose kind stayed NULL would file its vector under a NULL partition: a row no typed query
    ///     could reach.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_UpgradesANullableV20Bank_ClassifiesEveryRow()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await SeedAsync(connection,
            new SeedRow("file-hash", "docs/guide.md", "docs/guide.md", EntryKind.File, Embedding(1f)),
            new SeedRow("note-hash", "9f2c.md", null, EntryKind.Memory, Embedding(2f)));

        // The first v20 build's shape: a nullable column, and the kinds the backfill never reached.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER IF EXISTS vec_entries_au", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER IF EXISTS vec_structure_au", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE entries DROP COLUMN entry_type", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE entries ADD COLUMN entry_type TEXT NULL", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET entry_type = NULL", cancellationToken: TestContext.Current.CancellationToken));
        await StampAsync(connection, 20);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var kinds = await connection.QueryAsync<KindRow>(
            "SELECT hash, entry_type AS Kind FROM entries");
        foreach (var (hash, kind) in kinds)
        {
            kind.ShouldNotBeNull($"the row {hash} must carry a kind after the upgrade");
            kind.ShouldNotBe("none", $"the row {hash} must carry a kind after the upgrade");
        }

        // No vector is filed under a NULL partition.
        var vectorKinds = await ReadVectorKindsAsync(connection);
        foreach (var (hash, kind) in vectorKinds)
        {
            kind.ShouldNotBeNull($"the vector of {hash} must be filed under a kind");
        }
    }

    /// <summary>Seeds a row and embeds it, so the vec0 trigger places the vector.</summary>
    private static async Task SeedAsync(SqliteConnection connection, params SeedRow[] rows)
    {
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at,
                                      embed_state, embedding, entry_type)
                VALUES (@hash, @path, 'seeded', @sourceFile, 'project', 'acme', 1, 1, 'pending', @embedding, @kind)
                """,
                new
                {
                    hash = row.Hash,
                    path = row.Path,
                    sourceFile = row.SourceFile,
                    embedding = row.Embedding,
                    kind = new EntryKindClassifier().ToStoredValue(row.Kind)
                },
                cancellationToken: TestContext.Current.CancellationToken));

            // The embed transition is what places the vector: the trigger reads the row's kind.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash",
                new { hash = row.Hash }, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    ///     Rebuilds the memory vector tables WITHOUT the kind partition — the shape a v20 bank
    ///     has — keeping the vectors, so the upgrade has something to preserve.
    /// </summary>
    private static async Task RebuildVecTablesUntypedAsync(SqliteConnection connection)
    {
        foreach (var table in new[] { "vec_entries", "vec_structure" })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DROP TABLE IF EXISTS {table}", cancellationToken: TestContext.Current.CancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                $"CREATE VIRTUAL TABLE {table} USING vec0(ctx TEXT partition key, embedding float[{Dimension}] distance_metric=cosine)",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             INSERT INTO vec_entries(rowid, ctx, embedding)
             SELECT id, CASE WHEN scope = 'shared' THEN 'shared' ELSE 'project:' || COALESCE(project_id, '') END, embedding
             FROM entries WHERE embed_state = 'embedded' AND embedding IS NOT NULL
             """,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The vectors the bank holds: rowid to the raw bytes.</summary>
    private static async Task<Dictionary<long, byte[]>> ReadVectorsAsync(SqliteConnection connection)
    {
        var rows = await connection.QueryAsync<VectorBlobRow>(
            "SELECT rowid, embedding FROM vec_entries");
        return rows.ToDictionary(r => r.Rowid, r => r.Embedding);
    }

    /// <summary>The kind each vector is filed under, read back from the partition column.</summary>
    private static async Task<Dictionary<string, string>> ReadVectorKindsAsync(SqliteConnection connection)
    {
        var rows = await connection.QueryAsync<KindRow>(
            "SELECT e.hash, v.type AS Kind FROM vec_entries v JOIN entries e ON e.id = v.rowid");
        return rows.ToDictionary(r => r.Hash, r => r.Kind!);
    }

    /// <summary>Stamps the bank as an older version and clears the digest, so the ladder runs.</summary>
    private static async Task StampAsync(SqliteConnection connection, int version)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {version}", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static byte[] Embedding(float seed)
    {
        var bytes = new byte[Dimension * sizeof(float)];
        for (var i = 0; i < Dimension; i++)
        {
            var value = seed + i / (float)Dimension;
            BitConverter.GetBytes(value).CopyTo(bytes, i * sizeof(float));
        }

        return bytes;
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
