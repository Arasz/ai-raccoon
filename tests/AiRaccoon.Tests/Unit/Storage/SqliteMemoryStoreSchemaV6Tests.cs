using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

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
    ///     Amended (this task): noise_clusters/vec_noise (ADR-0039's centroid-clustering store) are
    ///     dead by evidence — leader-follower measured 93% singleton clusters on real traffic, and
    ///     noise silhouette (0.047-0.142) is no better than signal (0.045-0.075). Removed from the
    ///     fresh-bank DDL only; MigrateToV6Async stays a historical no-op ladder step so a legacy bank
    ///     upgrading from &lt;v6 still gets them (see the migration test below). noise_entries
    ///     (ADR-0029's original write-time reject log) is restored: it is now the training-data
    ///     source shadow mode and a future 200-item labelled set depend on.
    /// </summary>
    [Fact]
    public async Task OpenBank_FreshBank_HasNoiseEntries_ButNotNoiseClusterTables()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var hasNoiseEntries = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_entries'");
        hasNoiseEntries.ShouldBe(1L, "a fresh bank must create noise_entries — it is the noise-learning training-data source (ADR-0029/ADR-0039)");

        var hasNoiseClusters = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_clusters'");
        hasNoiseClusters.ShouldBe(0L, "noise_clusters is dead by evidence (93% singleton clusters on real traffic) and must not be created on a fresh bank");

        var hasVecNoise = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='vec_noise'");
        hasVecNoise.ShouldBe(0L, "vec_noise served only the dead clustering store and must not be created on a fresh bank");

        var userVersion = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        userVersion.ShouldBe(MemorySchema.CurrentVersion, "PRAGMA user_version must match CurrentVersion");
    }

    /// <summary>
    ///     MigrateToV6Async is left untouched as a historical no-op ladder step (never renumbered or
    ///     deleted, per ADR-0011): a bank that was already stamped &lt;v6 before this change still
    ///     receives noise_clusters/vec_noise on upgrade, inert, alongside the newly-restored
    ///     noise_entries (which the unconditional Ddl adds regardless of stamped version).
    /// </summary>
    [Fact]
    public async Task OpenBank_LegacyPreV6Bank_StillGetsNoiseClusterTables_ViaTheHistoricalLadderStep()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "DROP TABLE IF EXISTS noise_clusters; DROP TABLE IF EXISTS vec_noise; PRAGMA user_version = 5");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var hasNoiseClusters = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_clusters'");
        hasNoiseClusters.ShouldBe(1L, "MigrateToV6Async must still run for a bank stamped <v6, inert but present");

        var hasVecNoise = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='vec_noise'");
        hasVecNoise.ShouldBe(1L);

        var userVersion = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        userVersion.ShouldBe(MemorySchema.CurrentVersion, "the bank must still reach CurrentVersion after the ladder runs");
    }
}
