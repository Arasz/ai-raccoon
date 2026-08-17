using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Criterion 1 (this task): a rejected write must still record into noise_entries — end to end,
///     independent of the ADR-0039 learner/shadow switch — proving the ADR-0029 training-data path
///     actually works rather than being write-only again (ADR-0033's original finding on the first
///     incarnation of this store).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreNoiseEntryTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-entry-wiring");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreNoiseEntryTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private const string RejectedContent = "[IMPORTANT: Background process proc_x9f completed normally (exit code 0).\n" +
                                            "Command: dotnet build\nOutput: ]";

    private SqliteMemoryStore CreateStore(INoiseEntryStore noiseEntryStore)
    {
        var entryEmbedder = new EntryEmbedder(TestData.CreateEmbeddingService());
        var noiseFilteringService = new NoiseFilteringService([new HermesProcessNoisePolicy()]);
        return new SqliteMemoryStore(_factory, new SqliteMemorySourceStore(_factory),
            new FileIngestor(new FileTypeMatcher([]), entryEmbedder, new SqliteMemorySourceStore(_factory), new FakeTimeProvider(FixedNow),
                new LocalTokenizer()),
            entryEmbedder, new FakeTimeProvider(FixedNow), NullLogger<SqliteMemoryStore>.Instance,
            noiseFilteringService, new SqliteSettingsStore(_factory), NoOpNoiseShadowObserverForTests.Instance, noiseEntryStore);
    }

    [Fact]
    public async Task RejectedWrite_RecordsToNoiseEntries_WithItsPolicyName()
    {
        var store = CreateStore(new SqliteNoiseEntryStore(_factory));

        var entry = await store.WriteAsync(new MemoryWriteRequest("proj-1", RejectedContent, SourceFile: "note.md"),
            TestContext.Current.CancellationToken);

        entry.Stored.ShouldBeFalse();

        var noiseStore = new SqliteNoiseEntryStore(_factory);
        var recent = await noiseStore.ListRecentAsync(10, TestContext.Current.CancellationToken);

        recent.Count.ShouldBe(1);
        recent[0].RequestContent.ShouldBe(RejectedContent);
        recent[0].ProjectId.ShouldBe("proj-1");
        recent[0].SourceFile.ShouldBe("note.md");
        recent[0].DetectedByPolicy.ShouldBe("HermesBackgroundProcessLog");
    }

    [Fact]
    public async Task RejectedWrite_ExpiresAt_HonorsTheConfiguredRetentionSetting()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@Key, '30') ON CONFLICT(key) DO UPDATE SET value = '30'",
                new { Key = NoiseConfigKeys.RetentionDaysGlobal });
        }

        var store = CreateStore(new SqliteNoiseEntryStore(_factory));
        await store.WriteAsync(new MemoryWriteRequest("proj-1", RejectedContent), TestContext.Current.CancellationToken);

        var noiseStore = new SqliteNoiseEntryStore(_factory);
        var recent = await noiseStore.ListRecentAsync(10, TestContext.Current.CancellationToken);

        var expectedExpiry = FixedNow.AddDays(30).ToUnixTimeSeconds();
        recent[0].ExpiresAt.ShouldBe(expectedExpiry);
    }

    /// <summary>The Null Object default (7-arg ctor, used by TestData.CreateMemoryStore) must never touch noise_entries.</summary>
    [Fact]
    public async Task NoOpDefault_RejectedWrite_NeverTouchesNoiseEntries()
    {
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), noisePolicies: [new HermesProcessNoisePolicy()]);

        var entry = await store.WriteAsync(new MemoryWriteRequest("proj-1", RejectedContent), TestContext.Current.CancellationToken);
        entry.Stored.ShouldBeFalse();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM noise_entries");
        count.ShouldBe(0L, "the 7-arg construction path used throughout the suite must default to a no-op noise entry store");
    }

    private sealed class NoOpNoiseShadowObserverForTests : INoiseShadowObserver
    {
        public static readonly INoiseShadowObserver Instance = new NoOpNoiseShadowObserverForTests();

        public Task<NoiseFilterResult> ObserveStoredWriteAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
            string projectId, string? agentId, string content, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(NoiseFilterResult.Clean);
    }
}
