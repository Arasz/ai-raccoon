using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

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

    [RetryFact]
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

    /// <summary>A repeated chunk is one row: it takes its first occurrence's place, and every row's total_chunks
    /// becomes the row count, even where the row's position was already right.</summary>
    [RetryFact]
    public async Task RunAsync_RepeatedParagraph_TakesItsFirstPositionAndTotalBecomesTheRowCount()
    {
        var file = Path.Combine(_dataRoot, "repeats.md");
        await File.WriteAllTextAsync(file, "alpha\n\nrepeat\n\nbeta\n\nrepeat\n\ngamma", TestContext.Current.CancellationToken);
        // The shape the ingest used to write: the repeat at its last place and the repeats counted.
        await using var connection = await OpenSeededAsync(
            (file, "alpha", 0), (file, "repeat", 3), (file, "beta", 2), (file, "gamma", 4));
        await connection.ExecuteAsync("UPDATE entries SET total_chunks = 5");

        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        var positions = await PositionsByValueAsync(connection, file);
        positions["alpha"].ShouldBe(0);
        positions["repeat"].ShouldBe(1);
        positions["beta"].ShouldBe(2);
        positions["gamma"].ShouldBe(3);
        (await connection.QueryAsync<long>("SELECT total_chunks FROM entries WHERE source_file = @file", new { file }))
            .ShouldAllBe(total => total == 4);
    }

    /// <summary>A note citing the file shares its partition: the file's rows take their document positions, the note
    /// follows them keeping its own position rather than being set to unknown, and every total counts both.</summary>
    [RetryFact]
    public async Task RunAsync_NoteCitingTheFile_FollowsTheFileRowsAndCountsInTheTotal()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para three", 1), (file, "para two", 2));
        await InsertNoteAsync(connection, file, "a note citing the doc", chunkIndex: 1);

        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        var positions = await PositionsByValueAsync(connection, file);
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three"].ShouldBe(2);
        positions["a note citing the doc"].ShouldBe(3);
        (await connection.QueryAsync<long>("SELECT total_chunks FROM entries WHERE source_file = @file", new { file }))
            .ShouldAllBe(total => total == 4);
    }

    /// <summary>A row at the right position whose total_chunks is not the row count is still a fix, and the report
    /// counts it so a dry run does not claim there is nothing to do.</summary>
    [RetryFact]
    public async Task RunAsync_DryRun_CountsRowsWhoseOnlyFaultIsTheirTotal()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one", 0), (file, "para two", 1), (file, "para three", 2));
        await connection.ExecuteAsync("UPDATE entries SET total_chunks = 5");

        var report = await Repair().RunAsync(connection, apply: false, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 3));
    }

    /// <summary>A file that gained a chunk before its stored rows still gives those rows positions below their
    /// count, in document order: the rows the file still has are ranked, not placed at the file's new offsets.</summary>
    [RetryFact]
    public async Task RunAsync_FileGrewBeforeItsRows_KeepsEveryPositionBelowTheTotal()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para zero\n\npara one\n\npara two", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para two", 0), (file, "para one", 1));

        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Value, long ChunkIndex, long TotalChunks)>(
            "SELECT value AS Value, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY chunk_index")).ToList();
        rows.Select(row => row.Value).ShouldBe(["para one", "para two"], "document order");
        rows.Select(row => row.ChunkIndex).ShouldBe([0L, 1L]);
        rows.ShouldAllBe(row => row.TotalChunks == 2);
    }

    /// <summary>Fixing only a row's total leaves its section, so the full-text index is not rewritten for it.</summary>
    [RetryFact]
    public async Task RunAsync_TotalOnlyFix_LeavesTheFullTextIndexAlone()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one", 0), (file, "para two", 1), (file, "para three", 2));
        await connection.ExecuteAsync("UPDATE entries SET total_chunks = 5");
        const string ftsBlocks = "SELECT group_concat(hex(block), '') FROM (SELECT block FROM entries_fts_data ORDER BY id)";
        var before = await connection.ExecuteScalarAsync<string>(ftsBlocks);

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.RowsRetotalled.ShouldBe(3, "premise: every row's total was wrong");
        (await connection.ExecuteScalarAsync<string>(ftsBlocks)).ShouldBe(before);
    }

    [RetryFact]
    public async Task RunAsync_SetsTheUnknownSentinel_WhenTheSourceFileNoLongerExists()
    {
        var missing = Path.Combine(_dataRoot, "gone.md");
        await using var connection = await OpenSeededAsync((missing, "some content", 0));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.RowsSetToUnknown.ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT chunk_index FROM entries WHERE path = @path", new { path = missing })).ShouldBe(-1);
    }

    [RetryFact]
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
    [RetryFact]
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

    /// <summary>#549: a repositioned row also takes the section the current chunker reports for it,
    /// so a repair fixes the label a pre-#549 ingest left null.</summary>
    [RetryFact]
    public async Task RunAsync_RepositionedRows_TakeTheSectionTheCurrentChunkerReports()
    {
        var file = Path.Combine(_dataRoot, "sectioned.md");
        await File.WriteAllTextAsync(file, "## Part A\n\npara one\n\npara two", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "## Part A", 2), (file, "para one", 1), (file, "para two", 0));

        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<string?>(
            "SELECT section FROM entries WHERE value = '## Part A'")).ShouldBe("Part A");
    }

    /// <summary>Copilot round 5 (comment 3838133861): a row the re-chunk cannot reproduce gets
    /// chunk_index = -1 (unknown), but the repair must not clear its existing section on the way —
    /// that would break its file#section anchor beyond the job's scope.</summary>
    [RetryFact]
    public async Task RunAsync_RowsTheRechunkCannotReproduce_KeepTheirExistingSection()
    {
        var file = Path.Combine(_dataRoot, "keep.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        const string staleValue = "stale content the file no longer holds";
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id,
                                 created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @sourceFile, @section, 'project', @projectId, 1, 1, 'embedded', @chunkIndex, @totalChunks)
            """,
            new
            {
                hash = ContentHash.Of(file, staleValue),
                path = file,
                value = staleValue,
                sourceFile = file,
                section = "Keep",
                projectId = ProjectId,
                chunkIndex = 0,
                totalChunks = 1
            });

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.RowsSetToUnknown.ShouldBe(1);
        var row = await connection.QuerySingleAsync<(long ChunkIndex, string? Section)>(
            "SELECT chunk_index AS ChunkIndex, section AS Section FROM entries WHERE path = @path", new { path = file });
        row.ChunkIndex.ShouldBe(-1);
        row.Section.ShouldBe("Keep");
    }

    /// <summary>Rows an older chunker cut differently still hold the file's text in a valid order: the repair keeps
    /// that order instead of setting the rows it cannot reproduce to unknown.</summary>
    [RetryFact]
    public async Task RunAsync_RowsFromAnOlderChunkingInValidOrder_KeepTheirPositions()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para three", 1));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(0);
        positions["para three"].ShouldBe(1);
    }

    /// <summary>A kept order still gets its totals fixed.</summary>
    [RetryFact]
    public async Task RunAsync_RowsFromAnOlderChunkingInValidOrder_StillTakeThePartitionRowCountAsTotal()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para three", 1));
        await connection.ExecuteAsync("UPDATE entries SET total_chunks = 5");

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 2));
        (await connection.QueryAsync<long>("SELECT chunk_index FROM entries WHERE source_file = @file ORDER BY chunk_index", new { file }))
            .ShouldBe([0L, 1L]);
    }

    /// <summary>When the rows the file does reproduce are stored out of document order, the stored order is not valid:
    /// the repair ranks those by the file and sets the older chunking's row to unknown, as before.</summary>
    [RetryFact]
    public async Task RunAsync_OlderChunkingWithReproducedRowsOutOfOrder_UsesTheFileOrder()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three\n\npara four", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para two", 0), (file, "para one", 1), (file, "para three\n\npara four", 2));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 2, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three\n\npara four"].ShouldBe(-1);
    }

    /// <summary>Stored positions with a gap are not a valid order to keep: the older chunking's row goes to unknown and
    /// the reproduced row takes its rank in the file.</summary>
    [RetryFact]
    public async Task RunAsync_OlderChunkingWithAGapInItsPositions_UsesTheFileOrder()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para three", 3));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 1, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(-1);
        positions["para three"].ShouldBe(0);
    }

    /// <summary>Only the row whose text left the file goes unknown: the older chunking's rows whose text is still
    /// in the file keep their stored positions.</summary>
    [RetryFact]
    public async Task RunAsync_RowsFromAnOlderChunkingWhoseTextLeftTheFile_GoUnknownAlone()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para zzz", 1));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(0);
        positions["para zzz"].ShouldBe(-1);
    }

    /// <summary>Line endings are not text gone from the file: the chunkers store rows with bare \n, so a CRLF
    /// file's rows keep their positions even though the raw bytes hold no exact match for their text.</summary>
    [RetryFact]
    public async Task RunAsync_RowsFromAnOlderChunkingOnACrlfFile_KeepTheirPositions()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\r\n\r\npara two\r\n\r\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para three", 1));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(0);
        positions["para three"].ShouldBe(1);
    }

    /// <summary>A row whose text moved elsewhere in the file has no place in the stored order: it goes to unknown, and
    /// the rows whose text still follows the stored order keep their positions.</summary>
    [RetryFact]
    public async Task RunAsync_OlderChunkingRowWhoseTextMoved_GoesUnknownAlone()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "r1\n\nr2\n\nt1\n\nt2\n\np1\n\np2", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "p1\n\np2", 0), (file, "r1\n\nr2", 1), (file, "t1\n\nt2", 2));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["p1\n\np2"].ShouldBe(-1);
        positions["r1\n\nr2"].ShouldBe(1);
        positions["t1\n\nt2"].ShouldBe(2);
    }

    /// <summary>Text that is still in the file only somewhere the stored order cannot put the row — its own section
    /// is gone and a copy survives elsewhere — is not the row's text: the row goes to unknown and the rows the file
    /// reproduces keep their places.</summary>
    [RetryFact]
    public async Task RunAsync_OlderChunkingRowWhoseTextSurvivesOnlyOutOfPlace_GoesUnknown()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "alpha\n\nx1\n\nx2\n\nbeta\n\ngamma\n\nx1\n\nx2", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "alpha", 0), (file, "beta", 1), (file, "x1\n\nx2", 2), (file, "gamma", 3));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["alpha"].ShouldBe(0);
        positions["beta"].ShouldBe(1);
        positions["x1\n\nx2"].ShouldBe(-1);
        positions["gamma"].ShouldBe(3);
    }

    /// <summary>A file saved with lone `\r` line endings: the chunkers read those as `\n` too, so the older chunking's
    /// rows are still in the file and keep their positions.</summary>
    [RetryFact]
    public async Task RunAsync_RowsFromAnOlderChunkingOnALoneCrFile_KeepTheirPositions()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\r\rpara two\r\rpara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one\n\npara two", 0), (file, "para three", 1));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(0);
        positions["para three"].ShouldBe(1);
    }

    /// <summary>Two rows stored at the same position are not a numbering to keep (#788): the repair ranks the rows the
    /// file reproduces and sends the older chunking's row to unknown, so no position is held twice.</summary>
    [RetryFact]
    public async Task RunAsync_OlderChunkingWithADuplicatePosition_UsesTheFileOrder()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three\n\npara four", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para two", 1), (file, "para three\n\npara four", 1));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 1, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three\n\npara four"].ShouldBe(-1);
    }

    /// <summary>A row already unknown has no place in the order proof: the scan reproducing its hash again does not
    /// unsettle a stored order the other rows prove, and the keep rule does not resurrect it.</summary>
    [RetryFact]
    public async Task RunAsync_AnUnknownRowTheScanReproduces_DoesNotUnsettleTheKeptOrder()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three\n\npara four\n\npara five",
            TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para two", 0), (file, "para three", 1), (file, "para one", -1), (file, "para four\n\npara five", 2));

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para two"].ShouldBe(0);
        positions["para three"].ShouldBe(1);
        positions["para one"].ShouldBe(-1);
        positions["para four\n\npara five"].ShouldBe(2);
    }

    /// <summary>What the rule writes it also keeps: its own output — kept positions, one unknown row — is a
    /// fixed point, so a second run changes nothing.</summary>
    [RetryFact]
    public async Task RunAsync_KeptOrderWithAnUnknownRow_IsItsOwnFixedPoint()
    {
        var file = Path.Combine(_dataRoot, "legacy.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one\n\npara two", 0), (file, "para zzz", 1), (file, "para three", 2));
        await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one\n\npara two"].ShouldBe(0);
        positions["para zzz"].ShouldBe(-1);
        positions["para three"].ShouldBe(2);
    }

    /// <summary>A row with no project id (legal: `project_id TEXT NULL`) still belongs to a partition. Before ADR-0124 its
    /// context key was NULL: GROUP BY grouped those rows while `= m.ctx` could never match NULL, the lookup came back empty,
    /// and the pass crashed on its first [0]. Since ADR-0124 the key is total (`custom:0::lab`), so the lookup matches it
    /// like any other partition — this pins the end-to-end repair of such a partition.</summary>
    [RetryFact]
    public async Task RunAsync_NullContextKeyPartition_IsRepairedLikeAnyOther()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para three", 1), (file, "para two", 2));
        // The row shape a write without a project id leaves behind: no project_id, non-shared scope.
        await connection.ExecuteAsync("UPDATE entries SET project_id = NULL, scope = 'custom', context_label = 'lab'");

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 2, 0, 0));
        var positions = await PositionsByValueAsync(connection, file);
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three"].ShouldBe(2);
    }

    /// <summary>The group list is a snapshot taken before the walk, and the walk re-chunks files for minutes while the bank
    /// stays live — on this server's bank 95 rows were deleted inside the crashed run's window (sync_tombstones). A partition
    /// deleted or re-keyed after the snapshot leaves a group id whose lookup finds nothing, and the pass must skip it — the
    /// group is gone, there is nothing to repair — rather than crash on the empty partition. The trigger stands in for that
    /// concurrent writer: the first UPDATE the pass makes (it only ever UPDATEs) deletes every other partition's rows, so
    /// whichever order the two groups are walked in, the second is gone by its turn.</summary>
    [RetryFact]
    public async Task RunAsync_APartitionDeletedAfterTheSnapshot_IsSkippedAndNotExamined()
    {
        var first = Path.Combine(_dataRoot, "first.md");
        var second = Path.Combine(_dataRoot, "second.md");
        await File.WriteAllTextAsync(first, "first one\n\nfirst two", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second one\n\nsecond two", TestContext.Current.CancellationToken);
        // Both partitions seeded with total_chunks = 4 (the row count seeded), so the surviving one is
        // guaranteed writes and the trigger below fires — its positions are already right.
        await using var connection = await OpenSeededAsync(
            (first, "first one", 0), (first, "first two", 1),
            (second, "second one", 0), (second, "second two", 1));
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER vanish_after_first_write AFTER UPDATE ON entries
            BEGIN
                DELETE FROM entries WHERE source_file <> NEW.source_file;
            END
            """);

        var report = await Repair().RunAsync(connection, apply: true, TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(1, 0, 0, 2));
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

    /// <summary>A memory_write note row whose source_file cites <paramref name="sourceFile" />.</summary>
    private static Task InsertNoteAsync(SqliteConnection connection, string sourceFile, string value, int chunkIndex)
    {
        const string notePath = "notes/review.md";
        return connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                 created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @sourceFile, 'project', @projectId, 1, 1, 'embedded', @chunkIndex, 4)
            """,
            new { hash = ContentHash.Of(notePath, value), path = notePath, value, sourceFile, projectId = ProjectId, chunkIndex });
    }

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
                    path = sourceFile,
                    value,
                    sourceFile,
                    projectId = ProjectId,
                    chunkIndex,
                    totalChunks = rows.Length
                });
        }

        return connection;
    }
}
