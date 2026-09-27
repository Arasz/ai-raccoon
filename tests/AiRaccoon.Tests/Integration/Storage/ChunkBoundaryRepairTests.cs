using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Banks written before chunk boundaries fell on whitespace hold rows cut mid-word, and nothing
///     re-chunks them on its own: a note is never rewritten and a watched file's unchanged content hash
///     skips the digest. <see cref="ChunkBoundaryRepair" /> finds those seams and repairs them.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBoundaryRepairTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-boundary-repair");
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
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    private static string LongNote() =>
        string.Join(" ", Enumerable.Repeat(
            "The village fete committee met in the church hall to plan stalls, bunting, the tombola and the cake competition.", 10))
        + " Invoice reference vk83jq was filed with the parish council.";

    private static (string Head, string Tail) CutInside(string text, string word)
    {
        var cut = text.IndexOf(word, StringComparison.Ordinal) + 2;
        return (text[..cut], text[cut..]);
    }

    [RetryFact]
    public async Task Run_NoteCutMidWord_IsRechunkedSoTheWordIsWholeAgain()
    {
        var text = LongNote();
        var path = await WriteNoteAsync(text);
        var (head, tail) = CutInside(text, "vk83jq");
        await ResplitAsync(path, [head, tail], noteOrder: true);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(0, "premise: the seeded rows split the identifier");

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        var values = await ValuesAsync(path);
        values.Sum(value => value.Length).ShouldBe(text.Length, "a repair re-cuts the text; it never adds or drops any");
        (await PendingCountAsync(path)).ShouldBe(values.Count, "re-chunked rows must be embedded again");
        (await TombstonedAsync(ContentHash.Of(path, head))).ShouldBeTrue("a peer must not resurrect the old half on sync");
    }

    [RetryFact]
    public async Task Run_WatchedFileCutMidWord_IsReingestedEvenThoughItsContentHashIsUnchanged()
    {
        var text = LongNote();
        var file = await IngestFileAsync("minutes.md", text);
        var (head, tail) = CutInside(text, "vk83jq");
        await ResplitAsync(file, [head, tail], noteOrder: false);
        await SeedWatchFingerprintAsync(file, text);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(0, "premise: the seeded rows split the identifier");

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        (await ValuesAsync(file)).ShouldNotContain(head, "the old half must be gone, not duplicated");
    }

    [RetryFact]
    public async Task Run_FencedLineCutMidWord_IsReingestedFromTheFile()
    {
        var line = string.Join(" ", Enumerable.Range(0, 120).Select(i => $"handler{i:D3}();")) + " dispatch_vk83jq();";
        var text = "```\n" + line + "\n```\n";
        var file = await IngestFileAsync("dispatch.md", text);
        var (head, tail) = CutInside(line, "vk83jq");
        await ResplitAsync(file, ["```\n" + head + "\n```\n", "```\n" + tail + "\n```\n"], noteOrder: false);
        (await KeywordHitsAsync("dispatch_vk83jq")).ShouldBe(0, "premise: the seeded sub-fences split the identifier");

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(1);
        (await KeywordHitsAsync("dispatch_vk83jq")).ShouldBe(1);
    }

    [RetryFact]
    public async Task Run_FileGoneFromDisk_IsRepairedFromItsRowsWithPositionsInOrder()
    {
        const string closing = "Closing paragraph: the parish council thanked the committee.\n";
        var text = LongNote() + "\n" + closing;
        var file = await IngestFileAsync("gone.md", text);
        var (head, tail) = CutInside(LongNote(), "vk83jq");
        await ResplitAsync(file, [head, tail + "\n", closing], noteOrder: false);
        File.Delete(file);

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        var positions = await PositionsAsync(file);
        positions.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, positions.Count)]);
        positions.ShouldAllBe(p => p.TotalChunks == positions.Count);
        string.Concat(positions.Select(p => p.Value)).ShouldBe(text, "positions follow the text order");
    }

    [RetryFact]
    public async Task Run_TermLongerThanTheBudget_IsLeftAsItIs()
    {
        var term = string.Concat(Enumerable.Range(0, 300).Select(i => $"q{i:D3}"));
        var path = await WriteNoteAsync($"Release artifact digest {term} was pinned in the lockfile.");
        var before = await ValuesAsync(path);
        before.Count.ShouldBeGreaterThan(1, "premise: the term is hard-cut across rows");

        var report = await RepairAsync();

        report.RowsWritten.ShouldBe(0, "a hard cut the current chunker would make again is not a defect to repair");
        (await ValuesAsync(path)).ShouldBe(before);
    }

    /// <summary>A piece no larger than the overlay could have been repeated at the start of the next row, so
    /// joining the two could duplicate text; such a seam is not one the old chunker's hard cut produced.</summary>
    [RetryFact]
    public async Task Run_CutAfterAPieceNoLargerThanTheOverlay_IsNotJoined()
    {
        var path = await WriteNoteAsync(LongNote());
        await ResplitAsync(path, ["Short note ab", "cd continues here."], noteOrder: true);

        var report = await RepairAsync();

        report.RowsWritten.ShouldBe(0);
        (await ValuesAsync(path)).ShouldBe(["cd continues here.", "Short note ab"]);
    }

    [RetryFact]
    public async Task Job_RunsOnceAndLeavesTheRepairedRowsForTheEmbedDrain()
    {
        var text = LongNote();
        var path = await WriteNoteAsync(text);
        var (head, tail) = CutInside(text, "vk83jq");
        await ResplitAsync(path, [head, tail], noteOrder: true);
        var job = new ChunkBoundaryRepairJob(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), _store, new FakeTimeProvider(FixedNow));
        job.Interval.ShouldBeNull("once ever: it heals rows the write paths no longer create");
        job.Name.ShouldBe(ChunkBoundaryRepairJob.JobName);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var leftPending = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        leftPending.ShouldBeTrue("PendingEmbedJob must see the new rows in the same pass");
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
    }

    private async Task<ChunkBoundaryRepairReport> RepairAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await new ChunkBoundaryRepair(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
                TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow))
            .RunAsync(connection, _store, TestContext.Current.CancellationToken);
    }

    private async Task<string> WriteNoteAsync(string text)
    {
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, text), TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string>("SELECT path FROM entries WHERE hash = @hash", new { hash = entry.Hash })
               ?? throw new InvalidOperationException("the note stored no row");
    }

    private async Task<string> IngestFileAsync(string name, string text)
    {
        var file = Path.Combine(_dataRoot, name);
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        (await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);
        return file;
    }

    /// <summary>Replaces a path's rows with the pieces an older chunker would have stored: a note's
    /// continuation rows first and its first row last, a file's rows in position order.</summary>
    private async Task ResplitAsync(string path, IReadOnlyList<string> pieces, bool noteOrder)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var template = await connection.QueryFirstAsync(
            "SELECT scope, project_id, context_label, workspace_id, source_file, section, source_id, agent_id, created_at FROM entries WHERE path = @path",
            new { path });
        await connection.ExecuteAsync("DELETE FROM entries WHERE path = @path", new { path });
        var order = noteOrder ? [.. Enumerable.Range(1, pieces.Count - 1), 0] : Enumerable.Range(0, pieces.Count).ToList();
        foreach (var i in order)
        {
            await connection.ExecuteAsync(MemorySql.InsertEntry, new
            {
                hash = ContentHash.Of(path, pieces[i]),
                path,
                value = pieces[i],
                sourceFile = (string?)template.source_file,
                section = (string?)template.section,
                scope = (string?)template.scope,
                projectId = (string?)template.project_id,
                contextLabel = (string?)template.context_label,
                workspaceId = (string?)template.workspace_id,
                agentId = (string?)template.agent_id,
                createdAt = (long)template.created_at,
                updatedAt = (long)template.created_at,
                sourceId = (long?)template.source_id,
                chunkIndex = noteOrder ? -1 : i,
                totalChunks = noteOrder ? 0 : pieces.Count
            });
        }
    }

    private async Task SeedWatchFingerprintAsync(string file, string content)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO watch_files (project_id, path, file_hash, updated_at) VALUES (@projectId, @path, @fileHash, 1)",
            new { projectId = ProjectId, path = file, fileHash = WatchDigestExecutor.ComputeHash(file, content) });
    }

    private async Task<long> KeywordHitsAsync(string term)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries_fts WHERE entries_fts MATCH @term",
            new { term = $"\"{term}\"" });
    }

    private async Task<List<string>> ValuesAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<string>("SELECT value FROM entries WHERE path = @path ORDER BY id", new { path })];
    }

    private async Task<List<Position>> PositionsAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<Position>(
            "SELECT chunk_index AS ChunkIndex, total_chunks AS TotalChunks, value AS Value FROM entries WHERE path = @path ORDER BY chunk_index",
            new { path })];
    }

    private async Task<long> PendingCountAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE path = @path AND embed_state = 'pending'", new { path });
    }

    private async Task<bool> TombstonedAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sync_tombstones WHERE hash = @hash", new { hash }) > 0;
    }

    private sealed record Position(long ChunkIndex, long TotalChunks, string Value);
}
