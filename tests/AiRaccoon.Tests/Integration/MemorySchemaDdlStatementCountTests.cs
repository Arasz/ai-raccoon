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
///     ADR-0075's digest gate: 4 statements when the digest matches, 39 in the block when it does not.
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

        // The whole point of WP1: an install past its first run pays four statements, not forty-two.
        CountDdl(statements).ShouldBe(0, Report(statements));
        statements.Count.ShouldBe(4, Report(statements));
    }

    [Fact]
    public async Task EnsureAsync_WhenTheDigestIsStale_RunsTheThirtyNineStatementDdlBlock()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A bank that predates the digest gate reads application_id = 0, which is what every
        // real bank looked like before ADR-0075 — so this is the cost the gate removes.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));

        var statements = await TraceAsync(connection);

        // 39, not the plan's "~30"/"roughly thirty-two" (§4.2) — WP5 measured it and manually
        // recounting the top-level CREATE statements in Ddl agrees. The plan undercounted.
        CountDdl(statements).ShouldBe(39, Report(statements));
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
            s.Contains("type = 'trigger' AND name = 'promotion_queue_entries_ad'", StringComparison.Ordinal));

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
