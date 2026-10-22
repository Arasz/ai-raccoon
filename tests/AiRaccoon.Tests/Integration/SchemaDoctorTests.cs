using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     GH #357: <c>MemorySchema.EnsureAsync</c> proves every object EXISTS, never that it has the
///     right columns/types/constraints. <see cref="SchemaDoctor" /> verifies shape by comparing a
///     bank against the real <c>Ddl</c> applied to a throwaway in-memory bank — never a
///     hand-maintained second copy of the schema.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SchemaDoctorTests
{
    [RetryFact]
    public async Task DiagnoseAsync_OnAFreshHealthyBank_ReportsHealthyWithNoFindings()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.Healthy);
        report.Findings.ShouldBeEmpty();
        report.StoredVersion.ShouldBe(MemorySchema.CurrentVersion);
        report.CurrentVersion.ShouldBe(MemorySchema.CurrentVersion);
        report.StoredDigest.ShouldBe(MemorySchema.SchemaDigest);
        report.ExpectedDigest.ShouldBe(MemorySchema.SchemaDigest);
    }

    /// <summary>
    ///     The unclassified population is visible: a write path that omits the column lands 'none'
    ///     silently, so the count is how the omission shows up in a bank nobody has queried yet.
    /// </summary>
    [RetryFact]
    public async Task DiagnoseAsync_CountsTheRowsThatCarryNoKind()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at, entry_type) "
            + "VALUES ('h1', 'p1', 'v', 'project', 'proj', 1, 1, 'none'), "
            + "('h2', 'p2', 'v', 'project', 'proj', 1, 1, 'none'), "
            + "('h3', 'p3', 'v', 'project', 'proj', 1, 1, 'memory'), "
            + "('h4', 'p4', 'v', 'project', 'proj', 1, 1, 'file')",
            cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.EntryKinds.Unclassified.ShouldBe(2, "the rows that carry no kind are the rows the typed query cannot place");
        report.EntryKinds.Unknown.ShouldBe(0);
    }

    /// <summary>
    ///     A NULL kind is an unclassified row too: a bank touched by the first v20 build carries a
    ///     nullable column, and a NULL row satisfies neither the 'none' count nor the unknown
    ///     count. It has to be counted as unclassified, or the doctor would report a population it
    ///     cannot see.
    /// </summary>
    [RetryFact]
    public async Task DiagnoseAsync_CountsANullKindAsUnclassified()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // A nullable column, the way the first v20 build left it. The typed embed triggers name the
        // column, and SQLite refuses to drop a column a trigger refers to.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER IF EXISTS vec_entries_au", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER IF EXISTS vec_structure_au", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE entries DROP COLUMN entry_type", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE entries ADD COLUMN entry_type TEXT NULL", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) "
            + "VALUES ('h1', 'p1', 'v', 'project', 'proj', 1, 1)",
            cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.EntryKinds.Unclassified.ShouldBe(1, "a NULL kind is an unclassified row, not an invisible one");
        report.EntryKinds.Unknown.ShouldBe(0);
    }

    /// <summary>
    ///     A stored kind this build does not know is a defect, not an unclassified row: it is
    ///     counted apart, so a corrupt value cannot hide inside the 'none' count.
    /// </summary>
    [RetryFact]
    public async Task DiagnoseAsync_CountsAKindItDoesNotKnow()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at, entry_type) "
            + "VALUES ('h1', 'p1', 'v', 'project', 'proj', 1, 1, 'transcript')",
            cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.EntryKinds.Unknown.ShouldBe(1, "a kind this build does not know is a defect, not an unclassified row");
        report.EntryKinds.Unclassified.ShouldBe(0);
    }

    /// <summary>
    ///     The exact GH #357 repro: an `entries` table that already exists with a narrower shape
    ///     than the real DDL. `CREATE TABLE IF NOT EXISTS` silently no-ops against it — EnsureAsync
    ///     alone would report this bank as fine.
    /// </summary>
    [RetryFact]
    public async Task DiagnoseAsync_OnANarrowEntriesTable_DetectsTheMissingColumns()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE entries (only_one_column TEXT)", cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.ShapeMismatch);
        report.Findings.ShouldContain(f => f.ObjectName == "entries" && f.Detail.Contains("missing column 'hash'"));
        report.Findings.ShouldContain(f => f.ObjectName == "entries" && f.Detail.Contains("missing column 'path'"));
        report.Findings.ShouldContain(f => f.ObjectName == "entries" && f.Detail.Contains("unexpected column 'only_one_column'"));
    }

    [RetryFact]
    public async Task DiagnoseAsync_OnABankMissingAnIndex_DetectsTheMissingIndex()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP INDEX idx_entries_hash", cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.ShapeMismatch);
        report.Findings.ShouldContain(f => f.ObjectName == "idx_entries_hash" && f.Detail.Contains("missing index"));
    }

    /// <summary>WP1 acceptance criterion 2: the doctor's schema inventory reaches the code corpus, same as any other Ddl table.</summary>
    [RetryFact]
    public async Task DiagnoseAsync_OnABankMissingTheCodeCorpus_DetectsTheMissingCodeTables()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE code_entries", cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.ShapeMismatch);
        report.Findings.ShouldContain(f => f.ObjectName == "code_entries" && f.Detail.Contains("missing table"));
    }

    [RetryFact]
    public async Task DiagnoseAsync_OnABankStampedNewerThanThisBinary_ReportsVersionAheadNotAShapeMismatch()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {MemorySchema.CurrentVersion + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        report.Status.ShouldBe(SchemaDoctorStatus.VersionAheadOfBinary);
        report.Findings.ShouldBeEmpty("a newer binary's shape is unknown to this one — it must not be diffed as broken");
        report.StoredVersion.ShouldBe(MemorySchema.CurrentVersion + 1);
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
}
