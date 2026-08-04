using System.Data;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private SqliteConnectionFactory Factory(InstallScope scope = InstallScope.User) =>
        new(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = scope },
            new NullKeyProvider());

    [Fact]
    public void BankPath_UserScope_IsDataRootMemoryDb() => Factory().BankPath.ShouldBe(Path.Combine(_dataRoot, "memory.db"));

    [Fact]
    public void BankPath_ProjectScope_IsDataRootAiRaccoonMemoryDb() => Factory(InstallScope.Project).BankPath.ShouldBe(Path.Combine(_dataRoot, ".ai-raccoon", "memory.db"));

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
    public async Task OpenBankAsync_InitializesOurSchema_OnFirstOpen()
    {
        var factory = Factory();

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var tables = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY name"))
            .ToList();
        tables.ShouldContain("entries");
        tables.ShouldContain("workspaces");
        tables.ShouldContain("settings");
        tables.ShouldContain("entries_fts");
        tables.ShouldContain("vec_entries");
    }

    [Fact]
    public async Task OpenBankAsync_CreatesOnlyMemoryDb_InTheBankDirectory()
    {
        var factory = Factory();

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var dbFiles = Directory.EnumerateFiles(_dataRoot, "*.db").Select(Path.GetFileName).ToList();
        dbFiles.ShouldBe(["memory.db"]);
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
