using System.Data;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    private SqliteConnectionFactory Factory(InstallScope scope = InstallScope.User) => new(
        new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = scope },
        loadExtensions: _ => { });

    [Fact]
    public void BankPath_UserScope_IsDataRootMemoryDb()
    {
        Factory().BankPath.ShouldBe(Path.Combine(_dataRoot, "memory.db"));
    }

    [Fact]
    public void BankPath_ProjectScope_IsDataRootAiRaccoonMemoryDb()
    {
        Factory(InstallScope.Project).BankPath.ShouldBe(Path.Combine(_dataRoot, ".ai-raccoon", "memory.db"));
    }

    [Fact]
    public void MetaDatabasePath_UserScope_IsDataRootRaccoonMetaDb()
    {
        Factory().MetaDatabasePath.ShouldBe(Path.Combine(_dataRoot, "raccoon_meta.db"));
    }

    [Fact]
    public void MetaDatabasePath_ProjectScope_IsDataRootAiRaccoonRaccoonMetaDb()
    {
        Factory(InstallScope.Project).MetaDatabasePath.ShouldBe(Path.Combine(_dataRoot, ".ai-raccoon", "raccoon_meta.db"));
    }

    [Fact]
    public async Task OpenBankAsync_CreatesDatabaseAtBankPath()
    {
        var factory = Factory();

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        File.Exists(factory.BankPath).ShouldBeTrue();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenBankAsync_AppliesWalAndBusyTimeoutPragmas()
    {
        var factory = Factory();

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await QueryStringAsync(connection, "PRAGMA journal_mode")).ShouldBe("wal");
        (await QueryIntAsync(connection, "PRAGMA busy_timeout")).ShouldBe(5000);
    }

    [Fact]
    public async Task OpenMetaAsync_OpensRaccoonMetaDatabase_WithoutLoadingExtensions()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => throw new InvalidOperationException("extensions must not load"));

        await using var connection = await factory.OpenMetaAsync(TestContext.Current.CancellationToken);

        File.Exists(factory.MetaDatabasePath).ShouldBeTrue();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public void DefaultExtensionPaths_FollowDataRootExtensionsRidLayout()
    {
        var paths = ExtensionPaths.For(_dataRoot, "osx-arm64", includeCloudSync: true);

        paths.Vector.ShouldBe(Path.Combine(_dataRoot, "extensions", "osx-arm64", "vector.dylib"));
        paths.Memory.ShouldBe(Path.Combine(_dataRoot, "extensions", "osx-arm64", "memory.dylib"));
        paths.CloudSync.ShouldBe(Path.Combine(_dataRoot, "extensions", "osx-arm64", "cloudsync.dylib"));
    }

    [Fact]
    public void DefaultExtensionPaths_WithoutCloudSync_OmitsSyncModule()
    {
        var paths = ExtensionPaths.For(_dataRoot, "osx-arm64", includeCloudSync: false);

        paths.CloudSync.ShouldBeNull();
    }

    [Fact]
    public void DefaultExtensionPaths_UsePlatformModuleSuffixes()
    {
        ExtensionPaths.For(_dataRoot, "linux-x64", includeCloudSync: false).Vector.ShouldEndWith("vector.so");
        ExtensionPaths.For(_dataRoot, "win-x64", includeCloudSync: false).Vector.ShouldEndWith("vector.dll");
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> QueryStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> QueryIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
