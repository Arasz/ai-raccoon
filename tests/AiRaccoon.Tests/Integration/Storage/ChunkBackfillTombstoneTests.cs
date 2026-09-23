using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Residual #4 (join-p2.md, item 4): <see cref="ChunkBackfill" />'s per-row replace deletes the
///     original oversized row and re-inserts its split pieces under new, smaller-content hashes —
///     with more than one piece, none of them can equal the original text, so the original hash
///     never survives and must be tombstoned (the peer copy F29/F30 already covers would otherwise
///     resurrect it on the next pull). The one case this must NOT do: a piece that happens to
///     reproduce the original content means that hash IS re-inserted in this same operation —
///     tombstoning it would delete a peer's still-live copy of content that never actually vanished.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBackfillTombstoneTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-backfill-tombstone");
    private readonly SqliteConnectionFactory _factory;

    public ChunkBackfillTombstoneTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task Backfill_SplittingAnOverWindowRow_TombstonesTheHashThatDoesNotSurvive()
    {
        const string Path = "big.md";
        var value = Paragraphs(60);
        var hash = ContentHash.Of(Path, value);
        await using var connection = await OpenSeededAsync(Path, hash, value);

        await new ChunkBackfill(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
                TestData.RealPlainTextChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService())
            .RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await TombstoneCountAsync(connection, hash)).ShouldBe(1,
            "the original oversized row's hash never survives a split into more than one piece and must be tombstoned");
        var survivingHashes = (await connection.QueryAsync<string>(
            "SELECT hash FROM entries WHERE path = @path", new { path = Path })).ToList();
        (await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sync_tombstones WHERE hash IN @hashes",
                new { hashes = survivingHashes }))
            .ShouldBe(0, "a hash that still backs a surviving piece must never be tombstoned");
    }

    /// <summary>Positive control: a chunker whose split reproduces the original text as one of
    /// several pieces means the original hash survives this same replace — tombstoning it would be
    /// wrong (it would delete a peer's copy of a row that still legitimately exists).</summary>
    [RetryFact]
    public async Task Backfill_WhenAPieceReproducesTheOriginalContent_DoesNotTombstoneThatHash()
    {
        const string Path = "reprint.md";
        var value = Paragraphs(60);
        var hash = ContentHash.Of(Path, value);
        await using var connection = await OpenSeededAsync(Path, hash, value);

        await new ChunkBackfill(TestData.RealFileTypeMatcher(new ReprintingChunker(value)), TestData.RealMarkdownChunker(),
                TestData.RealPlainTextChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService())
            .RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM entries WHERE hash = @hash", new { hash }))
            .ShouldBe(1, "arrange: the reprinted piece really did survive under the original hash");
        (await TombstoneCountAsync(connection, hash)).ShouldBe(0,
            "a hash a piece re-inserted in this same operation must never be tombstoned — it would delete a peer's live copy");
    }

    private static async Task<long> TombstoneCountAsync(SqliteConnection connection, string hash) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sync_tombstones WHERE hash = @hash", new { hash });

    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(0, count).Select(i =>
            $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("memory retrieval budget window", 12))));

    private async Task<SqliteConnection> OpenSeededAsync(string path, string hash, string value)
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                 created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @path, 'project', @projectId, 1, 1, 'embedded', 0, 1)
            """,
            new { hash, path, value, projectId = ProjectId });
        return connection;
    }

    /// <summary>Splits into the original text plus a short synthetic tail — the pathological shape
    /// where a "split" re-writes rather than removes the original content.</summary>
    private sealed class ReprintingChunker(string original) : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0,
            TokenCount? countTokens = null) =>
            [original, "a short synthetic tail piece"];
    }
}
