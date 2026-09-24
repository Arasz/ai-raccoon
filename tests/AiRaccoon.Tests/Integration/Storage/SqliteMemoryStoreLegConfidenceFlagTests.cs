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
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Confidence-weighted RRF's setting end to end (issue #706). Default OFF: with the setting
///     absent or explicitly false the store must behave exactly as it did before this existed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreLegConfidenceFlagTests : IAsyncLifetime
{
    private const string Query = "quokka forage";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    private async Task SeedAsync()
    {
        foreach (var text in new[] { "quokka forage", "quokka notes", "forage notes", "unrelated entry" })
        {
            await _store.WriteAsync(new MemoryWriteRequest("acme", text), TestContext.Current.CancellationToken);
        }
    }

    private Task<SearchResults> SearchAsync() =>
        _store.SearchAsync(new SearchQuery("acme", Query, VectorWeight: 0), TestContext.Current.CancellationToken);

    [RetryFact]
    public async Task Search_FlagAbsent_MatchesFlagExplicitlyFalse_ByteIdenticalOrderAndScores()
    {
        await SeedAsync();
        var absent = await SearchAsync();

        await _store.SetSettingAsync(FusionConfigKeys.LegConfidenceEnabledGlobal, "false",
            TestContext.Current.CancellationToken);
        var explicitlyFalse = await SearchAsync();

        explicitlyFalse.Results.Select(r => r.Hash).ShouldBe(absent.Results.Select(r => r.Hash));
        explicitlyFalse.Results.Select(r => r.Ranking).ShouldBe(absent.Results.Select(r => r.Ranking));
    }

    /// <summary>
    ///     A single contributing leg's RRF scores all get the SAME multiplier (the leg's own
    ///     rank-1..rank-k separation), so the max-normalization in
    ///     <see cref="Fusion.ReciprocalRankFusion.FuseWithEvidence" /> divides it straight back
    ///     out: order and served Ranking must come out byte-identical whether the flag is on or
    ///     off. Uses VectorWeight: 0 so only the FTS leg contributes — no embedding engine needed.
    /// </summary>
    [RetryFact]
    public async Task Search_FlagEnabled_SingleContributingLeg_MatchesBaselineExactly()
    {
        await SeedAsync();
        var baseline = await SearchAsync();
        baseline.Results.ShouldNotBeEmpty();

        await _store.SetSettingAsync(FusionConfigKeys.LegConfidenceEnabledGlobal, "true",
            TestContext.Current.CancellationToken);
        var withFlagOn = await SearchAsync();

        withFlagOn.Results.Select(r => r.Hash).ShouldBe(baseline.Results.Select(r => r.Hash));
        withFlagOn.Results.Select(r => r.Ranking).ShouldBe(baseline.Results.Select(r => r.Ranking));
    }
}
