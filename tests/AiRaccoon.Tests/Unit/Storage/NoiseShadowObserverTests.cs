using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Shadow mode (ADR-0039, amended by this task): evaluates <see cref="INoiseDetector" /> against
///     the stored write's own content and records what it *would* have rejected, without ever
///     rejecting anything. No vector, no stored-sample lookup — the research settled on a
///     structural/lexical classifier over raw content, so there is nothing embedding-shaped left to
///     reuse or fetch. Gated by noise.learner.shadow.enabled.global, default off. The seam is
///     exercised here with <see cref="NoOpNoiseDetector" /> (the only detector shipped) and a fake
///     that reports a match, to prove the plumbing works independently of any scoring model ever
///     landing behind it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NoiseShadowObserverTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-shadow");
    private readonly SqliteConnectionFactory _factory;

    public NoiseShadowObserverTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static NoiseShadowObserver CreateObserver(INoiseDetector? detector = null) =>
        new(detector ?? new NoOpNoiseDetector(), NullLogger<NoiseShadowObserver>.Instance);

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowDisabled_ReturnsCleanAndNeverConsultsTheDetector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);

        var detector = new SpyDetector();
        var observer = CreateObserver(detector);
        var result = await observer.ObserveStoredWriteAsync(connection, "proj-1", "agent-1", "an ordinary note", "somehash", ct);

        result.IsNoise.ShouldBeFalse();
        detector.EvaluateCallCount.ShouldBe(0, "shadow disabled must skip the detector entirely, not just discard its verdict");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_NoOpDetector_AlwaysReturnsClean()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(); // default: NoOpNoiseDetector — no scoring model is shipped
        var result = await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", "an ordinary architectural note", "somehash", ct);

        result.IsNoise.ShouldBeFalse("no detector is shipped yet (ADR-0039) — the NoOp default must never flag anything");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_PassesTheWrittenContentStraightToTheDetector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        var detector = new SpyDetector();
        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(detector);
        await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", "evaluate this exact content", "somehash", ct);

        detector.EvaluateCallCount.ShouldBe(1);
        detector.LastContent.ShouldBe("evaluate this exact content");
    }

    [Fact]
    public async Task ObserveStoredWriteAsync_ShadowEnabled_DetectorFlagsIt_RecordsButNeverBlocks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await SetShadowEnabledAsync(connection, ct);
        }

        var detector = new SpyDetector { AlwaysFlags = true };
        await using var conn2 = await _factory.OpenBankAsync(ct);
        var observer = CreateObserver(detector);
        var result = await observer.ObserveStoredWriteAsync(conn2, "proj-1", "agent-1", "would be flagged by a future detector", "somehash", ct);

        result.IsNoise.ShouldBeTrue("the seam must surface whatever the injected detector decides");
        result.PolicyName.ShouldBe("fake-detector-match");
    }

    private static Task SetShadowEnabledAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct) =>
        connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@Key, 'true') ON CONFLICT(key) DO UPDATE SET value = 'true'",
            new { Key = NoiseConfigKeys.LearnerShadowEnabledGlobal });

    /// <summary>A detector double proving the seam is exercised — never a real scoring model.</summary>
    private sealed class SpyDetector : INoiseDetector
    {
        public int EvaluateCallCount { get; private set; }
        public string? LastContent { get; private set; }
        public bool AlwaysFlags { get; init; }

        public NoiseFilterResult Evaluate(string content)
        {
            EvaluateCallCount++;
            LastContent = content;
            return AlwaysFlags ? NoiseFilterResult.Noise("fake-detector-match") : NoiseFilterResult.Clean;
        }
    }
}
