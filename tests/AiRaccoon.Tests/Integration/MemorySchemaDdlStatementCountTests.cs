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
///     ADR-0075's digest gate: 8 statements when the digest matches (4 gate reads + 4 code-corpus
///     every-open probes), 56 in the block when it does not.
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

        // The whole point of the gate: an install past its first run pays 8 cheap reads, not the 56-statement block.
        CountDdl(statements).ShouldBe(0, Report(statements));
        statements.Count.ShouldBe(8, Report(statements));
    }

    [Fact]
    public async Task EnsureAsync_WhenTheDigestIsStale_RunsTheFiftySixStatementDdlBlock()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A bank that predates the digest gate reads application_id = 0, which is what every
        // real bank looked like before ADR-0075 — so this is the cost the gate removes.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));

        var statements = await TraceAsync(connection);

        // 39 measured at ADR-0075, +1 model_migration (ADR-0076), +1 repair_requests, +1
        // promotion_queue_prune_requests, +14 the code corpus (ADR-0085: code_entries + code_fts +
        // vec_code, their trigger families, indexes, and the idx_code_entries_path DROP).
        CountDdl(statements).ShouldBe(56, Report(statements));
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
        // code-corpus every-open probes (S2 column ensure + S7 unconditional overlap prune):
        - statements.Count(s => s.Contains("name = 'code_entries'", StringComparison.Ordinal))
        - statements.Count(s => s.Contains("table_info('code_entries')", StringComparison.Ordinal))
        - statements.Count(s => s.Contains("table_info='code_entries'", StringComparison.Ordinal))
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
