using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP12 Fix A: <c>ReplaceCoreAsync</c> used to run <c>fileIngestor.IngestFileAsync</c> (file
///     read, chunking, hashing, inserts) between <c>BEGIN IMMEDIATE</c> and <c>COMMIT</c> — the write
///     lock was held through the chunker for the whole call. This proves the fix by racing a second
///     connection against a chunker that is still "running" (blocked on a
///     <see cref="TaskCompletionSource" />): today, that second connection's own
///     <c>BEGIN IMMEDIATE</c> would wait out its <c>busy_timeout</c> and throw SQLITE_BUSY; after the
///     fix, it succeeds immediately, because the write lock is only requested AFTER the chunker
///     returns.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReplaceHoldsTheLockOnlyForTheWriteTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-replace-lock-scope");
    private readonly SqliteConnectionFactory _factory;

    public ReplaceHoldsTheLockOnlyForTheWriteTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReplaceAsync_WhileTheChunkerIsStillRunning_TheWriteLockIsFreeForAnotherConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "blocking-ingest.md");
        await File.WriteAllTextAsync(file, "content for the lock-scope test", ct);

        var sourceStore = new SqliteMemorySourceStore(_factory);
        var time = new FakeTimeProvider(FixedNow);
        var embeddings = TestData.CreateEmbeddingService();
        var realIngestor = new FileIngestor(
            new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]), sourceStore, time,
            embeddings, NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance);
        var blockingIngestor = new BlockingFileIngestor(realIngestor);
        var store = new SqliteMemoryStore(_factory, sourceStore, blockingIngestor,
            new EntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), time, new VecDimensionReconciler()), time,
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]), new SqliteSettingsStore(_factory),
            NullEmbedDrainPump.Instance, NoOpMeasurementRecorder.Instance);

        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        var replaceTask = store.ReplaceAsync("acme", file, "fixed-hash", ct);
        await blockingIngestor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // While the chunker is still blocked, a second connection's BEGIN IMMEDIATE must succeed. A
        // short busy_timeout on this probe connection keeps a RED failure fast (today it would wait
        // out this timeout and throw SQLITE_BUSY) instead of the real 5s default.
        await using (var probe = await _factory.OpenBankAsync(ct))
        {
            await probe.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout = 200", cancellationToken: ct));
            await probe.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: ct));
            await probe.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: ct));
        }

        blockingIngestor.Release();
        await replaceTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        await using var verify = await _factory.OpenBankAsync(ct);
        var rows = await verify.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND source_file = @file",
            new { projectId = "acme", file });
        rows.ShouldBeGreaterThan(0, "the replace must have completed once the chunker was released");
        (await verify.ExecuteScalarAsync<string?>(
                "SELECT file_hash FROM watch_files WHERE project_id = @projectId AND path = @path",
                new { projectId = "acme", path = file }))
            .ShouldBe("fixed-hash", "the fingerprint must land — the replace ran to completion, not just the ingest");
    }

    /// <summary>Delegates to a real <see cref="IFileIngestor" />, but blocks inside
    /// <see cref="IngestFileAsync" /> until <see cref="Release" /> is called — the seam this test
    /// uses to hold the "chunker" open while it probes the write lock from a second connection.</summary>
    private sealed class BlockingFileIngestor(IFileIngestor inner) : IFileIngestor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the moment <see cref="IngestFileAsync" /> is called, before it blocks.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await inner.IngestFileAsync(connection, projectId, path, context, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<DirectoryIngestResult> IngestDirectoryAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken) =>
            inner.IngestDirectoryAsync(connection, projectId, path, context, cancellationToken);

        public Task<IReadOnlyList<string>> ChunkToBudgetAsync(SqliteConnection connection, string content,
            CancellationToken cancellationToken) =>
            inner.ChunkToBudgetAsync(connection, content, cancellationToken);

        public void Release() => _release.TrySetResult();
    }
}
