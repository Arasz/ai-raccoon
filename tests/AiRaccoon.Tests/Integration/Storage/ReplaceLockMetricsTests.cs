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
}
