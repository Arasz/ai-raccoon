using System.Diagnostics;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0075 amendment: the server side of <see cref="IRepairStore" />. Reports scan the bank
///     read-only, exactly like the CLI used to do locally before this change — only the process that
///     does it moved. Requests write the repair_requests outbox row, mirroring model_migration's
///     shape (ADR-0076) but keyed by kind since chunk-index and reingest repairs are independent.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteRepairStoreTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long the writer-held latch is given before absence is believed (mirrors ProjectIdsRepairContendedLockTests).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = TestData.CreateTempRoot("repair-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _memoryStore;
    private readonly SqliteRepairStore _store;

    public SqliteRepairStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _memoryStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        _store = new SqliteRepairStore(_factory, matcher, TestData.CreateEmbeddingService(), _memoryStore, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task ReportReingestAsync_NeverWrites_OnAnUnaffectedBank()
    {
        var report = await _store.ReportReingestAsync(TestContext.Current.CancellationToken);

        report.FilesToReingest.ShouldBe(0);
    }

    [RetryFact]
    public async Task ReportChunkIndexAsync_NeverWrites_OnAnUnaffectedBank()
    {
        var report = await _store.ReportChunkIndexAsync(TestContext.Current.CancellationToken);

        report.RowsRepositioned.ShouldBe(0);
    }

    [RetryFact]
    public async Task ReportReingestAsync_FindsAFileWithAStaleHash()
    {
        var file = Path.Combine(_dataRoot, "stale.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await _memoryStore.SetSettingAsync(IngestScopeKeys.ScopeGlobal,
            IngestScopeKeys.Serialize([_dataRoot]), TestContext.Current.CancellationToken);
        await _memoryStore.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            var id = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM entries WHERE source_file = @file ORDER BY id LIMIT 1", new { file });
            await connection.ExecuteAsync(
                "UPDATE entries SET hash = @staleHash, chunk_index = -1 WHERE id = @id",
                new { staleHash = "stale-" + Guid.NewGuid().ToString("N"), id });
        }

        var report = await _store.ReportReingestAsync(TestContext.Current.CancellationToken);

        report.FilesToReingest.ShouldBe(1);
    }

    [RetryFact]
    public async Task RequestRepairAsync_InsertsAnOpenRequestRow()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("reingest")).ShouldBe(1);
    }

    [RetryFact]
    public async Task RequestRepairAsync_IsScopedToItsOwnKind()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("chunk-index")).ShouldBe(0);
    }

    [RetryFact]
    public async Task RequestRepairAsync_CalledTwice_StaysOneRow()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM repair_requests WHERE kind = 'reingest'"))
            .ShouldBe(1);
    }

    [RetryFact]
    public async Task RequestRepairAsync_AfterAPreviousRequestFinished_ReopensIt()
    {
        await _store.RequestRepairAsync(RepairKind.ChunkIndex, TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE repair_requests SET finished_at = 1 WHERE kind = 'chunk-index'");
        }

        await _store.RequestRepairAsync(RepairKind.ChunkIndex, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("chunk-index")).ShouldBe(1);
    }

    /// <summary>
    ///     WP2 contingency (prove-the-check-fails): a legacy watch.scope.* row makes
    ///     MigrateIngestScopeKeysAsync attempt a write on every bank open, including the read-only
    ///     report path's own OpenBankAsync call — while another connection holds BEGIN IMMEDIATE
    ///     that write must wait out busy_timeout and fail. A read-only report must never be able to
    ///     fail this way.
    /// </summary>
    [RetryFact]
    public async Task ReportProjectIdsAsync_WhileAnotherConnectionHoldsTheWriteLock_StillAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var writerConnection = await _factory.OpenBankAsync(ct);
        var (writer, releaseWriter) = await HoldWriteLockOverLegacyScopeRowAsync(writerConnection, ct);

        var (thrown, elapsed) = await TimeAsync(() => _store.ReportProjectIdsAsync(ct));

        releaseWriter.TrySetResult(true);
        await writer;

        thrown.ShouldBeNull($"a read-only report must not fail under write contention, but got: {thrown}");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a read-only report must not wait on the bank's write lock");
    }

    /// <summary>
    ///     T4: a genuinely fresh, never-opened bank file must still get its schema created and answer
    ///     correctly through the cheap-open path — EnsureCheapAsync's digest-mismatch fallback to the
    ///     full ladder must fire for a fresh bank exactly as OpenBankAsync did before.
    /// </summary>
    [RetryFact]
    public async Task ReportProjectIdsAsync_OnANeverOpenedBank_CreatesTheSchemaAndAnswers()
    {
        var report = await _store.ReportProjectIdsAsync(TestContext.Current.CancellationToken);

        report.Rows.ShouldBeEmpty();
        report.NullScopeEntries.ShouldBe(0);
    }

    /// <summary>T5: same contention shape as T3, for the other two report methods.</summary>
    [RetryFact]
    public async Task ReportReingestAsync_AndReportChunkIndexAsync_WhileAnotherConnectionHoldsTheWriteLock_StillAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var writerConnection = await _factory.OpenBankAsync(ct);
        var (writer, releaseWriter) = await HoldWriteLockOverLegacyScopeRowAsync(writerConnection, ct);

        var (reingestThrown, reingestElapsed) = await TimeAsync(() => _store.ReportReingestAsync(ct));
        var (chunkIndexThrown, chunkIndexElapsed) = await TimeAsync(() => _store.ReportChunkIndexAsync(ct));

        releaseWriter.TrySetResult(true);
        await writer;

        reingestThrown.ShouldBeNull($"ReportReingestAsync must not fail under write contention, but got: {reingestThrown}");
        reingestElapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "ReportReingestAsync must not wait on the bank's write lock");
        chunkIndexThrown.ShouldBeNull($"ReportChunkIndexAsync must not fail under write contention, but got: {chunkIndexThrown}");
        chunkIndexElapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "ReportChunkIndexAsync must not wait on the bank's write lock");
    }

    /// <summary>
    ///     Seeds a legacy watch.scope.* row through <paramref name="writerConnection" />, then holds
    ///     BEGIN IMMEDIATE open on it until the returned <see cref="TaskCompletionSource{TResult}" />
    ///     is completed — the shared setup behind T3 and T5's write-lock-contention scenario.
    /// </summary>
    private async Task<(Task Writer, TaskCompletionSource<bool> Release)> HoldWriteLockOverLegacyScopeRowAsync(
        SqliteConnection writerConnection, CancellationToken cancellationToken)
    {
        await writerConnection.ExecuteAsync("INSERT INTO settings (key, value) VALUES ('watch.scope.acme', '[]')");

        var lockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await writerConnection.ExecuteAsync("BEGIN IMMEDIATE");
            try
            {
                await writerConnection.ExecuteAsync("UPDATE settings SET value = value WHERE key = 'watch.scope.acme'");
                lockHeld.TrySetResult(true);
                await releaseWriter.Task.WaitAsync(Patience, cancellationToken);
            }
            finally
            {
                await writerConnection.ExecuteAsync("COMMIT");
            }
        }, cancellationToken);
        await lockHeld.Task.WaitAsync(Patience, cancellationToken);

        return (writer, releaseWriter);
    }

    /// <summary>Runs <paramref name="action" />, capturing any thrown exception and the elapsed time instead of letting either propagate/go unmeasured.</summary>
    private static async Task<(Exception? Thrown, TimeSpan Elapsed)> TimeAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? thrown = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            stopwatch.Stop();
        }

        return (thrown, stopwatch.Elapsed);
    }

    private async Task<long> OpenRequestCountAsync(string kind)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM repair_requests WHERE kind = @kind AND finished_at IS NULL", new { kind });
    }

    /// <summary>ADR-0099: the one-shot project-ids map rides the request row and reads back verbatim; other kinds store null.</summary>
    [RetryFact]
    public async Task RequestRepairAsync_WithProjectIdsMapJson_RoundTrips()
    {
        var mapJson = new AiRaccoon.Core.Projects.ProjectIdAliasMap(
            [new AiRaccoon.Core.Projects.ProjectIdAliasEntry("old-id", "new-id")], ["new-id"], []).ToJson();
        await _store.RequestRepairAsync(RepairKind.ProjectIds, TestContext.Current.CancellationToken, mapJson);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<string?>(
            "SELECT map_json FROM repair_requests WHERE kind = 'project-ids'"))
            .ShouldBe(mapJson);
    }

    [RetryFact]
    public async Task RequestRepairAsync_WithoutMapJson_StoresNull()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<string?>(
            "SELECT map_json FROM repair_requests WHERE kind = 'reingest'"))
            .ShouldBeNull();
    }
}
