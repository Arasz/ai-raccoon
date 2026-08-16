using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WP5 (bank-open cost, before baseline): empirically counts the SQLite statements
///     <see cref="MemorySchema.EnsureAsync" /> executes on an already-current bank, via
///     <c>sqlite3_trace</c> on the real connection handle — not by reading or splitting the
///     <c>Ddl</c> source string, which would misparse the trigger bodies' embedded semicolons.
///     Test-only; no production code touched.
///     See docs/plans/2026-08-16-bank-open-cost-implementation.md §4.2, §8.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaDdlStatementCountTests
{
    [Fact]
    public async Task EnsureAsync_OnAnAlreadyCurrentBank_ExecutesTheEmpiricallyCountedStatements()
    {
        await using var connection = await OpenAsync();

        // First open: fresh bank, stamps CurrentVersion. Not the number under test.
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // Second open, same connection, now already at CurrentVersion — this is what every
        // logical bank open on an install past its first run actually pays today.
        var statements = new List<string>();
        strdelegate_trace tracer = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, tracer, null);
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);

        var versionReads = statements.Count(s => s.Contains("PRAGMA user_version", StringComparison.Ordinal));
        var legacyScopeProbes = statements.Count(s => s.Contains("watch.scope.", StringComparison.Ordinal));
        var triggerGuardProbes = statements.Count(s =>
            s.Contains("type = 'trigger' AND name = 'promotion_queue_entries_ad'", StringComparison.Ordinal));
        var ddlStatements = statements.Count - versionReads - legacyScopeProbes - triggerGuardProbes;

        var report = $"{statements.Count} total statements traced on the second (already-current) "
                      + $"EnsureAsync call: {versionReads} version read(s), {legacyScopeProbes} legacy-scope "
                      + $"probe(s), {triggerGuardProbes} trigger-guard probe(s), {ddlStatements} Ddl-block "
                      + $"statement(s).\n" + string.Join("\n---\n", statements);

        // Empirically observed and pinned here as a characterization test: if these move, either
        // the Ddl block changed or WP1's digest gate landed on this code path — either is worth
        // noticing, which is why this asserts the exact counts rather than "at least N".
        // 39, not the plan's "~30"/"roughly thirty-two" (§4.2): manually recounting the top-level
        // CREATE TABLE/VIRTUAL TABLE/TRIGGER/INDEX statements in Ddl gives 39 too — the plan
        // undercounted.
        versionReads.ShouldBe(1, report);
        legacyScopeProbes.ShouldBe(1, report);
        triggerGuardProbes.ShouldBe(1, report);
        ddlStatements.ShouldBe(39, report);
        statements.Count.ShouldBe(42, report);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
