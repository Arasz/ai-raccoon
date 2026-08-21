using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Counts the SQLite statements <see cref="MemorySchema.EnsureAsync" /> executes, via
///     <c>sqlite3_trace</c> on the real connection handle — not by splitting the <c>Ddl</c> source
///     string, which would misparse the trigger bodies' embedded semicolons. Pins both sides of
///     ADR-0075's digest gate: 5 statements when the digest matches, 59 in the block when it does not.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaDdlStatementCountTests
{
    [Fact]
    public async Task EnsureAsync_WhenTheDigestMatches_SkipsTheDdlBlockEntirely()
    {
        await using var connection = await OpenAsync();

        // First open: fresh bank, runs Ddl and stamps both CurrentVersion and the schema digest.
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var statements = await TraceAsync(connection);

        // The whole point of WP1: an install past its first run pays five statements, not fifty-nine.
        CountDdl(statements).ShouldBe(0, Report(statements));
        statements.Count.ShouldBe(5, Report(statements));
    }

    [Fact]
    public async Task EnsureAsync_WhenTheDigestIsStale_RunsTheFiftyNineStatementDdlBlock()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A bank that predates the digest gate reads application_id = 0, which is what every
        // real bank looked like before ADR-0075 — so this is the cost the gate removes.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));

        var statements = await TraceAsync(connection);

        // Re-derived from a live trace (Report(statements)), not restated by hand: 56 CREATE/DROP
        // DDL statements (was last correctly counted at 42, before the ENTIRE code-corpus feature
        // — code_entries/code_fts/vec_code and their triggers/indexes, WP1-WP8 — landed without
        // anyone updating this test) + 3 for EnsureCodeEmbedAttemptsColumnAsync's own existence
        // check (S2), which CountDdl does not special-case since it runs on this same
        // digest-mismatch path, not the steady-state one the sibling test above pins to zero.
        CountDdl(statements).ShouldBe(59, Report(statements));
    }

    private static async Task<List<string>> TraceAsync(SqliteConnection connection)
    {
        var statements = new List<string>();
        strdelegate_trace tracer = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, tracer, null);
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        return statements;
    }

    /// <summary>Everything the trace saw that is not the version read, digest read/stamp, or a repair probe.</summary>
    private static int CountDdl(List<string> statements) =>
        statements.Count
        - statements.Count(s => s.Contains("PRAGMA user_version", StringComparison.Ordinal))
        - statements.Count(s => s.Contains("PRAGMA application_id", StringComparison.Ordinal))
        - statements.Count(s => s.Contains("watch.scope.", StringComparison.Ordinal))
        - statements.Count(s =>
            s.Contains("type = 'trigger' AND name = 'promotion_queue_entries_ad'", StringComparison.Ordinal))
        // The watch-overlap prune's own probe, demoted off the v11 ladder to an unconditional
        // every-open step ("fix(schema): demote watch-overlap prune from a v11 ladder step to an
        // unconditional every-open step") — a third repair probe alongside the two above.
        - statements.Count(s => s.Contains("FROM watches", StringComparison.Ordinal));

    private static string Report(List<string> statements) =>
        $"{statements.Count} statements traced, {CountDdl(statements)} of them Ddl:\n"
        + string.Join("\n---\n", statements);

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
