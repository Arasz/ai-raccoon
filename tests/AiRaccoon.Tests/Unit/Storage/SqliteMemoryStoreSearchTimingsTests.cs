using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     WP2 (docs/plans/2026-08-15-performance-metrics-implementation.md) plus S1
///     (docs/plans/2026-08-17-search-phase-attribution.md): <c>SearchAsync</c>'s eight phase timings
///     and its measured total, previously <c>SearchTimings.Empty</c>. <c>fts</c>/<c>vector</c>
///     accumulate across the per-context loop (and the FTS fallback re-query); the rest are single
///     spans, and <c>Total</c> brackets the whole method body.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreSearchTimingsTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-search-timings");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreSearchTimingsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Search_PopulatesEveryPhaseThatRan_AndLeavesTheSkippedModalityAtZero()
    {
        var store = SearchTimingsHarness.CreateStore(_factory,
            new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets", VectorWeight: 0), ct);

        result.Timings.Open.ShouldBeGreaterThan(TimeSpan.Zero, "every search opens a bank connection");
        result.Timings.Embed.ShouldBeGreaterThan(TimeSpan.Zero, "every search calls EmbedQueryAsync");
        result.Timings.Fts.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Vector.ShouldBe(TimeSpan.Zero, "VectorWeight is 0, so the vector modality never queries");
        result.Timings.Fusion.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Affinity.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Snippets.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Bump.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Total.ShouldBeGreaterThan(TimeSpan.Zero, "Total brackets the whole SearchAsync body");
    }

    [Fact]
    public async Task Search_FtsStaysZero_WhenFtsWeightIsZero()
    {
        var store = SearchTimingsHarness.CreateStore(_factory,
            new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets", FtsWeight: 0, VectorWeight: 0), ct);

        result.Timings.Fts.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Search_FtsAccumulatesAcrossContexts_IncludingTheFallbackReQuery()
    {
        var store = SearchTimingsHarness.CreateStore(_factory,
            new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
        var ct = TestContext.Current.CancellationToken;

        // Two tokens, no matches anywhere: FtsQueryNormalizer.BuildPlan gives a non-null Fallback,
        // and zero primary hits is always <= the fallback threshold, so the re-query always fires.
        var singleContext = await store.SearchAsync(
            new SearchQuery("proj-1", "alpha bravo", Scope: SearchScope.Project), ct);
        var multiContext = await store.SearchAsync(
            new SearchQuery("proj-1", "alpha bravo", Scope: SearchScope.All), ct);

        singleContext.Timings.Fts.ShouldBeGreaterThan(TimeSpan.Zero,
            "the fallback re-query must be counted even for a single context");
        multiContext.Timings.Fts.ShouldBeGreaterThan(singleContext.Timings.Fts,
            "fts must sum across every context in scope (shared + project here), not just the last one queried");
    }

    [Fact]
    public async Task Search_AttributesEachPhasesElapsedTime_ToThatPhaseAndNoOther()
    {
        // A distinct elapsed value per phase, in the exact order SearchAsync measures them: open,
        // embed, fts, vector, fusion, affinity, snippets, bump — every gap between brackets (and the
        // gap before Total's own closing read) scripted at zero, so Total's nesting around every
        // phase does not perturb any individual phase's own value. A single-token query scoped to
        // the project has exactly one context and no FTS fallback (FtsQueryNormalizer gives
        // Fallback = null for a one-token query), so each phase brackets exactly once.
        var timeProvider = ScriptedTimeProvider.ForPhases(
            TimeSpan.FromMilliseconds(10), // open
            TimeSpan.FromMilliseconds(11), // embed
            TimeSpan.FromMilliseconds(22), // fts
            TimeSpan.FromMilliseconds(33), // vector
            TimeSpan.FromMilliseconds(44), // fusion
            TimeSpan.FromMilliseconds(55), // affinity
            TimeSpan.FromMilliseconds(66), // snippets
            TimeSpan.FromMilliseconds(77) // bump
        );
        var store = SearchTimingsHarness.CreateStore(_factory, timeProvider, new SearchTimingsHarness.VectorEmbedderStub());
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widget", Scope: SearchScope.Project), ct);

        result.Timings.Open.ShouldBe(TimeSpan.FromMilliseconds(10));
        result.Timings.Embed.ShouldBe(TimeSpan.FromMilliseconds(11));
        result.Timings.Fts.ShouldBe(TimeSpan.FromMilliseconds(22));
        result.Timings.Vector.ShouldBe(TimeSpan.FromMilliseconds(33));
        result.Timings.Fusion.ShouldBe(TimeSpan.FromMilliseconds(44));
        result.Timings.Affinity.ShouldBe(TimeSpan.FromMilliseconds(55));
        result.Timings.Snippets.ShouldBe(TimeSpan.FromMilliseconds(66));
        result.Timings.Bump.ShouldBe(TimeSpan.FromMilliseconds(77));
    }

    /// <summary>
    ///     Returns a monotonic cursor from <c>GetTimestamp</c>, advancing it by the next scripted
    ///     delta after every call. <see cref="TimeProvider.GetElapsedTime(long)" /> calls
    ///     <c>GetTimestamp()</c> internally, so a simple bracket's elapsed is exactly the delta
    ///     consumed between its own start and end calls — and because <c>Total</c> nests around
    ///     every phase (its own start is the first call, its own end is the last), the same
    ///     mechanism sums correctly for it too, unlike an alternating start/end toggle which
    ///     desynchronises the moment a bracket nests inside another one.
    /// </summary>
    private sealed class ScriptedTimeProvider(IReadOnlyList<TimeSpan> deltas) : TimeProvider
    {
        private int _index;
        private long _cursor;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var value = _cursor;
            if (_index < deltas.Count)
            {
                _cursor += deltas[_index++].Ticks;
            }

            return value;
        }

        /// <summary>
        ///     Builds the interleaved delta sequence <c>SearchAsync</c>'s call order needs for a
        ///     single-context, no-fallback search: a zero gap before every phase's own start/end
        ///     pair (the untimed work between brackets), then that phase's scripted value, for each
        ///     of the eight phases in measurement order, plus a trailing zero gap before Total's
        ///     own closing read.
        /// </summary>
        public static ScriptedTimeProvider ForPhases(params TimeSpan[] phaseValues)
        {
            var deltas = new List<TimeSpan>();
            foreach (var value in phaseValues)
            {
                deltas.Add(TimeSpan.Zero);
                deltas.Add(value);
            }

            deltas.Add(TimeSpan.Zero);
            return new ScriptedTimeProvider(deltas);
        }
    }
}
