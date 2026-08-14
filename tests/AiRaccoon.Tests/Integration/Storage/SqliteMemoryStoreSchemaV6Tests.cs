using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreSchemaV6Tests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-schema-v6");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreSchemaV6Tests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>
    ///     ADR-0039 restores noise_clusters/vec_noise to the fresh-bank DDL: the self-learning noise
    ///     subsystem now has a working store (SqliteNoiseClusterStore), gated OFF by default and not
    ///     wired to the write path (a research lane found a write-path learned-cluster filter scores
    ///     precision 0 on real traffic — see docs/adr/0039). noise_entries — the reject log ADR-0033
    ///     found nothing ever read — stays removed; it is not part of this reversal.
    /// </summary>
    [Fact]
    public async Task OpenBank_FreshBank_HasNoiseClusterTables_ButNotNoiseEntries()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var hasNoiseClusters = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_clusters'");
        hasNoiseClusters.ShouldBe(1L, "a fresh bank must create noise_clusters (ADR-0039 restores it)");

        var hasVecNoise = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='vec_noise'");
        hasVecNoise.ShouldBe(1L, "a fresh bank must create the vec_noise virtual table (ADR-0039 restores it)");

        var hasNoiseEntries = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_entries'");
        hasNoiseEntries.ShouldBe(0L, "noise_entries is not restored — nothing ever read it (ADR-0033) and that finding stands");

        var userVersion = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        userVersion.ShouldBe(MemorySchema.CurrentVersion, "PRAGMA user_version must match CurrentVersion");
    }
}
