using System.Data;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private SqliteConnectionFactory Factory(InstallScope scope = InstallScope.User) =>
        new(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = scope },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = scope }));

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

    /// <summary>
    ///     A post-open SqliteException (schema DDL here — a stand-in for SQLITE_BUSY, which
    ///     cannot be triggered deterministically in-process) must never be diagnosed as a key
    ///     mismatch: only the actual open step proves whether the key is wrong.
    /// </summary>
    [Fact]
    public async Task OpenBankWithResolvedKeyAsync_PostOpenSchemaFailure_PropagatesSqliteExceptionUnwrapped()
    {
        var factory = Factory();
        SeedBankWithSchemaConflict(factory.BankPath);
        var resolvedKey = new ResolvedKey(null, "test") { LegacyPassphrase = "unused-legacy-key" };

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await factory.OpenBankWithResolvedKeyAsync(resolvedKey, TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldNotContain("restore it from a backup");
    }

    /// <summary>A failed post-open step (extensions, vector load, schema DDL) must dispose the
    /// connection it was given rather than leak it, open, into the pool.</summary>
    [Fact]
    public async Task InitializeAsync_PostOpenStepThrows_DisposesTheConnection()
    {
        var bankPath = Path.Combine(_dataRoot, "post-open-failure.db");
        SeedBankWithSchemaConflict(bankPath);

        var connection = new SqliteConnection($"Data Source={bankPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SqliteException>(async () =>
            await SqliteConnectionFactory.InitializeAsync(connection, TestContext.Current.CancellationToken));

        connection.State.ShouldBe(ConnectionState.Closed);
    }

    /// <summary>Pre-creates an "entries" table missing the columns MemorySchema's DDL indexes
    /// (scope, project_id) — CREATE TABLE IF NOT EXISTS then skips it, and the later CREATE
    /// INDEX fails deterministically, giving a non-key SqliteException from schema DDL.</summary>
    private static void SeedBankWithSchemaConflict(string bankPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bankPath)!);
        using var seed = new SqliteConnection($"Data Source={bankPath}");
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = "CREATE TABLE entries (id INTEGER PRIMARY KEY, hash TEXT, value TEXT)";
        cmd.ExecuteNonQuery();
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("airaccoon-store-tests");

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
