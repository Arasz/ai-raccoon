using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP3 step 4. Rows written before <c>memory_write</c> chunked, or ingested while the budget was
///     not engine-aware, hold more text than the embedding window — 6,240 of 17,219 on the live bank,
///     worst at 7,376 tokens against a 254-token window. <see cref="ChunkBackfill" /> splits each such
///     row's own stored value into in-budget pieces.
///     <para>
///         Its two acceptance criteria are opposites and both must hold: nothing over the window
///         afterwards, and no text lost. Either alone is trivially satisfiable — deleting the rows
///         passes the first, doing nothing passes the second.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBackfillTests : IDisposable
{
    private const string ProjectId = "acme";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-backfill");
    private readonly SqliteConnectionFactory _factory;

    public ChunkBackfillTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Backfill_SplitsAnOverWindowRow_UntilNothingExceedsTheWindow()
    {
        await using var connection = await OpenSeededAsync(("big.md", Paragraphs(60)));

        var report = await Backfill().RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        report.RowsReplaced.ShouldBe(1);
        report.PiecesWritten.ShouldBeGreaterThan(1, "an over-window row must become several in-budget rows");
        (await OverWindowCountAsync(connection)).ShouldBe(0);
    }

    /// <summary>The other half of the contract. A backfill that loses text passes the first check perfectly.</summary>
    [Fact]
    public async Task Backfill_LosesNoText()
    {
        await using var connection = await OpenSeededAsync(("big.md", Paragraphs(60)));
        var before = await connection.ExecuteScalarAsync<long>("SELECT SUM(length(value)) FROM entries");

        await Backfill().RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        var after = await connection.ExecuteScalarAsync<long>("SELECT SUM(length(value)) FROM entries");
        after.ShouldBeGreaterThanOrEqualTo(before,
            "chunk overlay may repeat text, so the total may grow — it must never shrink");
    }

    [Fact]
    public async Task Backfill_LeavesAnInBudgetRowUntouched()
    {
        await using var connection = await OpenSeededAsync(("small.md", "A short note that fits comfortably."));

        var report = await Backfill().RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        report.RowsReplaced.ShouldBe(0, "a row already within the window is not the backfill's business");
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries")).ShouldBe(1);
    }

    /// <summary>A dry run reports the same work and performs none of it.</summary>
    [Fact]
    public async Task Backfill_DryRun_ReportsTheWorkWithoutDoingIt()
    {
        await using var connection = await OpenSeededAsync(("big.md", Paragraphs(60)));

        var report = await Backfill().RunAsync(connection, dryRun: true, TestContext.Current.CancellationToken);

        report.RowsReplaced.ShouldBe(1, "the dry run must report what a real run would replace");
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries")).ShouldBe(1,
            "a dry run must not write");
        (await OverWindowCountAsync(connection)).ShouldBe(1, "and must leave the defect in place");
    }

    /// <summary>
    ///     The replacement rows must stay findable: same path, same source_file, same project. A
    ///     backfill that orphans a document's chunks has traded one defect for a worse one.
    /// </summary>
    [Fact]
    public async Task Backfill_KeepsThePiecesAttachedToTheirSource()
    {
        await using var connection = await OpenSeededAsync(("big.md", Paragraphs(60)));

        await Backfill().RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Path, string? SourceFile, string ProjectId)>(
            "SELECT path AS Path, source_file AS SourceFile, project_id AS ProjectId FROM entries")).ToList();
        rows.ShouldAllBe(r => r.Path == "big.md" && r.SourceFile == "big.md" && r.ProjectId == ProjectId);
    }

    /// <summary>New rows are written unembedded on purpose: the existing embed pass owns that work.</summary>
    [Fact]
    public async Task Backfill_WritesThePiecesAsPending()
    {
        await using var connection = await OpenSeededAsync(("big.md", Paragraphs(60)));

        await Backfill().RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE embed_state <> 'pending'")).ShouldBe(0);
    }

    private static ChunkBackfill Backfill() =>
        new(TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());

    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(0, count).Select(i =>
            $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("memory retrieval budget window", 12))));

    private static async Task<long> OverWindowCountAsync(SqliteConnection connection)
    {
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var budget = EmbeddingService.SafeChunkBudgetFor("local", null);
        var values = await connection.QueryAsync<string>("SELECT value FROM entries WHERE value IS NOT NULL");
        return values.Count(v => tokenizer.CountTokens(v) > budget);
    }

    /// <summary>Seeds rows the way the defect made them: one row per document, chunker bypassed.</summary>
    private async Task<SqliteConnection> OpenSeededAsync(params (string Path, string Value)[] rows)
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        foreach (var (path, value) in rows)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                     created_at, updated_at, embed_state, chunk_index, total_chunks)
                VALUES (@hash, @path, @value, @path, 'project', @projectId, 1, 1, 'embedded', 0, 1)
                """,
                new { hash = $"h-{path}", path, value, projectId = ProjectId });
        }

        return connection;
    }
}
