using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
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
///     GH #371 follow-up: <see cref="ChunkIndexRepair" /> can only mark a row chunk_index = -1 when a
///     chunker change makes it unreproducible by hash — it never guesses. <see cref="ReingestRepair" />
///     fixes those files for real, by routing through <see cref="SqliteMemoryStore.ReplaceAsync" />'s
///     unconditional delete-and-reingest instead of ChunkIndexRepair's pure UPDATE.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReingestRepairTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("reingest-repair");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public ReingestRepairTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        // A paragraph-per-chunk stub, not the real chunker: the real one packs short paragraphs
        // into one chunk under budget, which would defeat these fixtures' "N paragraphs, N rows"
        // premise. The repair's own scanner below is built on the same stub, so detection agrees
        // with what actually got ingested.
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task RunAsync_DryRun_ChangesNothing()
    {
        var file = Path.Combine(_dataRoot, "stale-dry-run.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await StampStaleHashAsync(file);
        var before = await SnapshotAsync(file);

        var report = await RunRepairAsync(apply: false);

        report.FilesToReingest.ShouldBe(1, "the stale-hash row must be detected");
        (await SnapshotAsync(file)).ShouldBe(before, "a dry run must not write");
    }

    [RetryFact]
    public async Task RunAsync_Apply_GivesTheStrandedRowsRealPositions()
    {
        var file = Path.Combine(_dataRoot, "stale-apply.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await StampStaleHashAsync(file);

        var report = await RunRepairAsync(apply: true);

        report.FilesToReingest.ShouldBe(1);
        var positions = await PositionsByValueAsync(file);
        positions.Values.ShouldAllBe(index => index >= 0, "every row must have a real document position after reingest");
        positions["para one"].ShouldBe(0);
        positions["para two"].ShouldBe(1);
        positions["para three"].ShouldBe(2);
    }

    /// <summary>
    ///     The realistic reingest scenario, and the exact gate the ReplaceIfFileChangedAsync/
    ///     ReplaceAsync split exists to prove: the file's own content never changed — only the
    ///     chunker did — so a watch fingerprint recorded earlier still matches it exactly. Repair
    ///     must replace anyway; it calls the unconditional <c>ReplaceAsync</c>, never the
    ///     fingerprint-gated <c>ReplaceIfFileChangedAsync</c> the watch digest uses.
    /// </summary>
    [RetryFact]
    public async Task RunAsync_Apply_ReplacesEvenThoughAMatchingWatchFingerprintAlreadyExists()
    {
        var file = Path.Combine(_dataRoot, "stale-fingerprinted.md");
        const string content = "para one\n\npara two\n\npara three";
        await File.WriteAllTextAsync(file, content, TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await StampStaleHashAsync(file);
        await SeedMatchingWatchFingerprintAsync(file, content);

        var report = await RunRepairAsync(apply: true);

        report.FilesToReingest.ShouldBe(1);
        var positions = await PositionsByValueAsync(file);
        positions.Values.ShouldAllBe(index => index >= 0,
            "a matching watch fingerprint must not gate the repair off — ReplaceAsync runs unconditionally");
    }

    [RetryFact]
    public async Task RunAsync_Apply_RemovesTheOldRows_RatherThanDuplicatingThem()
    {
        var file = Path.Combine(_dataRoot, "stale-no-dup.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        var staleHash = await StampStaleHashAsync(file);

        await RunRepairAsync(apply: true);

        var hashes = await RowHashesForAsync(file);
        hashes.Count.ShouldBe(2, "the row count for this source must not grow (ADR-0069 accident)");
        hashes.ShouldNotContain(staleHash, "the stale hash must not survive the replace");
    }

    [RetryFact]
    public async Task RunAsync_Apply_NeverTouchesAManualRowCitingTheSameSourceFile()
    {
        var file = Path.Combine(_dataRoot, "cited.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await StampStaleHashAsync(file);
        var manual = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "manual note citing the file", SourceFile: file),
            TestContext.Current.CancellationToken);

        await RunRepairAsync(apply: true);

        var metadata = await _store.GetMetadataAsync(ProjectId, manual.Hash, TestContext.Current.CancellationToken);
        metadata.ShouldNotBeNull("a memory_write row citing the file as source_file must survive reingest untouched");
    }

    [RetryFact]
    public async Task RunAsync_Apply_NeverTouchesAGroupWhoseFileIsGone()
    {
        var missing = Path.Combine(_dataRoot, "gone.md");
        await SeedOrphanedRowAsync(missing);

        var report = await RunRepairAsync(apply: true);

        report.FilesToReingest.ShouldBe(0, "a source_file that no longer exists must never be a reingest candidate");
        (await RowHashesForAsync(missing)).Count.ShouldBe(1, "the orphaned row must be left exactly as it was");
    }

    private async Task<ReingestRepairReport> RunRepairAsync(bool apply)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        var repair = new ReingestRepair(new ChunkPositionScanner(matcher, TestData.CreateEmbeddingService()));
        return await repair.RunAsync(connection, _store, apply, TestContext.Current.CancellationToken);
    }

    private Task ScopeDataRootAsync() =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

    /// <summary>
    ///     Simulates a chunker-version boundary shift: overwrites one row's hash so this pass's
    ///     re-chunk cannot reproduce it, and resets its chunk_index to -1 — the same symptom a real
    ///     chunker change leaves behind and ChunkIndexRepair would already have marked (GH #371),
    ///     without depending on there actually being two chunker versions in this test. Resetting
    ///     chunk_index too matters: leaving it at its already-valid ingested value would make a
    ///     skipped replace indistinguishable from a real one in every test's own assertions.
    /// </summary>
    private async Task<string> StampStaleHashAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var id = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM entries WHERE source_file = @file ORDER BY id LIMIT 1", new { file });
        var staleHash = "stale-" + Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(
            "UPDATE entries SET hash = @staleHash, chunk_index = -1 WHERE id = @id", new { staleHash, id });
        return staleHash;
    }

    private async Task SeedMatchingWatchFingerprintAsync(string file, string content)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var fileHash = WatchDigestExecutor.ComputeHash(file, content);
        await connection.ExecuteAsync(
            "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES (@projectId, @path, @fileHash, 1)",
            new { projectId = ProjectId, path = file, fileHash });
    }

    private async Task SeedOrphanedRowAsync(string missingFile)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                 created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @sourceFile, 'project', @projectId, 1, 1, 'embedded', -1, 1)
            """,
            new
            {
                hash = ContentHash.Of(missingFile, "orphaned content"),
                path = missingFile, value = "orphaned content", sourceFile = missingFile, projectId = ProjectId
            });
    }

    private async Task<List<(string Hash, long ChunkIndex)>> SnapshotAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return
        [
            .. await connection.QueryAsync<(string Hash, long ChunkIndex)>(
                "SELECT hash AS Hash, chunk_index AS ChunkIndex FROM entries WHERE source_file = @file ORDER BY hash",
                new { file })
        ];
    }

    private async Task<List<string>> RowHashesForAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return
        [
            .. await connection.QueryAsync<string>(
                "SELECT hash FROM entries WHERE source_file = @file", new { file })
        ];
    }

    private async Task<Dictionary<string, long>> PositionsByValueAsync(string file)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return (await connection.QueryAsync<(string Value, long ChunkIndex)>(
                "SELECT value AS Value, chunk_index AS ChunkIndex FROM entries WHERE source_file = @file", new { file }))
            .ToDictionary(r => r.Value, r => r.ChunkIndex, StringComparer.Ordinal);
    }
}
