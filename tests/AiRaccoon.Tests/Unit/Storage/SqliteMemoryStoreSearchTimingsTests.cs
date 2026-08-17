using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     WP2 (docs/plans/2026-08-15-performance-metrics-implementation.md): <c>SearchAsync</c>'s six
///     phase timings, previously <c>SearchTimings.Empty</c>. <c>fts</c>/<c>vector</c> accumulate
///     across the per-context loop (and the FTS fallback re-query); the rest are single spans.
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

    private SqliteMemoryStore CreateStore(TimeProvider timeProvider, IEntryEmbedder? embedder = null)
    {
        embedder ??= new EntryEmbedder(TestData.CreateEmbeddingService());
        return new SqliteMemoryStore(_factory, new SqliteMemorySourceStore(_factory),
            new FileIngestor(new FileTypeMatcher([]), embedder, new SqliteMemorySourceStore(_factory), timeProvider,
                new LocalTokenizer()),
            embedder, timeProvider, NullLogger<SqliteMemoryStore>.Instance,
            new NoiseFilteringService([]), new SqliteSettingsStore(_factory));
    }

    [Fact]
    public async Task Search_PopulatesEveryPhaseThatRan_AndLeavesTheSkippedModalityAtZero()
    {
        var store = CreateStore(new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets", VectorWeight: 0), ct);

        result.Timings.Fts.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Vector.ShouldBe(TimeSpan.Zero, "VectorWeight is 0, so the vector modality never queries");
        result.Timings.Fusion.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Affinity.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Snippets.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Timings.Bump.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Search_FtsStaysZero_WhenFtsWeightIsZero()
    {
        var store = CreateStore(new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets", FtsWeight: 0, VectorWeight: 0), ct);

        result.Timings.Fts.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Search_FtsAccumulatesAcrossContexts_IncludingTheFallbackReQuery()
    {
        var store = CreateStore(new FakeTimeProvider(FixedNow) { AutoAdvanceAmount = TimeSpan.FromTicks(1) });
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
        // A distinct elapsed value per phase, in the exact order SearchAsync measures them: fts,
        // vector, fusion, affinity, snippets, bump. A single-token query scoped to the project has
        // exactly one context and no FTS fallback (FtsQueryNormalizer gives Fallback = null for a
        // one-token query), so each phase brackets exactly once.
        var timeProvider = new ScriptedTimeProvider(
        [
            TimeSpan.FromMilliseconds(11), // fts
            TimeSpan.FromMilliseconds(22), // vector
            TimeSpan.FromMilliseconds(33), // fusion
            TimeSpan.FromMilliseconds(44), // affinity
            TimeSpan.FromMilliseconds(55), // snippets
            TimeSpan.FromMilliseconds(66) // bump
        ]);
        var store = CreateStore(timeProvider, new FixedVectorEmbedder());
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widget", Scope: SearchScope.Project), ct);

        result.Timings.Fts.ShouldBe(TimeSpan.FromMilliseconds(11));
        result.Timings.Vector.ShouldBe(TimeSpan.FromMilliseconds(22));
        result.Timings.Fusion.ShouldBe(TimeSpan.FromMilliseconds(33));
        result.Timings.Affinity.ShouldBe(TimeSpan.FromMilliseconds(44));
        result.Timings.Snippets.ShouldBe(TimeSpan.FromMilliseconds(55));
        result.Timings.Bump.ShouldBe(TimeSpan.FromMilliseconds(66));
    }

    /// <summary>
    ///     Returns one scripted elapsed <see cref="TimeSpan" /> per <c>GetTimestamp</c> +
    ///     <c>GetElapsedTime</c> bracket, in call order. A real clock cannot pin a specific delay to
    ///     a specific phase deterministically, and <see cref="FakeTimeProvider" />'s elapsed time is
    ///     always zero unless the fake clock is advanced between the two calls — this advances it by
    ///     exactly the next scripted amount, once per bracket, so a swapped phase assignment shows up
    ///     as a swapped assertion failure instead of vanishing into noise.
    /// </summary>
    private sealed class ScriptedTimeProvider(IReadOnlyList<TimeSpan> perBracket) : TimeProvider
    {
        private int _bracket;
        private long _cursor;
        private bool _pendingElapsed;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (!_pendingElapsed)
            {
                _pendingElapsed = true;
                return _cursor;
            }

            _pendingElapsed = false;
            var delta = _bracket < perBracket.Count ? perBracket[_bracket++] : TimeSpan.Zero;
            _cursor += delta.Ticks;
            return _cursor;
        }
    }

    /// <summary>A fixed 384-float vector, so the vector modality actually queries without a live embedding endpoint.</summary>
    private sealed class FixedVectorEmbedder : IEntryEmbedder
    {
        public Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
            string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<byte[]?> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => Task.FromResult<byte[]?>(EmbeddingBlob.ToBytes(new float[384]));

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");
    }
}
