using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP11 (log-values-as-metrics): EventId 899's "Replace-by-path transaction held the write lock
///     for N ms (R row(s))" carries values that today only exist as log text. Recorded as the same
///     numbers the log line already computed, under the WRITING project's own id — ReplaceCoreAsync
///     already receives projectId, so there is no reason to fall back to the self-metrics sentinel.
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
            TestData.CreateEmbeddingService(), measurements: _measurements);
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

        var lockMs = _measurements.Recorded.Single(m => m.Name == "write.replace.lock_ms");
        lockMs.Kind.ShouldBe(MeasurementKind.Histogram);
        lockMs.ProjectId.ShouldBe("acme");
        lockMs.Value.ShouldBeGreaterThanOrEqualTo(0);

        var rows = _measurements.Recorded.Single(m => m.Name == "write.replace.rows");
        rows.Kind.ShouldBe(MeasurementKind.Histogram);
        rows.ProjectId.ShouldBe("acme");
        rows.Value.ShouldBeGreaterThan(0);
    }

    /// <summary>
    ///     M1 (#548 review, non-blocking): a guard-declined replace (fingerprint unchanged) still
    ///     records write.replace.rows = 0 and a real write.replace.lock_ms — same as what the log
    ///     line already does at the decline branch (Log.TransactionHeld(logger, declinedElapsedMs,
    ///     0)). Kept deliberately, not skipped: skipping would make the metric diverge from the log
    ///     line it is recorded beside, which is the one invariant this WP is built on ("the log
    ///     stays; the measurement is the source, the log formats it — no second computation"). See
    ///     docs/how-to/read-performance-metrics.md, "0 rows = declined/no-op replace".
    /// </summary>
    [Fact]
    public async Task ReplaceIfFileChangedAsync_WhenFingerprintUnchanged_RecordsZeroRowsButARealLockTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "declined.md");
        await File.WriteAllTextAsync(file, "content for the decline test", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        await _store.ReplaceIfFileChangedAsync("acme", file, "same-hash", ct); // runs, seeds the fingerprint
        _measurements.Recorded.Clear();

        await _store.ReplaceIfFileChangedAsync("acme", file, "same-hash", ct); // declines: fingerprint unchanged

        var rows = _measurements.Recorded.Single(m => m.Name == "write.replace.rows");
        rows.ProjectId.ShouldBe("acme");
        rows.Value.ShouldBe(0, "a declined replace wrote nothing — the log line already says 0 too");

        var lockMs = _measurements.Recorded.Single(m => m.Name == "write.replace.lock_ms");
        lockMs.ProjectId.ShouldBe("acme");
        lockMs.Value.ShouldBeGreaterThanOrEqualTo(0, "the lock was still taken and released, even though nothing changed");
    }
}
