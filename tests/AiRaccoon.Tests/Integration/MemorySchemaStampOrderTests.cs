using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

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
    [RetryFact]
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
    [RetryFact]
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

    /// <summary>
    ///     Pins the exit point at the <c>storedVersion >= CurrentVersion</c> early return: a bank
    ///     already at the current version can still have a stale digest (a digest-gated, no-version-bump
    ///     Ddl addition — the metrics/code-corpus tables shipped exactly this way), and that branch is
    ///     the ONLY remaining place such a bank's digest ever gets corrected.
    /// </summary>
    [RetryFact]
    public async Task ADigestOnlyDdlChange_OnAnAlreadyCurrentVersionBank_StampsTheDigestOnOpen()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // Simulates a legacy bank already at CurrentVersion whose digest predates a later,
        // digest-gated Ddl addition that never bumped CurrentVersion.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadApplicationIdAsync(connection)).ShouldBe(MemorySchema.SchemaDigest,
            "the storedVersion >= CurrentVersion branch must stamp the digest itself — it is the only exit for this case");
    }

    /// <summary>
    ///     Pins the <c>if (healthy)</c> guard at the ladder's terminal stamp: a ladder that did not
    ///     finish must leave BOTH the version and the digest stale, or a future open would cheap-path
    ///     a bank whose ladder never actually completed — the same bug class D5 exists to close.
    /// </summary>
    [RetryFact]
    public async Task AnUnhealthyLadder_DoesNotStampTheVersionOrTheDigest()
    {
        await using var connection = await OpenAsync();
        // A non-fresh, unstamped (v0, digest-mismatched) bank — see ACrashBetweenDdlAndLadder_StillMigratesOnNextOpen.
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE legacy_marker (id INTEGER)",
            cancellationToken: TestContext.Current.CancellationToken));

        MemorySchema.TestOnlyForceUnhealthyLadder.Value = true;
        try
        {
            await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        }
        finally
        {
            MemorySchema.TestOnlyForceUnhealthyLadder.Value = false;
        }

        (await ReadVersionAsync(connection)).ShouldNotBe(MemorySchema.CurrentVersion,
            "an unhealthy ladder must not stamp the version");
        (await ReadApplicationIdAsync(connection)).ShouldNotBe(MemorySchema.SchemaDigest,
            "an unhealthy ladder must not stamp the digest either, or the next open would cheap-path it as migrated");
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

    private static async Task<int> ReadApplicationIdAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "PRAGMA application_id", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;
}
