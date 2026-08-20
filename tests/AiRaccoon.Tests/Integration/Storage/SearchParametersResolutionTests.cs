using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchParametersResolutionTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public SearchParametersResolutionTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task GetSearchParameterDefaults_WithNoSettingsRows_ReturnsAllNull()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var defaults = await _store.GetSearchParameterDefaultsAsync(connection, TestContext.Current.CancellationToken);

        defaults.RrfK.ShouldBeNull();
        defaults.FtsWeight.ShouldBeNull();
        defaults.VectorWeight.ShouldBeNull();
        defaults.SourceLambda.ShouldBeNull();
        defaults.ConsolidationThreshold.ShouldBeNull();
        defaults.DocScoreFormula.ShouldBeNull();
        defaults.CandidateWindow.ShouldBeNull();
        defaults.StructureAlpha.ShouldBeNull();
        defaults.FusionNoRegressionEnabled.ShouldBeNull();
    }

    [Fact]
    public async Task GetSearchParameterDefaults_WithSettingsRows_ParsesEachOption()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(SearchParameterSettingsKeys.RrfK, "30", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.FtsWeight, "0", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.VectorWeight, "2", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.SourceLambda, "0.3", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.ConsolidationThreshold, "0.05", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.DocScoreFormula, "sum", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.CandidateWindow, "max5x50", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha, "0.8", ct);
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true", ct);

        await using var connection = await _factory.OpenBankAsync(ct);
        var defaults = await _store.GetSearchParameterDefaultsAsync(connection, ct);

        defaults.RrfK.ShouldBe(30);
        defaults.FtsWeight.ShouldBe(0);
        defaults.VectorWeight.ShouldBe(2);
        defaults.SourceLambda.ShouldBe(0.3);
        defaults.ConsolidationThreshold.ShouldBe(0.05);
        defaults.DocScoreFormula.ShouldBe(Core.Memory.DocScoreFormula.Sum);
        defaults.CandidateWindow.ShouldBe(CandidateWindowMode.Max5X50);
        defaults.StructureAlpha.ShouldBe(0.8);
        defaults.FusionNoRegressionEnabled.ShouldBe(true);
    }

    [Fact]
    public async Task GetSearchParameterDefaults_WithMalformedValues_ReturnsNullForThose()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(SearchParameterSettingsKeys.SourceLambda, "abc", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.RrfK, "-5", ct);
        await _store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha, "1.5", ct);
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "banana", ct);

        await using var connection = await _factory.OpenBankAsync(ct);
        var defaults = await _store.GetSearchParameterDefaultsAsync(connection, ct);

        defaults.SourceLambda.ShouldBeNull();
        defaults.RrfK.ShouldBeNull();
        defaults.StructureAlpha.ShouldBeNull();
        defaults.FusionNoRegressionEnabled.ShouldBeNull();

        // Malformed settings fall back to the canonical constants, never crash a search.
        var resolved = SearchParameters.FromSources(new SearchQuery("acme", "x"), defaults);
        resolved.SourceLambda.ShouldBe(SearchParameterSettingsKeys.DefaultSourceLambda);
        resolved.RrfK.ShouldBe(SearchParameterSettingsKeys.DefaultRrfK);
        resolved.StructureAlpha.ShouldBe(SearchParameterSettingsKeys.DefaultStructureAlpha);
        resolved.FusionNoRegressionEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Search_WithEmptySettingsBank_UsesCanonicalConstants()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "constants path still retrieves"), ct);

        var results = (await _store.SearchAsync(new SearchQuery("acme", "constants", VectorWeight: 0), ct)).Results;

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Ranking.ShouldBe(1.0);
    }

    [Fact]
    public async Task Search_QueryValuesOverrideSettings_Behaviorally()
    {
        var ct = TestContext.Current.CancellationToken;
        // The bank setting disables the FTS leg entirely...
        await _store.SetSettingAsync(SearchParameterSettingsKeys.FtsWeight, "0", ct);
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "zephyr keyword retrieval"), ct);

        // ...so a query that stays silent on weights finds nothing (vector leg also off).
        var silent = (await _store.SearchAsync(new SearchQuery("acme", "zephyr", VectorWeight: 0), ct)).Results;
        silent.ShouldBeEmpty();

        // A query that provides ftsWeight overrides the setting and the leg runs again.
        var overridden = (await _store.SearchAsync(
            new SearchQuery("acme", "zephyr", VectorWeight: 0, FtsWeight: 1), ct)).Results;
        overridden.ShouldContain(result => result.Hash == entry.Hash);
    }

    [Fact]
    public async Task Search_WithFusionFlagEnabled_SingleLegSearchStillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true", ct);
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "single leg with the flag on"), ct);

        // The flag's read is eager; its APPLICATION stays conditional on two contributing
        // legs (docs/adr/0078) — a single-leg search must behave exactly like a plain merge.
        var results = (await _store.SearchAsync(new SearchQuery("acme", "single", VectorWeight: 0), ct)).Results;

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Ranking.ShouldBe(1.0);
    }
}
