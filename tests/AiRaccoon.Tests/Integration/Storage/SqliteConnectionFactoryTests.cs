using System.Data;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

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

    /// <summary>
    ///     Watch-overlap-prune log channel (docs/work/2026-08-21-code-search-implementation-plan.md
    ///     §4, review arch-8, S7 orchestrator ruling): the unconditional prune step itself is silent
    ///     (returns pruned + warnings); InitializeAsync — the one caller with a logger — reports it.
    ///     Asserts the actual EventId-901/902 message shapes, not a loose substring: a `Contains('1')`
    ///     check would also match an unrelated digit anywhere in any log line and prove nothing.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_BankWithNestedWatches_LogsOnePrunedLinePerWatch_AndTheCount()
    {
        var bankPath = Path.Combine(_dataRoot, "watch-overlap-migration.db");
        SeedBankWithNestedWatches(bankPath);
        var logger = new FakeLogger<SqliteConnectionFactory>();

        var connection = new SqliteConnection($"Data Source={bankPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var opened = await SqliteConnectionFactory.InitializeAsync(connection,
            TestContext.Current.CancellationToken, logger);

        var remaining = (await connection.QueryAsync<string>(
                new CommandDefinition("SELECT path FROM watches WHERE project_id = 'acme' ORDER BY path",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToArray();
        remaining.ShouldBe(["/repo"]);

        var records = logger.Collector.GetSnapshot();
        // EventId 901's exact message format: "watch overlap migration: removed {Path} (covered by {CoveredBy})".
        records.ShouldContain(r => r.Message == "watch overlap migration: removed /repo/src (covered by /repo)" &&
                                    r.Level == LogLevel.Information);
        // EventId 902's exact message format: "watch overlap migration: removed {Count} overlapping watch(es)".
        records.ShouldContain(r => r.Message == "watch overlap migration: removed 1 overlapping watch(es)" &&
                                    r.Level == LogLevel.Information);
    }

    /// <summary>A bank (stamped user_version = CurrentVersion) with a watch nested inside another —
    /// the unconditional prune step must still reach it even though the bank is already current.</summary>
    private static void SeedBankWithNestedWatches(string bankPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bankPath)!);
        using var seed = new SqliteConnection($"Data Source={bankPath}");
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = """
                           CREATE TABLE watches (
                               project_id            TEXT NOT NULL,
                               path                   TEXT NOT NULL,
                               created_at             INTEGER NOT NULL,
                               last_change_ts         INTEGER NOT NULL,
                               scan_owner             TEXT NULL,
                               scan_lease_expires_at  INTEGER NOT NULL DEFAULT 0,
                               PRIMARY KEY (project_id, path)
                           );
                           CREATE TABLE watch_files (
                               project_id TEXT NOT NULL,
                               path       TEXT NOT NULL,
                               file_hash  TEXT NOT NULL,
                               updated_at INTEGER NOT NULL,
                               PRIMARY KEY (project_id, path)
                           );
                           INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES ('acme', '/repo', 1, 0);
                           INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES ('acme', '/repo/src', 2, 0);
                           PRAGMA user_version = 10;
                           """;
        cmd.ExecuteNonQuery();
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

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

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
