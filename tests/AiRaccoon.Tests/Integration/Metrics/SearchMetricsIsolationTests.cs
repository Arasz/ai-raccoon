using System.Reflection;
using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Metrics;

/// <summary>
///     WP3 AC4 and G4 (reshaped, ruling 2): the `metrics` row count must be unchanged when
///     memory_search returns, flusher paused
///     (docs/plans/2026-08-15-performance-metrics-implementation.md).
///
///     WP10 wired <see cref="IMeasurementRecorder" /> into <c>MemoryTools.Search</c> itself — the
///     gap WP1-WP8 left open, recorded in this file's earlier revision. This test now constructs
///     the real chain (<see cref="MetricsRecorder" /> over a real <see cref="MeasurementBuffer" />,
///     deliberately with no <see cref="MetricsFlusher" /> running — "paused" means it is never
///     constructed), so a search's nine series measurements really do enqueue, and the assertion
///     below is a genuine proof that they land in the buffer, not the table, before the call
///     returns — not vacuous by absence of a wired recorder, as it was before WP10.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchMetricsIsolationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly MeasurementBuffer _buffer = new(1000);
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-search-metrics-isolation");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMetricsStore _metricsStore;
    private readonly IMemoryStore _store;
    private readonly MemoryTools _tools;

    public SearchMetricsIsolationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _metricsStore = new SqliteMetricsStore(_factory, NullLogger<SqliteMetricsStore>.Instance);

        // The flusher is deliberately never constructed here — "paused" for this test means it
        // does not exist, so nothing but the assertions themselves can touch the metrics table.
        var sourceStore = new SqliteMemorySourceStore(_factory);
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, sourceStore,
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());
        var recorder = new MetricsRecorder(_buffer, NullLogger<MetricsRecorder>.Instance);
        _tools = BuildTools(recorder);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private MemoryTools BuildTools(IMeasurementRecorder recorder) =>
        new(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue()),
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), recorder,
            NullLogger<MemoryTools>.Instance);

    private async Task<int> CountMetricsRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM metrics");
    }

    [Fact]
    public async Task Search_MetricsRowCountIsUnchangedWhenTheCallReturns()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);

        var before = await CountMetricsRowsAsync();

        await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);

        var after = await CountMetricsRowsAsync();
        after.ShouldBe(before, "no measurement may be written on the caller's thread during memory_search");
    }

    /// <summary>
    ///     Spec scenario 20: "a search returns before its measurement is written" — the row-count
    ///     assertion above proves nothing moved, but the scenario also claims the search itself still
    ///     returns its results, and that the report does not yet include it. Both are asserted here,
    ///     not just the count.
    /// </summary>
    [Fact]
    public async Task Search_ReturnsItsResultsAndIsExcludedFromTheReport_BeforeTheBackgroundReaderRuns()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);

        var envelope = await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data.ShouldNotBeNull();
        envelope.Data.Results.ShouldNotBeEmpty("the search must return its own results even though nothing has been flushed yet");

        var reportService = new MetricsReportService(_factory, new FakeTimeProvider(FixedNow));
        var report = await reportService.GetReportAsync("acme", [], TimeSpan.FromHours(1), TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        report.Series.Single(s => s.Tool == "search.fts").Count.ShouldBe(0,
            "the background reader has not run yet, so this search must not appear in the report");
    }

    /// <summary>
    ///     AC1: a memory_search results in ten series measurements (nine phases plus the measured
    ///     total) — enqueued synchronously (G4 forbids only the *table* write, never enqueueing) and
    ///     reaching the `metrics` table once flushed, each carrying the query hash and this call's
    ///     own correlation id, and no query text anywhere in the row (SqliteMetricsStore's save-time
    ///     allowlist).
    /// </summary>
    [Fact]
    public async Task Search_RecordsNineSeriesMeasurements_ReachingTheMetricsTable_TaggedWithHashAndCorrelationId_NeverQueryText()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);

        var envelope = await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);
        var correlationId = envelope.Meta.CorrelationId.ShouldNotBeNull();

        var flusher = new MetricsFlusher(_buffer, _metricsStore, new InMemorySettings(), new FakeTimeProvider(FixedNow),
            TestTelemetry.None, NullLogger<MetricsFlusher>.Instance);
        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<PhaseRow>(
            "SELECT name AS Name, query_hash AS QueryHash, correlation_id AS CorrelationId, tags AS Tags " +
            "FROM metrics WHERE name LIKE 'search.%'")).ToList();

        rows.Select(r => r.Name).ShouldBe(SearchTimings.SeriesNames, ignoreOrder: true);
        rows.ShouldAllBe(r => r.QueryHash == ContentHash.OfValue("chassis"));
        rows.ShouldAllBe(r => r.CorrelationId == correlationId);
        rows.ShouldAllBe(r => r.Tags == null, "no row may carry the query text anywhere, including Tags");
    }

    /// <summary>
    ///     Spec scenario 23: "two runs of the same query share a hash" — nothing today runs the same
    ///     query twice and inspects the stored rows. Two separate calls (two separate correlation
    ///     ids) must still land under one shared query_hash.
    /// </summary>
    [Fact]
    public async Task Search_CalledTwiceWithTheSameQuery_BothRunsShareTheSameQueryHash()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);

        var first = await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);
        var second = await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);
        var firstCorrelationId = first.Meta.CorrelationId.ShouldNotBeNull();
        var secondCorrelationId = second.Meta.CorrelationId.ShouldNotBeNull();
        firstCorrelationId.ShouldNotBe(secondCorrelationId, "two genuinely separate runs, not one call counted twice");

        var flusher = new MetricsFlusher(_buffer, _metricsStore, new InMemorySettings(), new FakeTimeProvider(FixedNow),
            TestTelemetry.None, NullLogger<MetricsFlusher>.Instance);
        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<PhaseRow>(
            "SELECT name AS Name, query_hash AS QueryHash, correlation_id AS CorrelationId, tags AS Tags " +
            "FROM metrics WHERE name = 'search.fts'")).ToList();

        rows.Count.ShouldBe(2, "one search.fts row per run");
        rows.Select(r => r.QueryHash).Distinct().ShouldHaveSingleItem("both runs of the same query must share one query hash");
        rows.Select(r => r.CorrelationId).ShouldBe([firstCorrelationId, secondCorrelationId], ignoreOrder: true);
    }

    /// <summary>
    ///     Spec scenario 17 / spec.json O2: "no setting turns measurement off" — deliberately weak,
    ///     as ruled: this proves one combination (every derived metrics setting at its most
    ///     restrictive functioning value) does not disable measurement, not that no setting can.
    ///     Settings are derived from <see cref="MetricsConfigKeys" />'s own *Global constants so a
    ///     new setting joins this scenario automatically instead of needing a hand-added line.
    /// </summary>
    [Fact]
    public async Task Search_EveryMetricsSettingAtItsMostRestrictiveValue_StillRecordsAMeasurement()
    {
        var settingKeys = typeof(MetricsConfigKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Global", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        settingKeys.ShouldNotBeEmpty("the derivation itself must find the settings — an empty list would make this scenario vacuous");

        var restrictiveValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetricsConfigKeys.BufferCapacityGlobal] = "1", // smallest functioning buffer
            [MetricsConfigKeys.FlushIntervalSecondsGlobal] = "999999", // effectively never flushes
            [MetricsConfigKeys.RetentionDaysGlobal] = "1" // shortest hot-table retention
        };
        settingKeys.ShouldBe([.. restrictiveValues.Keys], ignoreOrder: true,
            "every derived settings key must be pinned to a restrictive value here, or a new setting silently escapes this scenario");

        var restrictiveCapacity = MetricsConfigKeys.ParseBufferCapacity(restrictiveValues[MetricsConfigKeys.BufferCapacityGlobal]);
        var restrictiveBuffer = new MeasurementBuffer(restrictiveCapacity);
        var recorder = new MetricsRecorder(restrictiveBuffer, NullLogger<MetricsRecorder>.Instance);
        var toolsUnderRestrictiveSettings = BuildTools(recorder);

        await toolsUnderRestrictiveSettings.Write("acme", "the chassis pattern decision",
            cancellationToken: TestContext.Current.CancellationToken);

        await toolsUnderRestrictiveSettings.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);

        restrictiveBuffer.EnqueuedCount.ShouldBeGreaterThan(0, "no combination of restrictive settings may disable measurement");
        restrictiveBuffer.DroppedCount.ShouldBeGreaterThan(0,
            "capacity 1 against nine series measurements must actually bite, or the setting was not really restrictive");
    }

    /// <summary>
    ///     The save-time allowlist fails closed for exactly the row shape RecordPhaseMeasurements
    ///     emits: a row identified by QueryHash+CorrelationId (Tags null) is the only shape that
    ///     survives; the same row with the query text carried in Tags instead of properly hashed is
    ///     rejected. SqliteMetricsStore itself is WP3's landed writer (read-only here, not owned by
    ///     this package) — this proves this package's call site never needs to rely on anything
    ///     stronger than what it already does: pass the hash, never the text.
    /// </summary>
    [Fact]
    public async Task SaveBatch_QueryTextCarriedInTagsInsteadOfHashed_IsRejected_HashedRowIsAccepted()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);

        var hashedCorrelationId = Guid.CreateVersion7().ToString("N");
        var leakedCorrelationId = Guid.CreateVersion7().ToString("N");
        var hashed = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            "acme", ContentHash.OfValue("chassis"), hashedCorrelationId);
        var queryTextLeaked = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            "acme", ContentHash.OfValue("chassis"), leakedCorrelationId,
            Tags: JsonSerializer.Serialize(new { query = "chassis" }));

        await _metricsStore.SaveBatchAsync([hashed, queryTextLeaked], TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var savedCorrelationIds = await connection.QueryAsync<string>(
            "SELECT correlation_id FROM metrics WHERE name = 'search.fts' AND correlation_id IN (@Hashed, @Leaked)",
            new { Hashed = hashedCorrelationId, Leaked = leakedCorrelationId });

        savedCorrelationIds.ShouldBe([hashedCorrelationId],
            "the properly-hashed row is saved; the row carrying query text in Tags is rejected — the allowlist fails closed");
    }

    /// <summary>
    ///     Honest watch-red for G4 (review-fixes finding 10): the previous version of this test wrote
    ///     through <see cref="SqliteMetricsStore" /> directly, *after* <c>Search</c> had already
    ///     returned — proving only that <c>COUNT(*)</c> moves when a row is inserted, never that the
    ///     before/after technique <see cref="Search_MetricsRowCountIsUnchangedWhenTheCallReturns" />
    ///     relies on can actually catch the defect it exists to forbid. The original author left it
    ///     that way because the real search path
    ///     (<see cref="SqliteMemoryStore.SearchAsync" />/<see cref="MemoryTools.Search" />) was
    ///     another package's locked file. It is not locked now: this plugs a recorder that writes
    ///     straight through to the store — synchronously, on the caller's thread, exactly the
    ///     "MetricsRecorder.Record write through synchronously" defect shape — into that real call
    ///     chain (<c>MemoryTools.Search</c> → <c>RecordPhaseMeasurements</c> →
    ///     <c>IMeasurementRecorder.Record</c>), and shows the count really does move during the call.
    /// </summary>
    [Fact]
    public async Task WatchRed_ASynchronousRecorderPluggedIntoTheRealSearchPath_MovesTheCountDuringTheCall()
    {
        await _tools.Write("acme", "the chassis pattern decision", cancellationToken: TestContext.Current.CancellationToken);
        var toolsWithSynchronousRecorder = BuildTools(new SynchronousMetricsRecorder(_metricsStore));

        var before = await CountMetricsRowsAsync();

        await toolsWithSynchronousRecorder.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);

        var after = await CountMetricsRowsAsync();
        after.ShouldBeGreaterThan(before,
            "a recorder that writes through synchronously, plugged into the real search path, must move the count the gate checks");
    }

    private sealed record PhaseRow(string Name, string? QueryHash, string? CorrelationId, string? Tags);

    /// <summary>Simulates the exact defect G4 forbids: writes straight through to the store, blocking, on the caller's thread.</summary>
    private sealed class SynchronousMetricsRecorder(IMetricsStore store) : IMeasurementRecorder
    {
        public void Record(Measurement measurement) => store.SaveBatchAsync([measurement], CancellationToken.None).GetAwaiter().GetResult();
    }
}
