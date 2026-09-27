using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Integration.Memory;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Banks written before memory_write stored a note's chunks in text order hold source-citing notes whose opening
///     sits at the last chunk_index. <see cref="NoteChunkOrderRepair" /> puts those positions in text order.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NoteChunkOrderRepairTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("note-chunk-order-repair");
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

    private string Cited => Path.Combine(_dataRoot, "minutes.md");

    [RetryFact]
    public async Task Run_NoteStoredWithItsOpeningLast_PutsPositionsInTextOrder()
    {
        var note = SourceCitingNoteChunkOrderTests.LongNote();
        var path = await WriteAsync(note);
        await StoreOpeningLastAsync(path);
        (await ValuesByPositionAsync(path))[^1].ShouldStartWith("Opening marker zq71", customMessage: "premise: the opening holds the last position");

        var report = await RunAsync();

        report.NotesReordered.ShouldBe(1);
        var values = await ValuesByPositionAsync(path);
        values[0].ShouldStartWith("Opening marker zq71");
        values[^1].ShouldEndWith("Closing marker xw42 ends the parish minutes.");
        var starts = values.Select(value => note.IndexOf(value, StringComparison.Ordinal)).ToList();
        starts.ShouldBe([.. starts.Order()], "each position starts later in the text than the one before it");
        (await PositionsAsync(path)).ShouldBe([.. Enumerable.Range(0, values.Count).Select(i => (long)i)], "the note keeps the positions it held");
    }

    /// <summary>The path hashes the body as sent; the rows hold it with \n endings.</summary>
    [RetryFact]
    public async Task Run_NoteWrittenWithCrLfEndings_IsPutInTextOrder()
    {
        var path = await WriteAsync(SourceCitingNoteChunkOrderTests.LongNote().Replace("\n", "\r\n", StringComparison.Ordinal));
        await StoreOpeningLastAsync(path);

        var report = await RunAsync();

        report.NotesReordered.ShouldBe(1);
        report.NotesUnproven.ShouldBe(0);
        (await ValuesByPositionAsync(path))[0].ShouldStartWith("Opening marker zq71");
    }

    [RetryFact]
    public async Task Run_NoteAlreadyInTextOrder_MovesNothing()
    {
        var path = await WriteAsync(SourceCitingNoteChunkOrderTests.LongNote());
        var before = await ValuesByPositionAsync(path);

        var report = await RunAsync();

        report.NotesReordered.ShouldBe(0);
        (await ValuesByPositionAsync(path)).ShouldBe(before);
    }

    [RetryFact]
    public async Task Run_NoteWhoseRowsDoNotJoinBackIntoItsBody_IsLeftAsStoredAndCounted()
    {
        var path = await WriteAsync(SourceCitingNoteChunkOrderTests.LongNote());
        await StoreOpeningLastAsync(path);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE entries SET value = value || ' amended' WHERE id = (SELECT MIN(id) FROM entries WHERE path = @path)",
                new { path });
        }

        var before = await ValuesByPositionAsync(path);

        var report = await RunAsync();

        report.NotesReordered.ShouldBe(0);
        report.NotesUnproven.ShouldBe(1);
        (await ValuesByPositionAsync(path)).ShouldBe(before);
    }

    [RetryFact]
    public async Task Job_RunsOnceAndCreatesNoEmbedWork()
    {
        var path = await WriteAsync(SourceCitingNoteChunkOrderTests.LongNote());
        await StoreOpeningLastAsync(path);
        var job = new NoteChunkOrderRepairJob(NullLogger<NoteChunkOrderRepairJob>.Instance);
        job.Interval.ShouldBeNull("once ever: it repairs positions the write path no longer creates");
        job.Name.ShouldBe(NoteChunkOrderRepairJob.JobName);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var createdWork = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        createdWork.ShouldBeFalse("it only moves positions");
        (await ValuesByPositionAsync(path))[0].ShouldStartWith("Opening marker zq71");
    }

    private async Task<NoteChunkOrderReport> RunAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await new NoteChunkOrderRepair().RunAsync(connection, TestContext.Current.CancellationToken);
    }

    private async Task<string> WriteAsync(string note)
    {
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, note, SourceFile: Cited),
            TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string>("SELECT path FROM entries WHERE hash = @hash", new { hash = entry.Hash })
               ?? throw new InvalidOperationException("the note stored no row");
    }

    /// <summary>Re-stores a note's rows the way memory_write used to: continuation rows first, the opening last,
    /// positions filled in id order.</summary>
    private async Task StoreOpeningLastAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<StoredRow>(
            """
            SELECT hash AS Hash, value AS Value, source_file AS SourceFile, scope AS Scope, project_id AS ProjectId,
                   source_id AS SourceId, created_at AS CreatedAt
            FROM entries WHERE path = @path ORDER BY chunk_index
            """, new { path })).ToList();
        rows.Count.ShouldBeGreaterThan(2, "premise: the note spans several rows");
        await connection.ExecuteAsync("DELETE FROM entries WHERE path = @path", new { path });
        foreach (var row in rows.Skip(1).Append(rows[0]))
        {
            await connection.ExecuteAsync(MemorySql.InsertEntry, new
            {
                hash = row.Hash,
                path,
                value = row.Value,
                sourceFile = row.SourceFile,
                section = (string?)null,
                scope = row.Scope,
                projectId = row.ProjectId,
                contextLabel = (string?)null,
                workspaceId = (string?)null,
                agentId = (string?)null,
                createdAt = row.CreatedAt,
                updatedAt = row.CreatedAt,
                sourceId = row.SourceId,
                chunkIndex = -1,
                totalChunks = 0
            });
        }

        await connection.ExecuteAsync(MemorySql.RecomputeChunkColumnsBankWide);
    }

    private async Task<List<string>> ValuesByPositionAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<string>("SELECT value FROM entries WHERE path = @path ORDER BY chunk_index", new { path })];
    }

    private async Task<List<long>> PositionsAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<long>("SELECT chunk_index FROM entries WHERE path = @path ORDER BY chunk_index", new { path })];
    }

    private sealed record StoredRow(string Hash, string Value, string? SourceFile, string? Scope, string? ProjectId, long? SourceId, long CreatedAt);
}
