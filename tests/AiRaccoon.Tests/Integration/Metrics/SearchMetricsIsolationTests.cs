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
///     memory_search returns, flusher paused
///     (docs/plans/2026-08-15-performance-metrics-implementation.md).
///
///     WP10 wired <see cref="IMeasurementRecorder" /> into <c>MemoryTools.Search</c> itself — the
///     gap WP1-WP8 left open, recorded in this file's earlier revision. This test now constructs
///     the real chain (<see cref="MetricsRecorder" /> over a real <see cref="MeasurementBuffer" />,
///     deliberately with no <see cref="MetricsFlusher" /> running — "paused" means it is never
///     constructed), so a search's six phase measurements really do enqueue, and the assertion
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
        var recorder = new MetricsRecorder(_buffer, NullLogger<MetricsRecorder>.Instance);
        _tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()),
            new NoOpSearchQualityService(), new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(store, new FakePromotionQueue()), recorder, NullLogger<MemoryTools>.Instance);
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
    ///     AC1: a memory_search results in six phase measurements — enqueued synchronously (G4
    ///     forbids only the *table* write, never enqueueing) and reaching the `metrics` table once
    ///     flushed, each carrying the query hash and this call's own correlation id, and no query
    ///     text anywhere in the row (SqliteMetricsStore's save-time allowlist).
    /// </summary>
    [Fact]
    public async Task Search_RecordsSixPhaseMeasurements_ReachingTheMetricsTable_TaggedWithHashAndCorrelationId_NeverQueryText()
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

        rows.Select(r => r.Name).ShouldBe(SearchTimings.PhaseNames, ignoreOrder: true);
        rows.ShouldAllBe(r => r.QueryHash == ContentHash.OfValue("chassis"));
        rows.ShouldAllBe(r => r.CorrelationId == correlationId);
        rows.ShouldAllBe(r => r.Tags == null, "no row may carry the query text anywhere, including Tags");
    }

    private sealed record PhaseRow(string Name, string? QueryHash, string? CorrelationId, string? Tags);

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
            Tags: System.Text.Json.JsonSerializer.Serialize(new { query = "chassis" }));

        await _metricsStore.SaveBatchAsync([hashed, queryTextLeaked], TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var savedCorrelationIds = await connection.QueryAsync<string>(
            "SELECT correlation_id FROM metrics WHERE name = 'search.fts' AND correlation_id IN (@Hashed, @Leaked)",
            new { Hashed = hashedCorrelationId, Leaked = leakedCorrelationId });

        savedCorrelationIds.ShouldBe([hashedCorrelationId],
            "the properly-hashed row is saved; the row carrying query text in Tags is rejected — the allowlist fails closed");
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
