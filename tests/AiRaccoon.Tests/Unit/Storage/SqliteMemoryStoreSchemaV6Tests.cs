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
    ///     ADR-0033: a fresh bank no longer gets noise_clusters/vec_noise — the zero-shot noise
    ///     filter and the noise-learning subsystem that wrote them are gone. MigrateToV6Async
    ///     (the ladder step that still creates them on a legacy bank upgrading from &lt;v6) stays in
    ///     place as a historical no-op and is never renumbered; a fresh bank skips the ladder
    ///     entirely and jumps straight to CurrentVersion.
    /// </summary>
    [Fact]
    public async Task OpenBank_FreshBank_HasNoNoiseTables_ButStampsCurrentVersion()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var hasNoiseClusters = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_clusters'");
        hasNoiseClusters.ShouldBe(0L, "a fresh bank must not create the removed noise_clusters table (ADR-0033)");

        var hasVecNoise = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='vec_noise'");
        hasVecNoise.ShouldBe(0L, "a fresh bank must not create the removed vec_noise virtual table (ADR-0033)");

        var hasNoiseEntries = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_entries'");
        hasNoiseEntries.ShouldBe(0L, "a fresh bank must not create the removed noise_entries table (ADR-0033)");

        var userVersion = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        userVersion.ShouldBe(MemorySchema.CurrentVersion, "PRAGMA user_version must match CurrentVersion");
    }
}
