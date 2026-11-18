using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP11 Finding (b): which writer holds the bank's write lock long enough to time out another
///     one (owner-observed `SQLITE_BUSY` after 5s) was not established — two candidates, and the
///     log alone could not separate them. This is that instrumentation: the held span of
///     <c>ReplaceCoreAsync</c>'s <c>BEGIN IMMEDIATE … COMMIT</c>, recorded as a logged value (never
///     asserted against a threshold — a wall-clock assertion on a real transaction would be flaky).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReplaceCoreTransactionSpanLogTests : IDisposable
{
    private const int TransactionHeldEventId = 899;
    private const int ReplaceSkippedEventId = 896;
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-replace-span-log");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<SqliteMemoryStore> _logger = new();
    private readonly SqliteMemoryStore _store;

    public ReplaceCoreTransactionSpanLogTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, _logger, new SqliteMemorySourceStore(_factory),
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>A store whose ingestor blocks until released — the seam a second digest races against.</summary>
    private (SqliteMemoryStore Store, BlockingFileIngestor Blocker, RecordingMeasurementRecorder Measurements)
        BlockingStore(FakeLogger<SqliteMemoryStore> logger)
    {
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var time = new FakeTimeProvider(FixedNow);
        var embeddings = TestData.CreateEmbeddingService();
        var realIngestor = new FileIngestor(
            new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]), sourceStore, time,
            embeddings, NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance, new IndexableFileWalk(), TestData.SharedScope,
            new EntryKindClassifier());
        var blocker = new BlockingFileIngestor(realIngestor);
        var measurements = new RecordingMeasurementRecorder();
        var store = new SqliteMemoryStore(_factory, sourceStore, blocker,
            TestData.CreateEntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), time, new VecDimensionReconciler()),
            time, logger, new NoiseFilteringService([]), new SqliteSettingsStore(_factory), NullEmbedDrainPump.Instance,
            measurements, TestData.SharedScope, new EntryKindClassifier());
        return (store, blocker, measurements);
    }

    private sealed class BlockingFileIngestor(IFileIngestor inner) : IFileIngestor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the moment <see cref="IngestFileAsync" /> is called, before it blocks.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task;
            return await inner.IngestFileAsync(connection, projectId, path, context, cancellationToken);
        }

        public Task<DirectoryIngestResult> IngestDirectoryAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken) =>
            inner.IngestDirectoryAsync(connection, projectId, path, context, cancellationToken);

        public Task<IReadOnlyList<string>> ChunkToBudgetAsync(SqliteConnection connection, string content,
            CancellationToken cancellationToken) => inner.ChunkToBudgetAsync(connection, content, cancellationToken);
    }

    /// <summary>
    ///     ADR-0151: EventId 899 means a replace ran. A digest that loses the race for its own path
    ///     (the guard re-check or the claim says another digest already did the work) writes 0 rows,
    ///     which is correct behaviour, so it logs at Debug under its own id — while the wait/held/rows
    ///     measurements keep recording, so a skip stays measurable without being printed by default.
    /// </summary>
    [RetryFact]
    public async Task ReplaceIfFileChangedAsync_WhenAnotherDigestHoldsTheClaim_LogsTheSkipAtDebug()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "claim-skip.md");
        await File.WriteAllTextAsync(file, "content for the claim-skip log test", ct);

        var (store, blocker, measurements) = BlockingStore(_logger);
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        var first = store.ReplaceIfFileChangedAsync("acme", file, "hash-a", ct);
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        try
        {
            // The first digest owns the claim and is still chunking; the second must skip, not chunk a
            // path whose row is about to be written by the digest ahead of it.
            var second = await store.ReplaceIfFileChangedAsync("acme", file, "hash-b", ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct);

            second.Replaced.ShouldBeFalse("a path another digest already claimed must not be chunked twice");
            _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == TransactionHeldEventId,
                "EventId 899 is the replace that ran — a skip must not be logged as one");
            var skip = _logger.Collector.GetSnapshot().Single(r => r.Id.Id == ReplaceSkippedEventId);
            skip.Level.ShouldBe(LogLevel.Debug);
            measurements.Recorded.ShouldContain(m => m.Name == MetricsConfigKeys.ReplaceRowsMetricName && m.Value == 0,
                "the skip still records its 0 rows: the noise was the level, not the measurement");
            measurements.Recorded.ShouldContain(m => m.Name == MetricsConfigKeys.ReplaceWaitMsMetricName,
                "a skip states the wait it spent on the lock, as the replace does");
            measurements.Recorded.ShouldContain(m => m.Name == MetricsConfigKeys.ReplaceHeldMsMetricName,
                "a skip states the hold it spent on the lock, as the replace does");
        }
        finally
        {
            // A failing assertion must not leave the first digest parked in the ingestor with an open
            // connection and a held claim: release it, then await it before the fixture deletes the root.
            blocker.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == TransactionHeldEventId && r.Level == LogLevel.Information,
            "the digest that did the work still reports its transaction at Information");
    }

    [RetryFact]
    public async Task ReplaceAsync_LogsTheHeldTransactionSpan()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "span-log.md");
        await File.WriteAllTextAsync(file, "content for the span log test", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        await _store.ReplaceAsync("acme", file, "fixed-hash", ct);

        var record = _logger.Collector.GetSnapshot()
            .SingleOrDefault(r => r.Id.Id == TransactionHeldEventId);
        record.ShouldNotBeNull("ReplaceCoreAsync must log the held span of its BEGIN IMMEDIATE … COMMIT");
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldContain("ms");
    }
}
