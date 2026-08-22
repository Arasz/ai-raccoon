using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Data F1 / plan item D5: the schema digest (<c>PRAGMA application_id</c>) must be stamped
///     only after the version ladder's own <c>PRAGMA user_version</c> stamp is durable, never
///     before — otherwise a crash between the two leaves a bank whose digest already matches
///     while <c>storedVersion</c> is still stale, and <see cref="MemorySchema.EnsureCheapAsync" />
///     (the per-tool-call hot path, <c>SqliteMemoryStore.ModelMigration.cs</c>) trusts the digest
///     alone and never notices.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaStampOrderTests
{
    [Fact]
    public async Task ACrashBetweenDdlAndLadder_StillMigratesOnNextOpen()
    {
        await using var connection = await OpenAsync();
        // A pre-existing table defeats the "brand new empty file" fresh-detection while
        // storedVersion (PRAGMA user_version) and the digest (PRAGMA application_id) both still
        // read their SQLite defaults of 0 — exactly a legacy, never-migrated bank. EnsureAsync
        // therefore takes the digest-mismatch -> ladder path, not the fresh-bank path.
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE legacy_marker (id INTEGER)",
            cancellationToken: TestContext.Current.CancellationToken));

        // Simulates a process crash the instant the DDL block finishes — before the ladder that
        // follows gets to run at all.
        MemorySchema.TestOnlyAfterDdlHookAsync.Value =
            _ => throw new InvalidOperationException("simulated crash between DDL and the ladder");
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken));
        }
        finally
        {
            MemorySchema.TestOnlyAfterDdlHookAsync.Value = null;
        }

        // "The next open", on the hot per-tool-call path (SqliteMemoryStore.ModelMigration.cs:18)
        // — not the full EnsureAsync a CLI verb or a fresh connection open would run.
        await MemorySchema.EnsureCheapAsync(connection, TestContext.Current.CancellationToken);

        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion,
            "a bank interrupted between the DDL and the version ladder must not be cheap-pathed as already migrated");
    }

    /// <summary>The other side of the same coin: a bank that finished a real EnsureAsync must still
    /// let the cheap path skip the full ensure — the fix must not turn every cheap check into a full one.</summary>
    [Fact]
    public async Task EnsureCheapAsync_OnACompletedEnsure_SkipsTheFullEnsure()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // Witness: drop an object only EnsureAsync's ladder/fresh branch creates, and confirm the
        // cheap path does not notice or recreate it.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP INDEX uq_entries_shared_bucket", cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureCheapAsync(connection, TestContext.Current.CancellationToken);

        (await IndexExistsAsync(connection, "uq_entries_shared_bucket")).ShouldBeFalse(
            "a bank whose digest already matches must not pay for a full EnsureAsync on the cheap path");
    }

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

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;
}
