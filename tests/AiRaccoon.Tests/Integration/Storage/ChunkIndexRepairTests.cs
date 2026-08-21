using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     GH #371: existing banks hold chunk_index values derived from row-id order, indistinguishable
///     from correct ones without re-deriving. <see cref="ChunkIndexRepair" /> re-derives them from
///     each source_file still on disk — never guesses for one that is gone — and never
///     INSERTs/DELETEs a row, only UPDATEs chunk_index/total_chunks.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkIndexRepairTests : IDisposable
{
    private const string ProjectId = "acme";
    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-index-repair");
    private readonly SqliteConnectionFactory _factory;

    public ChunkIndexRepairTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task RunAsync_ReDerivesPositionFromTheSourceFile_WhenTheStoredValueWasIdOrderWrong()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);

        // Seeded the way the defect left it: "para two" (the real index-1 chunk) carries the
        // HIGHEST chunk_index, exactly what an id-order recompute would do to an edited-then-reinserted
        // middle chunk (GH #371).
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para three", 1), (file, "para two", 2));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        // "para one" was already seeded at its correct position (0); only the swapped two-and-three move.
        report.RowsRepositioned.ShouldBe(2);
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three"].ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_SetsTheUnknownSentinel_WhenTheSourceFileNoLongerExists()
    {
        var missing = Path.Combine(_dataRoot, "gone.md");
        await using var connection = await OpenSeededAsync((missing, "some content", 0));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.RowsSetToUnknown.ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT chunk_index FROM entries WHERE path = @path", new { path = missing })).ShouldBe(-1);
    }

    [Fact]
    public async Task RunAsync_DryRun_ReportsWithoutWriting()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one", 0), (file, "para two", 5));

        var report = await Repair().RunAsync(connection, apply: false, TestContext.Current.CancellationToken);

        report.RowsRepositioned.ShouldBe(1, "the dry run must report what a real run would fix");
        (await connection.ExecuteScalarAsync<long>(
            "SELECT chunk_index FROM entries WHERE value = 'para two'")).ShouldBe(5, "a dry run must not write");
    }

    /// <summary>The repair's hard constraint: a pure UPDATE, provable by row count and hash set surviving unchanged.</summary>
    [Fact]
    public async Task RunAsync_IsNonDestructive_RowCountAndEveryHashSurvive()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para three", 1), (file, "para two", 2));

        var before = (await connection.QueryAsync<string>("SELECT hash FROM entries")).OrderBy(h => h, StringComparer.Ordinal).ToList();

        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        var after = (await connection.QueryAsync<string>("SELECT hash FROM entries")).OrderBy(h => h, StringComparer.Ordinal).ToList();
        after.ShouldBe(before, "the repair must only UPDATE chunk_index/total_chunks — never insert or delete a row");
    }

    private static ChunkIndexRepair Repair()
    {
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        return new ChunkIndexRepair(matcher, TestData.CreateEmbeddingService());
    }

    private static async Task<Dictionary<string, long>> PositionsByValueAsync(SqliteConnection connection, string sourceFile) =>
        (await connection.QueryAsync<(string Value, long ChunkIndex)>(
                "SELECT value AS Value, chunk_index AS ChunkIndex FROM entries WHERE source_file = @sourceFile",
                new { sourceFile }))
            .ToDictionary(r => r.Value, r => r.ChunkIndex, StringComparer.Ordinal);

    /// <summary>
    ///     Seeds rows with an explicit (wrong-on-purpose) chunk_index, the way an id-order recompute
    ///     would have left them — and the SAME content-addressed hash a real ingest would compute, so
    ///     the repair's re-chunk-and-match can find them.
    /// </summary>
    private async Task<SqliteConnection> OpenSeededAsync(params (string SourceFile, string Value, int ChunkIndex)[] rows)
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        foreach (var (sourceFile, value, chunkIndex) in rows)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                     created_at, updated_at, embed_state, chunk_index, total_chunks)
                VALUES (@hash, @path, @value, @sourceFile, 'project', @projectId, 1, 1, 'embedded', @chunkIndex, @totalChunks)
                """,
                new
                {
                    hash = ContentHash.Of(sourceFile, value),
                    path = sourceFile, value, sourceFile, projectId = ProjectId, chunkIndex, totalChunks = rows.Length
                });
        }

        return connection;
    }
}
