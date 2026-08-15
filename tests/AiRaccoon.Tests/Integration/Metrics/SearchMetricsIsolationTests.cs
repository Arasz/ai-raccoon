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

namespace AiRaccoon.Tests.Integration.Metrics;

/// <summary>
///     WP3 AC4 and G4 (reshaped, ruling 2): the `metrics` row count must be unchanged when
///     memory_search returns, flusher paused. Calls the real MemoryTools.Search
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC4) — WP1's envelope
///     migration (IMemoryStore.SearchAsync now returns SearchResults, unpacked inline by
///     MemoryTools.Search) is merged as of task/perf-metrics; MemoryTools.Search's own public
///     signature was unchanged by that migration, so this test needed no update at merge.
///
///     WP8 has since wired IMeasurementRecorder into a real consumer — ToolTelemetry, the
///     CallToolFilter tool-level hook (ToolTelemetryMeasurementCoverageTests, G1). That hook sits
///     at the MCP protocol dispatch layer, which this test never reaches: it constructs
///     MemoryTools directly and calls Search() as a plain method, bypassing CallToolFilter
///     entirely — and MemoryTools's constructor takes no IMeasurementRecorder of its own. No
///     package wires the search-phase family (SearchTimings) into a recorder either. So this
///     assertion remains honestly vacuous by absence for the exact path it exercises — not because
///     the write is properly deferred, but because nothing reachable from this test's own call
///     path can write at all yet. The watch-red test below proves the counting mechanism itself
///     would catch the defect G4 exists to forbid, by simulating "a measurement recorded
///     synchronously inside the search window" at the WP3 boundary this package owns, since
///     editing SqliteMemoryStore.cs/MemoryTools.cs is out of scope for this package.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchMetricsIsolationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-search-metrics-isolation");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMetricsStore _metricsStore;
    private readonly MemoryTools _tools;

    public SearchMetricsIsolationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _metricsStore = new SqliteMetricsStore(_factory, NullLogger<SqliteMetricsStore>.Instance);

        // The flusher is deliberately never constructed here — "paused" for this test means it
        // does not exist, so nothing but the assertions themselves can touch the metrics table.
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, sourceStore,
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());
        _tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()),
            new NoOpSearchQualityService(), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(store, new FakePromotionQueue()), NullLogger<MemoryTools>.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

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
    ///     Watch-red for G4: proves the before/after count actually detects a synchronous write in
    ///     the window it brackets, rather than passing vacuously because nothing writes there yet.
    ///     Simulates the exact defect the gate exists to forbid — "write one measurement
    ///     synchronously inside the search path" — at this package's own boundary (SqliteMetricsStore),
    ///     since the real search path (SqliteMemoryStore.SearchAsync/MemoryTools.Search) is WP1's
    ///     locked file and out of scope here.
    /// </summary>
    [Fact]
    public async Task WatchRed_ASynchronousWriteDuringTheWindow_MovesTheCount()
    {
        var before = await CountMetricsRowsAsync();

        await _tools.Search("acme", "chassis", cancellationToken: TestContext.Current.CancellationToken);

        // Simulated defect: a synchronous metrics write "during" the search call.
        await _metricsStore.SaveBatchAsync(
            [new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow)],
            TestContext.Current.CancellationToken);

        var after = await CountMetricsRowsAsync();
        after.ShouldBeGreaterThan(before, "the gate must be able to fail: a synchronous write must move the count");
    }
}
