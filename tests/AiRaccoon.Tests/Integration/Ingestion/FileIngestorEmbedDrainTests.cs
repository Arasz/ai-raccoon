using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     E10 (docs/work/2026-08-22-post-delta-3-plan.md WP11-B2): a directory ingest used to embed
///     one generator call per chunk, serially, on the shared inference session — a third
///     uncoordinated consumer of the pool alongside the two maintenance jobs. It now embeds nothing
///     inline and signals the embed topic once for the whole walk.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FileIngestorEmbedDrainTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _testDir;

    public FileIngestorEmbedDrainTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_embed_drain_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var scopeCmd = _conn.CreateCommand();
        scopeCmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scopeCmd.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scopeCmd.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_testDir]));
        scopeCmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_testDir);
    }

    [Fact]
    public async Task IngestDirectory_EmbedsNoChunkInline_AndSignalsOnce()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "a.md"), "# A\ncontent a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "b.md"), "# B\ncontent b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "c.md"), "# C\ncontent c", TestContext.Current.CancellationToken);
        var pump = TestData.NewEmbedDrainPump();
        var embedder = new CountingEntryEmbedder();
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var sourceStore = new SqliteMemorySourceStore(new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        var ingestor = new FileIngestor(matcher, embedder, sourceStore, TimeProvider.System,
            TestData.CreateEmbeddingService(), embedDrainPump: pump);

        var result = await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        result.Indexed.ShouldBe(3);
        embedder.EmbedIfConfiguredCalls.ShouldBe(0, "the walk must never embed a chunk inline");
        pump.EnqueuedCount.ShouldBe(1, "one signal for the whole walk, not one per file");
        var queued = pump.DrainUpTo(1).ShouldHaveSingleItem();
        queued.Corpus.ShouldBe(EmbedCorpus.Memory);
    }

    [Fact]
    public async Task IngestDirectory_NoRowsIndexed_NeverSignals()
    {
        var pump = TestData.NewEmbedDrainPump();
        var embedder = new CountingEntryEmbedder();
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var sourceStore = new SqliteMemorySourceStore(new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        var ingestor = new FileIngestor(matcher, embedder, sourceStore, TimeProvider.System,
            TestData.CreateEmbeddingService(), embedDrainPump: pump);

        var result = await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        result.Indexed.ShouldBe(0);
        pump.EnqueuedCount.ShouldBe(0, "nothing was left pending — no reason to wake the drain consumer");
    }

    /// <summary>Records <see cref="EmbedIfConfiguredAsync" /> calls; every other member is unused by this path.</summary>
    private sealed class CountingEntryEmbedder : IEntryEmbedder
    {
        public int EmbedIfConfiguredCalls { get; private set; }

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken)
        {
            EmbedIfConfiguredCalls++;
            return Task.CompletedTask;
        }

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
