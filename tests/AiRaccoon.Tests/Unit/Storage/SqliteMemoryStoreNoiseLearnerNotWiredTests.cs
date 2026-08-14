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
///     Criterion 3 (ADR-0039): with the noise-learning subsystem's kill switch off — the default —
///     a rejected write leaves noise_clusters untouched and the write path behaves exactly as before
///     this subsystem's restoration. Two scenarios: TestData.CreateMemoryStore's 7-arg construction
///     (used throughout the existing suite, out of this lane's ownership) falls back to
///     NoOpNoiseShadowObserver entirely; separately, the real NoiseShadowObserver with its own
///     settings-gated switch left at its default (off) produces the same zero-clustering-work result.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreNoiseLearnerNotWiredTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-not-wired");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreNoiseLearnerNotWiredTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private const string RejectedContent = "[IMPORTANT: Background process proc_x9f completed normally (exit code 0).\n" +
                                            "Command: dotnet build\nOutput: ]";

    [Fact]
    public async Task NoOpShadowObserverFallback_RejectedWrite_NeverTouchesNoiseClusters()
    {
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), noisePolicies: [new HermesProcessNoisePolicy()]);

        var entry = await store.WriteAsync(new MemoryWriteRequest("proj-1", RejectedContent), TestContext.Current.CancellationToken);

        entry.Stored.ShouldBeFalse("this content must still be rejected by the existing HermesProcessNoisePolicy (ADR-0032/0033)");
        entry.Reason.ShouldNotBeNull();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var clusterCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM noise_clusters");
        clusterCount.ShouldBe(0L);
    }

    [Fact]
    public async Task RealShadowObserver_ShadowSwitchLeftAtItsDefault_RejectedWrite_NeverTouchesNoiseClusters()
    {
        var clusterStore = new SqliteNoiseClusterStore(_factory, NullLogger<SqliteNoiseClusterStore>.Instance);
        var entryEmbedder = new EntryEmbedder(TestData.CreateEmbeddingService());
        var contentEmbedder = new SqliteContentEmbedder(_factory, entryEmbedder);
        var feedbackCollector = new NoiseFeedbackCollector(clusterStore, contentEmbedder);
        var shadowObserver = new NoiseShadowObserver(clusterStore, new NoOpNoiseDetector(), feedbackCollector,
            NullLogger<NoiseShadowObserver>.Instance);
        var noiseFilteringService = new NoiseFilteringService([new HermesProcessNoisePolicy()]);

        var store = new SqliteMemoryStore(_factory, new SqliteMemorySourceStore(_factory),
            new FileIngestor(new FileTypeMatcher([]), entryEmbedder, new SqliteMemorySourceStore(_factory), new FakeTimeProvider(FixedNow)),
            entryEmbedder, new FakeTimeProvider(FixedNow), NullLogger<SqliteMemoryStore>.Instance,
            noiseFilteringService, shadowObserver);

        // noise.learner.shadow.enabled.global is left unset — the default (off) applies.
        var entry = await store.WriteAsync(new MemoryWriteRequest("proj-1", RejectedContent), TestContext.Current.CancellationToken);

        entry.Stored.ShouldBeFalse();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var clusterCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM noise_clusters");
        clusterCount.ShouldBe(0L, "the real observer's own settings-gated switch defaults off, so it must do no clustering work either");
    }
}
