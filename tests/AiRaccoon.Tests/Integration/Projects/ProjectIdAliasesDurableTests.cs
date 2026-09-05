using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Package D (durable alias-map table + migration): the repair job persists the applied
///     one-shot map into <c>project_id_aliases</c> on success, rows immutable thereafter.
///     <para>
///         Honesty ledger (mutation : filter : fixture): drop-CREATE-TABLE : fresh-bank-creates :
///         empty bank; skip-ladder-step : v13-bank-creates-via-ladder : version-13 stamp;
///         values-overwrite : append-only-first-writer-wins : pre-seeded alias row;
///         case-folding-collation : round-trips-ordinal-case : OLD-ID/old-id pair.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(AiRaccoon.Tests.Unit.Projects.ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectIdAliasesDurableTests
{
    /// <summary>D-AC(3): a fresh bank gains the table with the contracted columns and the current stamp.</summary>
    [RetryFact]
    public async Task EnsureAsync_OnAFreshBank_CreatesProjectIdAliasesTable()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "project_id_aliases")).ShouldBeTrue();
        (await ColumnsAsync(connection, "project_id_aliases"))
            .ShouldBe(["alias", "winner", "kind", "applied_at"], "contracted column order");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>D-AC(3): the migration is idempotent — a second open keeps inserted rows byte-identical.</summary>
    [RetryFact]
    public async Task EnsureAsync_Twice_PreservesInsertedRows()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES ('old-id', 'new-id', 'alias', 42)",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Alias, string? Winner, string Kind, long AppliedAt)>(
                new CommandDefinition(
                    "SELECT alias AS Alias, winner AS Winner, kind AS Kind, applied_at AS AppliedAt FROM project_id_aliases",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldHaveSingleItem();
        rows[0].ShouldBe(("old-id", "new-id", "alias", 42L));
    }

    /// <summary>
    ///     D-AC(3): a bank stamped at v13 (every bank predating this change) gains the table through
    ///     the ladder step — not just the digest-gated Ddl. The digest still matches here (nothing
    ///     else changed on disk), so only the version ladder can create the table.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAV13Bank_CreatesTableViaLadder_AndStampsCurrent()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS project_id_aliases; PRAGMA user_version = 13",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "project_id_aliases")).ShouldBeTrue(
            "the v14 ladder step must create the table on a v13-stamped bank");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     D-AC(1): the persisted map round-trips the applied entries, and alias lookup is
    ///     case-SENSITIVE (Ordinal): <c>OLD-ID</c> and <c>old-id</c> coexist as distinct rows.
    /// </summary>
    [RetryFact]
    public async Task PersistedMap_RoundTrips_WithOrdinalCaseSensitivity()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var map = new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("OLD-ID", "winner-a"), new ProjectIdAliasEntry("old-id", "winner-b")],
            [],
            ["qa-noise-project"]);

        await ProjectIdAliases.PersistAppliedAsync(connection, map, 77, TestContext.Current.CancellationToken);

        var roundTripped = await ProjectIdAliases.LoadAsync(connection, TestContext.Current.CancellationToken);
        roundTripped.Aliases.ShouldBe(
            [new ProjectIdAliasEntry("OLD-ID", "winner-a"), new ProjectIdAliasEntry("old-id", "winner-b")],
            "both case variants survive as distinct rows — the PK is case-sensitive");
        roundTripped.Dropped.ShouldBe(["qa-noise-project"]);
    }

    /// <summary>
    ///     D-AC(1): rows are immutable — persisting a map whose alias already has a winner keeps
    ///     the first writer (alias-PK first-writer-wins) while appending genuinely new entries.
    /// </summary>
    [RetryFact]
    public async Task PersistApplied_IsAppendOnly_FirstWriterWins()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES ('alias-x', 'winner-1', 'alias', 100)",
            cancellationToken: TestContext.Current.CancellationToken));
        var map = new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("alias-x", "winner-2"), new ProjectIdAliasEntry("alias-y", "winner-3")],
            [],
            []);

        await ProjectIdAliases.PersistAppliedAsync(connection, map, 200, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Alias, string? Winner, long AppliedAt)>(
                new CommandDefinition(
                    "SELECT alias AS Alias, winner AS Winner, applied_at AS AppliedAt FROM project_id_aliases ORDER BY alias",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldBe(
            [("alias-x", "winner-1", 100L), ("alias-y", "winner-3", 200L)],
            "the first winner stands; the new entry appends with the new timestamp");
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<IReadOnlyCollection<string>> ColumnsAsync(SqliteConnection connection, string table) =>
    [
        .. await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT name FROM pragma_table_info('{table}')",
            cancellationToken: TestContext.Current.CancellationToken))
    ];

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;
}
