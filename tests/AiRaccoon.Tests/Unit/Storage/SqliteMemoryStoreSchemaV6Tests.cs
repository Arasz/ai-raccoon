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

    [Fact]
    public async Task OpenBank_CreatesV6NoiseClustersAndVecNoiseTables()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var hasNoiseClusters = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='noise_clusters'");
        hasNoiseClusters.ShouldBe(1L, "the bank must create noise_clusters table on open");

        var hasVecNoise = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='vec_noise'");
        hasVecNoise.ShouldBe(1L, "the bank must create vec_noise virtual vector table on open");

        var userVersion = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        userVersion.ShouldBe(MemorySchema.CurrentVersion, "PRAGMA user_version must match CurrentVersion");
    }
}
