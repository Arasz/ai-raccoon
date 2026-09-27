using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Memory;

/// <summary>
///     A memory_write note that cites a source file carries chunk positions. Those positions must follow the
///     note's text: the row at chunk_index 0 holds its opening, the last position holds its close.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SourceCitingNoteChunkOrderTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("note-chunk-order");
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Distinct numbered sentences, so every chunk's place in the text is unambiguous.</summary>
    public static string LongNote() =>
        "Opening marker zq71 starts the parish minutes.\n\n"
        + string.Join("\n\n", Enumerable.Range(0, 60).Select(i =>
            $"Item {i:D2} records that the committee approved the tombola budget line number {i * 7:D3} after a vote."))
        + "\n\nClosing marker xw42 ends the parish minutes.";

    [RetryFact]
    public async Task Write_SourceCitingNote_ChunkIndexFollowsTheTextOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var note = LongNote();
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, note, SourceFile: Path.Combine(_dataRoot, "minutes.md")), ct);

        var rows = await RowsByPositionAsync(entry.Hash);

        rows.Count.ShouldBeGreaterThan(2, "premise: the note is long enough to be stored as several rows");
        rows.Select(row => row.ChunkIndex).ShouldBe([.. Enumerable.Range(0, rows.Count).Select(i => (long)i)]);
        rows[0].Value.ShouldStartWith("Opening marker zq71", customMessage: "chunk_index 0 holds the note's opening");
        rows[^1].Value.ShouldEndWith("Closing marker xw42 ends the parish minutes.",
            customMessage: "the last position holds the note's close");
        var starts = rows.Select(row => note.IndexOf(row.Value, StringComparison.Ordinal)).ToList();
        starts.ShouldAllBe(start => start >= 0, "every row is a slice of the note");
        starts.ShouldBe([.. starts.Order()], "each position starts later in the text than the one before it");
    }

    /// <summary>memory_search reports each hit's chunkIndex; the hit on the note's opening must say 0.</summary>
    [RetryFact]
    public async Task Search_HitOnTheNotesOpening_ReportsChunkIndexZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, LongNote(), SourceFile: Path.Combine(_dataRoot, "minutes.md")), ct);

        var opening = (await _store.SearchAsync(new SearchQuery(ProjectId, "zq71"), ct)).Results.ShouldHaveSingleItem();
        var closing = (await _store.SearchAsync(new SearchQuery(ProjectId, "xw42"), ct)).Results.ShouldHaveSingleItem();

        opening.ChunkIndex.ShouldBe(0);
        closing.ChunkIndex.ShouldBe(closing.TotalChunks - 1);
    }

    private async Task<List<PositionRow>> RowsByPositionAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return
        [
            .. await connection.QueryAsync<PositionRow>(
                """
                SELECT chunk_index AS ChunkIndex, value AS Value FROM entries
                WHERE path = (SELECT path FROM entries WHERE hash = @hash)
                ORDER BY chunk_index
                """, new { hash })
        ];
    }

    private sealed record PositionRow(long ChunkIndex, string Value);
}
