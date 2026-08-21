using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     ADR-0075: the <c>Ddl</c> block is gated on a digest of itself, stored in
///     <c>PRAGMA application_id</c>, so a digest-matched bank skips it on reopen while the three
///     unconditional repairs (ADR-0023, +1 for the watch-overlap prune demoted off the v11 ladder —
///     "fix(schema): demote watch-overlap prune from a v11 ladder step to an unconditional
///     every-open step") keep running.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaDigestTests
{
    [Fact]
    public async Task EnsureAsync_OnADigestMatchedBank_SkipsTheDdlBlock()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var statements = new List<string>();
        strdelegate_trace hook = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, hook, null);
        try
        {
            await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        statements.ShouldNotContain(sql => sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase),
            "a digest-matched bank must not re-run the Ddl block");
        statements.Count.ShouldBe(5,
            "the fast path is two header reads (user_version, application_id), the two unconditional repair probes, "
            + "and the S7 overlap-prune watches read (the S2 column ensure moved inside the digest-gated Ddl branch)");
    }

    [Fact]
    public async Task EnsureAsync_OnAFreshBank_StampsTheSchemaDigest()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadApplicationIdAsync(connection)).ShouldBe(MemorySchema.SchemaDigest);
    }

    [Fact]
    public async Task EnsureAsync_WhenTheStoredDigestIsStale_RerunsTheDdlBlock()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));
        // Prove the Ddl block actually reran, not merely that the digest changed back: drop an
        // object only Ddl creates and confirm reopen recreates it.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE metrics", cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "metrics")).ShouldBeTrue("a stale digest must re-run the Ddl block");
        (await ReadApplicationIdAsync(connection)).ShouldBe(MemorySchema.SchemaDigest,
            "the stale digest must be corrected back to the current one");
    }

    [Fact]
    public async Task EnsureAsync_RepairsADamagedTriggerOnADigestMatchedBank()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER promotion_queue_entries_ad",
            cancellationToken: TestContext.Current.CancellationToken));
        (await ReadApplicationIdAsync(connection)).ShouldBe(MemorySchema.SchemaDigest,
            "the digest must still match; only the trigger was damaged");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'promotion_queue_entries_ad'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull("the unconditional trigger repair must run even when the Ddl digest still matches");
    }

    [Fact]
    public void SchemaDigest_IsNotZero() =>
        // An unstamped bank reads application_id = 0 (SQLite's default). If the computed digest
        // ever collided with 0, a fresh bank would read a false match and skip Ddl entirely.
        MemorySchema.SchemaDigest.ShouldNotBe(0);

    private static async Task<int> ReadApplicationIdAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "PRAGMA application_id", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // The DDL declares vec0 virtual tables, so the module has to be loaded exactly as
        // SqliteConnectionFactory.InitializeAsync does before the schema can be applied.
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
