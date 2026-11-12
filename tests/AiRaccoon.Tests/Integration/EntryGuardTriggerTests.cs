using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     v24: the bank itself holds "only memory rows carry a TTL" and "entry_type is a known kind",
///     with triggers, so a write path that forgets either rule fails at the statement.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryGuardTriggerTests
{
    [RetryFact]
    public async Task Insert_OfAFileRowWithATtl_Throws()
    {
        await using var connection = await OpenFreshBankAsync();

        var act = () => InsertAsync(connection, "h-file", "file", ttlDays: 30);

        var ex = await Should.ThrowAsync<SqliteException>(act);
        ex.Message.ShouldContain("ttl_days");
        (await CountAsync(connection, "h-file")).ShouldBe(0);
    }

    [RetryFact]
    public async Task Insert_OfAMemoryRowWithATtl_IsAccepted()
    {
        await using var connection = await OpenFreshBankAsync();

        await InsertAsync(connection, "h-memory", "memory", ttlDays: 30);

        (await CountAsync(connection, "h-memory")).ShouldBe(1);
    }

    [RetryFact]
    public async Task Update_ThatGivesAFileRowATtl_Throws()
    {
        await using var connection = await OpenFreshBankAsync();
        await InsertAsync(connection, "h-file", "file", ttlDays: null);

        var act = () => connection.ExecuteAsync("UPDATE entries SET ttl_days = 7 WHERE hash = 'h-file'");

        await Should.ThrowAsync<SqliteException>(act);
    }

    [RetryFact]
    public async Task Update_ThatRetypesATtlRowAwayFromMemory_Throws()
    {
        await using var connection = await OpenFreshBankAsync();
        await InsertAsync(connection, "h-memory", "memory", ttlDays: 7);

        var act = () => connection.ExecuteAsync("UPDATE entries SET entry_type = 'file' WHERE hash = 'h-memory'");

        await Should.ThrowAsync<SqliteException>(act);
    }

    [RetryFact]
    public async Task EnsureAsync_OnAV23BankWithTtlRows_ClearsTheFileTtlAndKeepsTheMemoryTtl()
    {
        await using var connection = await OpenFreshBankAsync();
        await DropGuardTriggersAsync(connection);
        await InsertAsync(connection, "h-file", "file", ttlDays: 30);
        await InsertAsync(connection, "h-memory", "memory", ttlDays: 30);
        await connection.ExecuteAsync("PRAGMA user_version = 23");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>("PRAGMA user_version")).ShouldBe(24);
        (await ReadTtlAsync(connection, "h-file")).ShouldBeNull("a non-memory row holds no TTL after v24");
        (await ReadTtlAsync(connection, "h-memory")).ShouldBe(30);
        var act = () => InsertAsync(connection, "h-file-2", "file", ttlDays: 5);
        await Should.ThrowAsync<SqliteException>(act);
    }

    /// <summary>
    ///     The ladder from a legacy bank backfills <c>entry_type</c> with UPDATEs on rows that may
    ///     carry a TTL: the guard cannot exist before the ladder reaches v24.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_FromV19WithATtlOnAFileRow_ReachesV24Healthy()
    {
        await using var connection = await OpenFreshBankAsync();
        await DropGuardTriggersAsync(connection);
        await DropEntryTypeColumnAsync(connection);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, "
            + "updated_at, chunk_index, total_chunks, ttl_days) "
            + "VALUES ('h-doc', 'docs/guide.md', 'v', 'docs/guide.md', NULL, 'project', 'proj-a', 1, 1, 0, 1, 30)");
        await connection.ExecuteAsync("PRAGMA user_version = 19");
        // A real legacy bank carries a stale digest, so the Ddl reruns before the ladder.
        await connection.ExecuteAsync("PRAGMA application_id = 0");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>("PRAGMA user_version")).ShouldBe(MemorySchema.CurrentVersion);
        (await ReadTtlAsync(connection, "h-doc")).ShouldBeNull();
        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);
        report.Status.ShouldBe(SchemaDoctorStatus.Healthy);
    }

    [RetryFact]
    public async Task Insert_OfAnUnknownKind_OnABankWhoseColumnHasNoCheck_Throws()
    {
        await using var connection = await OpenFreshBankAsync();
        await DropGuardTriggersAsync(connection);
        await DropEntryTypeColumnAsync(connection);
        await connection.ExecuteAsync("ALTER TABLE entries ADD COLUMN entry_type TEXT NOT NULL DEFAULT 'none'");
        await connection.ExecuteAsync("PRAGMA user_version = 23");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var act = () => InsertAsync(connection, "h-bogus", "bogus", ttlDays: null);
        var ex = await Should.ThrowAsync<SqliteException>(act);
        ex.Message.ShouldContain("entry_type");
    }

    [RetryFact]
    public async Task DiagnoseAsync_CountsTheRowsWithATtlThatAreNotMemory()
    {
        await using var connection = await OpenFreshBankAsync();
        await DropGuardTriggersAsync(connection);
        await InsertAsync(connection, "h-file", "file", ttlDays: 30);
        await InsertAsync(connection, "h-none", "none", ttlDays: 30);
        await InsertAsync(connection, "h-memory", "memory", ttlDays: 30);

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.EntryKinds.TtlOutsideMemory.ShouldBe(2);
    }

    [RetryFact]
    public async Task DiagnoseAsync_ReportsAMissingGuardTrigger()
    {
        await using var connection = await OpenFreshBankAsync();
        await connection.ExecuteAsync($"DROP TRIGGER {MemorySchema.EntryGuardTriggerNames[0]}");

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.ShapeMismatch);
        report.Findings.ShouldContain(f => f.ObjectName == MemorySchema.EntryGuardTriggerNames[0]);
    }

    private static async Task InsertAsync(SqliteConnection connection, string hash, string entryType, long? ttlDays) =>
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at, ttl_days, entry_type) "
            + "VALUES (@Hash, @Hash, 'v', 'project', 'proj', 1, 1, @Ttl, @Type)",
            new { Hash = hash, Ttl = ttlDays, Type = entryType });

    private static async Task<long> CountAsync(SqliteConnection connection, string hash) =>
        await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash = @Hash", new { Hash = hash });

    private static async Task<long?> ReadTtlAsync(SqliteConnection connection, string hash) =>
        await connection.ExecuteScalarAsync<long?>("SELECT ttl_days FROM entries WHERE hash = @Hash", new { Hash = hash });

    private static async Task DropGuardTriggersAsync(SqliteConnection connection)
    {
        foreach (var name in MemorySchema.EntryGuardTriggerNames)
        {
            await connection.ExecuteAsync($"DROP TRIGGER IF EXISTS {name}");
        }
    }

    /// <summary>SQLite refuses to drop a column a trigger names, so the typed embed triggers go with it.</summary>
    private static async Task DropEntryTypeColumnAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync("DROP TRIGGER IF EXISTS vec_entries_au");
        await connection.ExecuteAsync("DROP TRIGGER IF EXISTS vec_structure_au");
        await connection.ExecuteAsync("ALTER TABLE entries DROP COLUMN entry_type");
    }

    private static async Task<SqliteConnection> OpenFreshBankAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        return connection;
    }
}
