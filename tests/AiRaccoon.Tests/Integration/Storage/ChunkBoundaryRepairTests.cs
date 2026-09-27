using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
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

    /// <summary>A note whose middle row is cut inside "vk83jq", framed by an opening and a closing row.</summary>
    private static string[] FramedPieces()
    {
        var (head, tail) = CutInside(LongNote(), "vk83jq");
        return ["Opening line of the minutes.\n", head, tail + "\n", "Closing line of the minutes.\n"];
    }

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

    /// <summary>A note citing a source file shares that file's position partition. A seam in the middle of the
    /// note must leave every row of the partition on its own, contiguous position, in the note's text order.</summary>
    [RetryFact]
    public async Task Run_SourceCitingNoteWithAMiddleSeam_KeepsPositionsContiguousAndInTextOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var cited = Path.Combine(_dataRoot, "minutes.md");
        var pieces = FramedPieces();
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, string.Concat(pieces), SourceFile: cited), ct);
        var path = await PathOfAsync(entry.Hash);
        await ResplitAsync(path, pieces, noteOrder: true);
        await RecomputePositionsAsync();
        var before = await PositionsAsync(path);
        before.Select(p => p.ChunkIndex).Distinct().Count().ShouldBe(4, "premise: the writer's positions are distinct");
        before[^1].Value.ShouldStartWith("Opening", customMessage: "premise: the stored positions put the opening last");

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        var after = await PositionsAsync(path);
        after.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, after.Count)], "no two rows may claim one position");
        after.ShouldAllBe(p => p.TotalChunks == after.Count);
        string.Concat(after.Select(p => p.Value)).ShouldBe(string.Concat(pieces), "positions follow the note's text order");
    }

    /// <summary>A note whose rows were stored in text order (a chunk backfill, or any write since) must be
    /// repaired in that order, not with its last row taken for its opening.</summary>
    [RetryFact]
    public async Task Run_NoteStoredInTextOrder_IsRechunkedInTextOrder()
    {
        var text = LongNote();
        var path = await WriteNoteAsync(text);
        var (head, tail) = CutInside(text, "vk83jq");
        await ResplitAsync(path, [head, tail], noteOrder: false);

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        string.Concat(await ValuesAsync(path)).ShouldBe(text, "the repaired rows hold the note's text in order");
    }

    /// <summary>Rows that do not join back into the body their path names have no provable order, so a repair
    /// leaves them as stored rather than re-chunking a guessed one.</summary>
    [RetryFact]
    public async Task Run_NoteRowsThatDoNotJoinBackIntoItsBody_AreLeftAsStored()
    {
        var path = await WriteNoteAsync(LongNote());
        var (head, tail) = CutInside(LongNote(), "vk83jq");
        await ResplitAsync(path, ["Opening line that was never part of the note.\n", head, tail], noteOrder: true);
        var before = await ValuesAsync(path);

        var report = await RepairAsync();

        report.RowsWritten.ShouldBe(0);
        report.NotesUnproven.ShouldBe(1, "a note left as stored must be counted, not skipped silently");
        (await ValuesAsync(path)).ShouldBe(before);
    }

    /// <summary>A note without a source file has no positions at all; a middle seam repair leaves it that way.</summary>
    [RetryFact]
    public async Task Run_PlainNoteWithAMiddleSeam_StaysWithoutPositions()
    {
        var pieces = FramedPieces();
        var path = await WriteNoteAsync(string.Concat(pieces));
        await ResplitAsync(path, pieces, noteOrder: true);

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
        var after = await PositionsAsync(path);
        after.ShouldAllBe(p => p.ChunkIndex == -1 && p.TotalChunks == 0);
        after.Sum(p => p.Value.Length).ShouldBe(pieces.Sum(p => p.Length));
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

    /// <summary>Re-chunking a gone file cited by a note puts the file's rows first and the note after them, even when the
    /// note's stored position sat among the file's.</summary>
    [RetryFact]
    public async Task Run_FileGoneFromDiskCitedByANote_IsRepairedWithTheNoteAfterTheFileRows()
    {
        const string closing = "Closing paragraph: the parish council thanked the committee.\n";
        var text = LongNote() + "\n" + closing;
        var file = await IngestFileAsync("gone-cited.md", text);
        var (head, tail) = CutInside(LongNote(), "vk83jq");
        await ResplitAsync(file, [head, tail + "\n", closing], noteOrder: false);
        var note = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "The minutes were read aloud.", SourceFile: file),
            TestContext.Current.CancellationToken);
        await SetPositionAsync(await IdOfAsync(note.Hash), 1);
        File.Delete(file);

        var report = await RepairAsync();

        report.GroupsRepaired.ShouldBe(1);
        var partition = await PartitionAsync(file);
        partition.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, partition.Count)]);
        partition.ShouldAllBe(p => p.TotalChunks == partition.Count);
        partition[^1].Value.ShouldBe("The minutes were read aloud.", "the note follows the file's rows");
        string.Concat(partition.SkipLast(1).Select(p => p.Value)).ShouldBe(text);
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

    /// <summary>A file whose seams the current chunker cuts again on every ingest (a term longer than the budget)
    /// already holds the rows a re-ingest would write, so a repair leaves it alone instead of re-flagging it forever.</summary>
    [RetryFact]
    public async Task Run_FileTheChunkerMustCut_IsNotReingested()
    {
        var term = string.Concat(Enumerable.Range(0, 300).Select(i => $"q{i:D3}"));
        var file = await IngestFileAsync("lockfile.md", $"Release artifact digest {term} was pinned in the lockfile.\n");
        var before = await IdsAsync(file);
        before.Count.ShouldBeGreaterThan(1, "premise: the term is hard-cut across rows");

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(0, "re-ingesting would write the same rows and flag the same seam next run");
        (await IdsAsync(file)).ShouldBe(before, "no row was rewritten");
    }

    /// <summary>File rows the current chunker still reproduces but whose stored positions collide (#788) take their
    /// document positions in place: no re-ingest, so their embeddings stay.</summary>
    [RetryFact]
    public async Task Run_FileRowsWithDuplicatePositions_TakeTheirDocumentPositionsInPlace()
    {
        var file = await IngestFileAsync("plan.md", SectionedDocument(6));
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        var inDocumentOrder = await ValuesAsync(file);
        await SetPositionAsync(ids[^1], 1);

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(0);
        report.FilesRepositioned.ShouldBe(1);
        (await IdsAsync(file)).ShouldBe(ids, "the rows are kept, not re-ingested");
        var positions = await PositionsAsync(file);
        positions.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, ids.Count)]);
        positions.ShouldAllBe(p => p.TotalChunks == ids.Count);
        positions.Select(p => p.Value).ToList().ShouldBe(inDocumentOrder, "positions follow the order the ingest wrote");
    }

    /// <summary>File rows left with a gap and a total that counts a repeated chunk twice, the shape the ingest used to
    /// write, take contiguous positions and the row count in place.</summary>
    [RetryFact]
    public async Task Run_FileRowsWithAGapAndAnInflatedTotal_TakeContiguousPositionsInPlace()
    {
        var file = await IngestFileAsync("gapped.md", SectionedDocument(4));
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        var inDocumentOrder = await ValuesAsync(file);
        for (var i = 1; i < ids.Count; i++)
        {
            await SetPositionAsync(ids[i], i + 1);
        }

        await SetTotalAsync(file, ids.Count + 1);

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(0);
        report.FilesRepositioned.ShouldBe(1);
        (await IdsAsync(file)).ShouldBe(ids, "the rows are kept, not re-ingested");
        var positions = await PositionsAsync(file);
        positions.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, ids.Count)]);
        positions.ShouldAllBe(p => p.TotalChunks == ids.Count);
        positions.Select(p => p.Value).ToList().ShouldBe(inDocumentOrder);
    }

    /// <summary>File rows whose positions are right but whose total_chunks is not the row count take the row count.</summary>
    [RetryFact]
    public async Task Run_FileRowsWithOnlyAnInflatedTotal_TakeTheRowCount()
    {
        var file = await IngestFileAsync("inflated.md", SectionedDocument(4));
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        await SetTotalAsync(file, ids.Count + 3);

        var report = await RepairAsync();

        report.FilesRepositioned.ShouldBe(1);
        (await PositionsAsync(file)).ShouldAllBe(p => p.TotalChunks == ids.Count);
    }

    /// <summary>A file that grew on disk since its rows were written holds a chunk no row has yet, so repositioning
    /// the rows would leave it unindexed: the file is re-ingested instead.</summary>
    [RetryFact]
    public async Task Run_FileRowsWithDuplicatePositionsWhoseFileGrew_AreReingested()
    {
        var document = SectionedDocument(4);
        var file = await IngestFileAsync("grown.md", document);
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        await SetPositionAsync(ids[^1], 1);
        // A paragraph too large to join the last row, so every stored row is still reproduced.
        var appended = string.Join(" ", Enumerable.Repeat("The appended paragraph records a step added after the last ingest.", 30));
        await File.WriteAllTextAsync(file, document + appended + "\n", TestContext.Current.CancellationToken);
        (await ScanAsync(file)).PositionById.Values.ShouldAllBe(position => position >= 0,
            "premise: the grown file still reproduces every stored row");

        var report = await RepairAsync();

        report.FilesRepositioned.ShouldBe(0);
        report.FilesReingested.ShouldBe(1);
        (await ValuesAsync(file)).ShouldContain(value => value.Contains("appended paragraph"));
        var positions = await PositionsAsync(file);
        positions.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, positions.Count)]);
        positions.ShouldAllBe(p => p.TotalChunks == positions.Count);
    }

    /// <summary>Colliding positions on a file that also holds a row the chunker no longer makes are repaired by
    /// re-ingesting it, which drops the stale row.</summary>
    [RetryFact]
    public async Task Run_FileRowsWithDuplicatePositionsAndAStaleRow_AreReingested()
    {
        var file = await IngestFileAsync("stale.md", SectionedDocument(4));
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        await SetPositionAsync(ids[^1], 1);
        await InsertFileRowAsync(file, "## Removed section\n\nText the file no longer holds.\n", chunkIndex: 2);

        var report = await RepairAsync();

        report.FilesReingested.ShouldBe(1);
        var positions = await PositionsAsync(file);
        positions.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, ids.Count)]);
        (await ValuesAsync(file)).ShouldNotContain(value => value.Contains("Removed section"));
    }

    /// <summary>A note citing a file shares its position partition: the repair puts the file's rows first and the note
    /// after them, total_chunks counts both, and a later memory_write keeps them agreeing so a second run has nothing to do.</summary>
    [RetryFact]
    public async Task Run_FileCitedByANote_NumbersTheWholePartitionAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await IngestFileAsync("cited.md", SectionedDocument(4));
        var ids = await IdsAsync(file);
        ids.Count.ShouldBeGreaterThan(2, "premise: the document spans several rows");
        var inDocumentOrder = await ValuesAsync(file);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "The plan was reviewed by the parish council.", SourceFile: file), ct);
        await SetPositionAsync(ids[^1], 1);

        var first = await RepairAsync();

        first.FilesRepositioned.ShouldBe(1);
        var partition = await PartitionAsync(file);
        partition.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, ids.Count + 1)]);
        partition.ShouldAllBe(p => p.TotalChunks == ids.Count + 1, "total_chunks counts the note that cites the file");
        partition.Take(ids.Count).Select(p => p.Value).ToList().ShouldBe(inDocumentOrder, "the file's rows come first");

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "The council approved the plan a week later.", SourceFile: file), ct);
        partition = await PartitionAsync(file);
        partition.Select(p => (int)p.ChunkIndex).ToList().ShouldBe([.. Enumerable.Range(0, ids.Count + 2)]);
        partition.ShouldAllBe(p => p.TotalChunks == ids.Count + 2);

        var second = await RepairAsync();

        second.ShouldBe(new ChunkBoundaryRepairReport(0, 0, 0, 0, 0), "the repair and memory_write agree on what the partition holds");
        (await PartitionAsync(file)).ShouldBe(partition);
    }

    /// <summary>A code file whose identifier is longer than the budget is cut the same way on every ingest, so a
    /// re-ingest that writes back the same rows is not reported as a repair.</summary>
    [RetryFact]
    public async Task Run_CodeFileTheChunkerMustCut_IsNotReportedAsReingested()
    {
        var ct = TestContext.Current.CancellationToken;
        var codeStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, new CodeChunker(new CharCountTokenizer(), 90), null, null);
        var file = Path.Combine(_dataRoot, "digest.cs");
        await File.WriteAllTextAsync(file, "var digest = \"" + string.Concat(Enumerable.Range(0, 60).Select(i => $"q{i:D3}")) + "\";\n", ct);
        await codeStore.IngestFileAsync(ProjectId, file, null, ct);
        await using (var premise = await _factory.OpenBankAsync(ct))
        {
            (await premise.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM code_entries WHERE path = @file", new { file }))
                .ShouldBeGreaterThan(1, "premise: the identifier is hard-cut across rows");
        }

        await using var repairConnection = await _factory.OpenBankAsync(ct);
        var report = await new ChunkBoundaryRepair(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
                TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow))
            .RunAsync(repairConnection, codeStore, ct);

        report.FilesReingested.ShouldBe(0, "the re-ingest wrote back the rows the file already had");
    }

    [RetryFact]
    public async Task Run_CodeFileCutMidIdentifier_IsReingestedFromTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var codeStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, new CodeChunker(new CharCountTokenizer(), 90), null, null);
        var line = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"register_handler_{i:D2}();")) + " dispatch_vk83jq();\n";
        var file = Path.Combine(_dataRoot, "dispatch.cs");
        await File.WriteAllTextAsync(file, line, ct);
        await codeStore.IngestFileAsync(ProjectId, file, null, ct);
        var (head, tail) = CutInside(line, "vk83jq");
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync("DELETE FROM code_entries WHERE path = @file", new { file });
            for (var i = 0; i < 2; i++)
            {
                var value = i == 0 ? head : tail;
                await connection.ExecuteAsync(
                    """
                    INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, chunk_index, total_chunks)
                    VALUES (@hash, @file, @value, @file, 1, 1, @projectId, 1, 1, @i, 2)
                    """, new { hash = ContentHash.Of(file, value), file, value, projectId = ProjectId, i });
            }
        }

        await using var repairConnection = await _factory.OpenBankAsync(ct);
        var report = await new ChunkBoundaryRepair(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
                TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow))
            .RunAsync(repairConnection, codeStore, ct);

        report.FilesReingested.ShouldBe(1);
        await using var check = await _factory.OpenBankAsync(ct);
        (await check.QueryAsync<string>("SELECT value FROM code_entries WHERE path = @file", new { file }))
            .ShouldContain(value => value.Contains("dispatch_vk83jq"));
    }

    /// <summary>A piece no larger than the overlay could have been repeated at the start of the next row, so
    /// joining the two could duplicate text; such a seam is not one the old chunker's hard cut produced.</summary>
    [RetryFact]
    public async Task Run_CutAfterAPieceNoLargerThanTheOverlay_IsNotJoined()
    {
        var path = await WriteNoteAsync("Short note abcd continues here.");
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
            TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), _store, new FakeTimeProvider(FixedNow),
            NullLogger<ChunkBoundaryRepairJob>.Instance);
        job.Interval.ShouldBeNull("once ever: it heals rows the write paths no longer create");
        job.Name.ShouldBe("chunk-boundary-repair-v2", "a bank stamped v1 runs the repair once more for #788");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var leftPending = await job.RunAsync(connection, TestContext.Current.CancellationToken);

        leftPending.ShouldBeTrue("PendingEmbedJob must see the new rows in the same pass");
        (await KeywordHitsAsync("vk83jq")).ShouldBe(1);
    }

    /// <summary>The job logs what it did, including the notes it left as stored because their order is unprovable.</summary>
    [RetryFact]
    public async Task Job_LogsTheNotesItLeftAsStored()
    {
        var path = await WriteNoteAsync(LongNote());
        var (head, tail) = CutInside(LongNote(), "vk83jq");
        await ResplitAsync(path, ["Opening line that was never part of the note.\n", head, tail], noteOrder: true);
        var logger = new FakeLogger<ChunkBoundaryRepairJob>();
        var job = new ChunkBoundaryRepairJob(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            TestData.RealPlainTextChunker(), TestData.CreateEmbeddingService(), _store, new FakeTimeProvider(FixedNow), logger);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(447);
        record.StructuredState.ShouldNotBeNull().ShouldContain(new KeyValuePair<string, string?>("Unproven", "1"));
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

    private async Task<string> PathOfAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string>("SELECT path FROM entries WHERE hash = @hash", new { hash })
               ?? throw new InvalidOperationException("no row for the hash");
    }

    /// <summary>The positions memory_write gives a source-citing note: filled in id order after the insert.</summary>
    private async Task RecomputePositionsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(MemorySql.RecomputeChunkColumnsBankWide);
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

    /// <summary>Every row of a file's position partition — its own rows and the notes citing it — by position.</summary>
    private async Task<List<Position>> PartitionAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<Position>(
            "SELECT chunk_index AS ChunkIndex, total_chunks AS TotalChunks, value AS Value FROM entries WHERE source_file = @file ORDER BY chunk_index",
            new { file })];
    }

    /// <summary>A markdown document long enough to span several rows.</summary>
    private static string SectionedDocument(int sections) =>
        string.Concat(Enumerable.Range(0, sections).Select(i =>
            $"## Section {i}\n\n" + string.Join(" ", Enumerable.Repeat($"Paragraph {i} of the plan describes step {i} in detail.", 30)) + "\n\n"));

    private async Task<List<long>> IdsAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return [.. await connection.QueryAsync<long>("SELECT id FROM entries WHERE path = @path ORDER BY id", new { path })];
    }

    private async Task<long> IdOfAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT id FROM entries WHERE hash = @hash", new { hash });
    }

    private async Task SetPositionAsync(long id, long chunkIndex)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE entries SET chunk_index = @chunkIndex WHERE id = @id", new { id, chunkIndex });
    }

    /// <summary>What the current chunker makes of a file's rows on disk.</summary>
    private async Task<ChunkPositionScan> ScanAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var scanner = new ChunkPositionScanner(TestData.RealFileTypeMatcher(), TestData.CreateEmbeddingService());
        var budget = await scanner.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<StoredChunk>("SELECT id AS Id, hash AS Hash FROM entries WHERE path = @file", new { file });
        return scanner.Scan(file, [.. rows], budget.MaxTokens, budget.OverlayTokens, budget.CountTokens);
    }

    private async Task SetTotalAsync(string path, long totalChunks)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE entries SET total_chunks = @totalChunks WHERE path = @path", new { path, totalChunks });
    }

    private async Task InsertFileRowAsync(string file, string value, long chunkIndex)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var template = await connection.QueryFirstAsync(
            "SELECT scope, project_id, source_id, created_at, total_chunks FROM entries WHERE path = @file", new { file });
        await connection.ExecuteAsync(MemorySql.InsertEntry, new
        {
            hash = ContentHash.Of(file, value),
            path = file,
            value,
            sourceFile = file,
            section = (string?)null,
            scope = (string?)template.scope,
            projectId = (string?)template.project_id,
            contextLabel = (string?)null,
            workspaceId = (string?)null,
            agentId = (string?)null,
            createdAt = (long)template.created_at,
            updatedAt = (long)template.created_at,
            sourceId = (long?)template.source_id,
            chunkIndex,
            totalChunks = (long)template.total_chunks
        });
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

    private sealed class CharCountTokenizer : ICodeTokenizer
    {
        public int CountTokens(string text) => text.Length;
    }
}
