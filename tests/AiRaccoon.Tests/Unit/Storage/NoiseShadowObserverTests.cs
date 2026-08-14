using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Shadow mode (ADR-0039): evaluates <see cref="INoiseDetector" /> against real write traffic
///     and records what it *would* have rejected, without ever rejecting anything — the per-install
///     measurement a future enforce decision needs. Gated by
///     noise.learner.shadow.enabled.global, default off. The seam is exercised here with
///     <see cref="NoOpNoiseDetector" /> (the only detector shipped) and a fake that reports a match,
///     to prove the plumbing works independently of any scoring model ever landing behind it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NoiseShadowObserverTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-shadow");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteNoiseClusterStore _store;
    private readonly EntryEmbedder _entryEmbedder = new(TestData.CreateEmbeddingService());

    public NoiseShadowObserverTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteNoiseClusterStore(_factory, NullLogger<SqliteNoiseClusterStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private NoiseShadowObserver CreateObserver(INoiseDetector? detector = null) =>
        new(_store,
            detector ?? new NoOpNoiseDetector(),
            new NoiseFeedbackCollector(_store, new SqliteContentEmbedder(_factory, _entryEmbedder)),
            NullLogger<NoiseShadowObserver>.Instance);

    private async Task<long> InsertEntryWithEmbeddingAsync(string content, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        await _entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null, ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
            VALUES (@Hash, @Path, @Value, 'project', @ProjectId, @Now, @Now)
            RETURNING id
            """,
            new { Hash = Guid.NewGuid().ToString("N"), Path = "note.md", Value = content, ProjectId = "proj-1", Now = now });

        await _entryEmbedder.EmbedIfConfiguredAsync(connection, id, content, ct);
        return id;
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowDisabled_ReturnsCleanAndNeverConsultsTheDetector()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await InsertEntryWithEmbeddingAsync("an ordinary note", ct);
        await using var connection = await _factory.OpenBankAsync(ct);

        var detector = new SpyDetector();
        var observer = CreateObserver(detector);
        var result = await observer.ObserveStoredWriteAsync(connection, "proj-1", "agent-1", id, "somehash", ct);

        result.IsNoise.ShouldBeFalse();
        detector.EvaluateCallCount.ShouldBe(0, "shadow disabled must skip the detector entirely, not just discard its verdict");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_NoOpDetector_AlwaysReturnsClean()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await InsertEntryWithEmbeddingAsync("an ordinary architectural note", ct);
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(); // default: NoOpNoiseDetector — no scoring model is shipped
        var result = await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", id, "somehash", ct);

        result.IsNoise.ShouldBeFalse("no detector is shipped yet (ADR-0039) — the NoOp default must never flag anything");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_ReusesTheAlreadyComputedVector_NoSecondInference()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await InsertEntryWithEmbeddingAsync("reuse the vector, do not re-embed", ct);
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        var detector = new SpyDetector();
        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(detector);
        await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", id, "somehash", ct);

        detector.EvaluateCallCount.ShouldBe(1);
        detector.LastVector.ShouldNotBeNull();
        detector.LastVector!.Length.ShouldBe(384, "the reused vector must be the real 384-dim embedding EmbedIfConfiguredAsync persisted");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_DetectorFlagsIt_RecordsButNeverBlocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await InsertEntryWithEmbeddingAsync("would be flagged by a future detector", ct);
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        var detector = new SpyDetector { AlwaysFlags = true };
        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(detector);
        var result = await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", id, "somehash", ct);

        result.IsNoise.ShouldBeTrue("the seam must surface whatever the injected detector decides");
        result.PolicyName.ShouldBe("fake-detector-match");
    }

    [Fact]
    public async Task ObserveRejectedWriteAsync_ShadowDisabled_NeverFeedsTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await _entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null, ct);

        var observer = CreateObserver();
        await observer.ObserveRejectedWriteAsync(connection, "proj-1", "agent-1",
            "[IMPORTANT: Background process proc_x completed normally (exit code 0).\nCommand: x\nOutput: ]",
            "HermesBackgroundProcessLog", ct);

        var clusters = await _store.GetClustersAsync("proj-1", "agent-1", ct);
        clusters.Count.ShouldBe(0, "shadow mode must be the single switch that enables learning from confirmed rejections too");
    }

    [Fact]
    public async Task ObserveRejectedWriteAsync_ShadowEnabled_AppendsAConfirmedNegativeSample()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await _entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null, ct);
        await SetShadowEnabledAsync(connection, ct);

        var observer = CreateObserver();
        await observer.ObserveRejectedWriteAsync(connection, "proj-1", "agent-1",
            "[IMPORTANT: Background process proc_x completed normally (exit code 0).\nCommand: x\nOutput: ]",
            "HermesBackgroundProcessLog", ct);

        var clusters = await _store.GetClustersAsync("proj-1", "agent-1", ct);
        clusters.Count.ShouldBe(1);
        clusters[0].Frequency.ShouldBe(1);
    }

    private static Task SetShadowEnabledAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct) =>
        connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@Key, 'true') ON CONFLICT(key) DO UPDATE SET value = 'true'",
            new { Key = NoiseConfigKeys.LearnerShadowEnabledGlobal });

    /// <summary>A detector double proving the seam is exercised — never a real scoring model.</summary>
    private sealed class SpyDetector : INoiseDetector
    {
        public int EvaluateCallCount { get; private set; }
        public float[]? LastVector { get; private set; }
        public bool AlwaysFlags { get; init; }

        public NoiseFilterResult Evaluate(float[] vector, IReadOnlyList<NoiseCluster> storedSamples)
        {
            EvaluateCallCount++;
            LastVector = vector;
            return AlwaysFlags ? NoiseFilterResult.Noise("fake-detector-match") : NoiseFilterResult.Clean;
        }
    }
}
