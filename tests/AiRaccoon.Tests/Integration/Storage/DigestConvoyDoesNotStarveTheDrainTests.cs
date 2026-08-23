using System.Collections.Concurrent;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
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
///     WP12: a concurrency-4 watch-digest convoy used to hold the bank's write lock through the
///     chunker for each of its four replaces — a fifth writer (here, the embed drain) waiting out
///     its own <c>busy_timeout</c> and throwing SQLITE_BUSY. Fix A moved the chunker outside the
///     lock, so the convoy's own transactions are now short regardless of how slow chunking is; this
///     races all four digests concurrently against a drain loop and asserts it never sees
///     <c>SqliteErrorCode == 5</c>.
///     <para>
///         The drain's own probe connection runs a short <c>busy_timeout</c> (200 ms, same technique
///         as <see cref="ReplaceHoldsTheLockOnlyForTheWriteTests" />) so a RED run fails fast instead
///         of waiting out the production factory's real 5 s default.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DigestConvoyDoesNotStarveTheDrainTests : IDisposable
{
    private const int DrainBusyTimeoutMs = 200;
    private const int FileCount = 4;
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(150);
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-digest-convoy");
    private readonly SqliteConnectionFactory _factory;

    public DigestConvoyDoesNotStarveTheDrainTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task FourConcurrentDigests_WithASlowChunker_NeverStarveAConcurrentDrainIntoBusy()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(FixedNow);
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var embeddings = TestData.CreateEmbeddingService();
        var realIngestor = new FileIngestor(
            new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]), sourceStore, time,
            embeddings, NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance);
        var slowIngestor = new SlowFileIngestor(realIngestor, ChunkDelay);
        var store = new SqliteMemoryStore(_factory, sourceStore, slowIngestor,
            new EntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), time), time,
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]), new SqliteSettingsStore(_factory),
            NullEmbedDrainPump.Instance, NoOpMeasurementRecorder.Instance);

        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);
        await SeedPendingEntriesAsync(ct);

        var files = new List<string>();
        for (var i = 0; i < FileCount; i++)
        {
            var file = Path.Combine(_dataRoot, $"convoy-{i}.md");
            await File.WriteAllTextAsync(file, $"convoy content number {i}", ct);
            files.Add(file);
        }

        var busyErrors = new ConcurrentBag<SqliteException>();
        var drainEmbedder = new EntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), time);
        using var stopDrain = new CancellationTokenSource();

        var drainLoop = Task.Run(async () =>
        {
            while (!stopDrain.IsCancellationRequested)
            {
                try
                {
                    await using var connection = await _factory.OpenBankAsync(ct).ConfigureAwait(false);
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"PRAGMA busy_timeout = {DrainBusyTimeoutMs}", cancellationToken: ct)).ConfigureAwait(false);
                    await drainEmbedder.EmbedPendingBatchAsync(connection, 8, ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    busyErrors.Add(ex);
                }

                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }, ct);

        var digestTasks = files
            .Select(file => RunDigestAsync(store, file, busyErrors, ct))
            .ToArray();
        await Task.WhenAll(digestTasks);

        stopDrain.Cancel();
        await drainLoop.ContinueWith(_ => { }, CancellationToken.None);

        busyErrors.ShouldBeEmpty(
            "a concurrency-4 digest convoy must never starve a concurrent drain into SQLITE_BUSY");
    }

    private static async Task RunDigestAsync(SqliteMemoryStore store, string file, ConcurrentBag<SqliteException> busyErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.ReplaceAsync("acme", file, $"hash-{Path.GetFileName(file)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            busyErrors.Add(ex);
        }
    }

    /// <summary>Rows for the drain loop to keep finding work on for the whole convoy — a different
    /// project than the digested files, so the drain and the digests never contend on the same rows,
    /// only the same write lock.</summary>
    private async Task SeedPendingEntriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" }, cancellationToken: cancellationToken));
        for (var i = 0; i < 200; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', 'drain-fixture', 0, 0)
                """,
                new { hash = $"drain-row-{i}", path = $"drain{i}.md", value = $"drain fixture row {i}" },
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>Delegates to a real <see cref="IFileIngestor" />, adding a fixed real delay before
    /// each <see cref="IngestFileAsync" /> call — simulates a chunker slow enough to give a convoy of
    /// concurrent replaces a real overlap window.</summary>
    private sealed class SlowFileIngestor(IFileIngestor inner, TimeSpan delay) : IFileIngestor
    {
        public async Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return await inner.IngestFileAsync(connection, projectId, path, context, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<DirectoryIngestResult> IngestDirectoryAsync(SqliteConnection connection, string projectId,
            string path, string? context, CancellationToken cancellationToken) =>
            inner.IngestDirectoryAsync(connection, projectId, path, context, cancellationToken);

        public Task<IReadOnlyList<string>> ChunkToBudgetAsync(SqliteConnection connection, string content,
            CancellationToken cancellationToken) =>
            inner.ChunkToBudgetAsync(connection, content, cancellationToken);
    }
}
