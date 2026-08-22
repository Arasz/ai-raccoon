using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SearchResults = AiRaccoon.Core.Memory.SearchResults;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The no-fusion-regression flag end to end (docs/adr/0078, issue #367). Default OFF: with the
///     setting absent or explicitly false the store must behave exactly as it did before this
///     existed, and record no fusion evidence at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreFusionFlagTests : IAsyncLifetime
{
    private const string Query = "quokka forage";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     Issue #367's mechanism, reproduced: the target is written before the bank's first-ever
    ///     configure, whose drain is a no-op (no prior engine to migrate from) — so it gets no
    ///     vec_entries row and never appears in the vector leg; a later engine CHANGE would instead
    ///     drain it bank-wide. It is rank 1 on FTS and unseen by the other leg, while three consensus
    ///     rows are retrieved by both.
    /// </summary>
    private async Task<string> SeedRegressionShapeAsync()
    {
        var target = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "quokka forage"), TestContext.Current.CancellationToken);

        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
            "openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        foreach (var text in new[]
                 {
                     "quokka forage habitat notes covering the island and its long dry season",
                     "quokka population notes covering the island and its long dry season",
                     "forage availability notes covering the island and its long dry season"
                 })
        {
            await _store.WriteAsync(new MemoryWriteRequest("acme", text), TestContext.Current.CancellationToken);
        }

        return target.Hash;
    }

    private Task<SearchResults> SearchAsync() => _store.SearchAsync(new SearchQuery("acme", Query), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Search_FlagAbsent_BuriesTheSingleLegWinner_AndRecordsNoFusionEvidence()
    {
        var target = await SeedRegressionShapeAsync();

        var results = await SearchAsync();

        // The precondition this whole record exists for: hybrid ranks it below its best single leg.
        results.Results.Select(r => r.Hash).ShouldContain(target);
        IndexOf(results, target).ShouldBeGreaterThan(0);
        results.Fusion.ShouldBeNull();
    }

    [Fact]
    public async Task Search_FlagExplicitlyFalse_MatchesTheFlagAbsentOrderAndScoresExactly()
    {
        await SeedRegressionShapeAsync();
        var baseline = await SearchAsync();

        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "false",
            TestContext.Current.CancellationToken);
        var withFalseFlag = await SearchAsync();

        withFalseFlag.Results.Select(r => r.Hash).ShouldBe(baseline.Results.Select(r => r.Hash));
        withFalseFlag.Results.Select(r => r.Ranking).ShouldBe(baseline.Results.Select(r => r.Ranking));
        withFalseFlag.Fusion.ShouldBeNull();
    }

    [Fact]
    public async Task Search_FlagEnabled_RanksTheSingleLegWinnerAtLeastAsWellAsItsBestLeg()
    {
        var target = await SeedRegressionShapeAsync();
        var baseline = await SearchAsync();
        var baselineIndex = IndexOf(baseline, target);

        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true",
            TestContext.Current.CancellationToken);
        var adjusted = await SearchAsync();

        IndexOf(adjusted, target).ShouldBeLessThan(baselineIndex);
    }

    /// <summary>
    ///     The recorded diff must describe the list actually served. On this shape the promoted
    ///     result lands second — the consensus row keeps rank 1, so top1_changed is 0 while
    ///     top5_moved is not: that separation is the whole reason both are recorded.
    /// </summary>
    [Fact]
    public async Task Search_FlagEnabled_RecordsTheDifferenceBetweenTheBaselineAndTheServedOrder()
    {
        await SeedRegressionShapeAsync();
        var baseline = await SearchAsync();
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true", TestContext.Current.CancellationToken);

        var adjusted = await SearchAsync();

        var observedMoved = Enumerable
            .Range(0, Math.Min(5, Math.Min(baseline.Results.Count, adjusted.Results.Count)))
            .Count(i => baseline.Results[i].Hash != adjusted.Results[i].Hash);
        var diff = adjusted.Fusion.ShouldNotBeNull();
        diff.Top5Moved.ShouldBe(observedMoved);
        diff.Top5Moved.ShouldBeGreaterThan(0);
        diff.Top1Changed.ShouldBe(baseline.Results[0].Hash == adjusted.Results[0].Hash ? 0 : 1);
        diff.Top1RankDelta.ShouldBe(
            adjusted.Results.Select(r => r.Hash).ToList().IndexOf(baseline.Results[0].Hash));
    }

    /// <summary>
    ///     No engine configured, so the vector leg is skipped entirely. A degraded leg is not a leg
    ///     that disagrees: the served order must be untouched and no fusion evidence recorded, or the
    ///     enabled path would report a reorder on searches that were never hybrid.
    /// </summary>
    [Fact]
    public async Task Search_FlagEnabled_ButOnlyTheKeywordLegRan_ChangesNothingAndRecordsNothing()
    {
        foreach (var text in new[] { "quokka forage", "quokka notes", "forage notes" })
        {
            await _store.WriteAsync(new MemoryWriteRequest("acme", text), TestContext.Current.CancellationToken);
        }

        var baseline = await SearchAsync();
        baseline.Results.ShouldNotBeEmpty();

        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true",
            TestContext.Current.CancellationToken);
        var enabled = await SearchAsync();

        enabled.Results.Select(r => r.Hash).ShouldBe(baseline.Results.Select(r => r.Hash));
        enabled.Results.Select(r => r.Ranking).ShouldBe(baseline.Results.Select(r => r.Ranking));
        enabled.Fusion.ShouldBeNull();
    }

    /// <summary>Distinct rankings all the way out, so no downstream Path tie-break decides the top hit.</summary>
    [Fact]
    public async Task Search_FlagEnabled_ServesDistinctRankings()
    {
        await SeedRegressionShapeAsync();
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true",
            TestContext.Current.CancellationToken);

        var adjusted = await SearchAsync();

        adjusted.Results.Select(r => r.Ranking).ShouldBeUnique();
    }

    private static int IndexOf(SearchResults results, string hash)
    {
        for (var index = 0; index < results.Results.Count; index++)
        {
            if (results.Results[index].Hash == hash)
            {
                return index;
            }
        }

        return -1;
    }
}
