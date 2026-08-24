using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP11 (log-values-as-metrics) + WP12 (wait/held split): EventId 899's "Replace-by-path waited
///     W ms for the write lock and held it H ms (R row(s) written)" carries values that today only
///     exist as log text. Recorded as the same numbers the log line already computed, under the
///     WRITING project's own id — ReplaceCoreAsync already receives projectId, so there is no reason
///     to fall back to the self-metrics sentinel.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReplaceLockMetricsTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-replace-lock-metrics");
    private readonly SqliteConnectionFactory _factory;
    private readonly RecordingMeasurementRecorder _measurements = new();
    private readonly SqliteMemoryStore _store;

    public ReplaceLockMetricsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), measurements: _measurements, modelMigrationLease: null, jsonChunker: null, noisePolicies: null, settings: null, codeChunker: null, ignoreRulesProvider: null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReplaceAsync_RecordsTheHeldLockAndRowsUnderTheWritingProject()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "replace-metrics.md");
        await File.WriteAllTextAsync(file, "content for the replace-lock metrics test", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        await _store.ReplaceAsync("acme", file, "fixed-hash", ct);

        var waitMs = _measurements.Recorded.Single(m => m.Name == "write.replace.wait_ms");
        waitMs.Kind.ShouldBe(MeasurementKind.Histogram);
        waitMs.ProjectId.ShouldBe("acme");
        waitMs.Value.ShouldBeGreaterThanOrEqualTo(0);

        var heldMs = _measurements.Recorded.Single(m => m.Name == "write.replace.held_ms");
        heldMs.Kind.ShouldBe(MeasurementKind.Histogram);
        heldMs.ProjectId.ShouldBe("acme");
        heldMs.Value.ShouldBeGreaterThanOrEqualTo(0);

        var rows = _measurements.Recorded.Single(m => m.Name == "write.replace.rows");
        rows.Kind.ShouldBe(MeasurementKind.Histogram);
        rows.ProjectId.ShouldBe("acme");
        rows.Value.ShouldBeGreaterThan(0);
    }

    /// <summary>
    ///     WP12 Fix A superseded M1 (#548 review): <c>ReplaceCoreAsync</c> now runs the guard as a
    ///     cheap UNLOCKED read first, so the common no-race decline (fingerprint unchanged) never
    ///     reaches <c>BEGIN IMMEDIATE</c> at all — there is no wait or held time to record, so nothing
    ///     is. (The guard's authoritative re-check still runs a second time under the lock, for the
    ///     rare case of two replays racing the same stale-to-new transition; that path DOES take the
    ///     lock briefly and DOES still record real wait/held times with 0 rows — just not this one,
    ///     where nothing raced.)
    /// </summary>
    [Fact]
    public async Task ReplaceIfFileChangedAsync_WhenFingerprintUnchanged_RecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "declined.md");
        await File.WriteAllTextAsync(file, "content for the decline test", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        await _store.ReplaceIfFileChangedAsync("acme", file, "same-hash", ct); // runs, seeds the fingerprint
        _measurements.Recorded.Clear();

        await _store.ReplaceIfFileChangedAsync("acme", file, "same-hash", ct); // declines: fingerprint unchanged

        _measurements.Recorded.ShouldBeEmpty(
            "the unlocked guard declined before the write lock was ever touched — nothing to measure");
    }
}
