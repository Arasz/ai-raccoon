using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     P3 (ADR-0097): <c>search_quality.kind</c> — nullable <c>TEXT</c> with
///     <c>CHECK(kind IN ('memory','code','both'))</c>, added by the v11→v12 ladder rung only,
///     with a cutoff backfill (<c>created_at &lt; 1788447366</c>, the <c>356afe95</c> landing —
///     pre-kind rows all ran the memory leg) to <c>'memory'</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchQualityKindMigrationTests
{
    // Backfill cutoff: `git show -s --format=%ct 356afe95` — the commit whose rows predate
    // kind entirely, so every row older than this ran the memory leg.
    private const long BackfillCutoff = 1788447366;

    [RetryFact]
    public async Task EnsureAsync_OnAV11Bank_AddsNullableKindColumnWithCheck()
    {
        await using var connection = await OpenV11BankAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('search_quality')");
        columns.ShouldContain("kind", "the v12 rung must add the kind column to a v11 bank");
    }

    [RetryFact]
    public async Task EnsureAsync_OnAV11Bank_BackfillsPreCutoffRowsToMemory_AndLeavesNewerRowsNull()
    {
        await using var connection = await OpenV11BankAsync();
        await SeedRowAsync(connection, "corr-old", BackfillCutoff - 1);
        await SeedRowAsync(connection, "corr-boundary", BackfillCutoff);
        await SeedRowAsync(connection, "corr-new", BackfillCutoff + 1);
        // Duplicates: identical content under two correlation ids — every entry backfills,
        // the UPDATE is per-row, never "one representative per content".
        await SeedRowAsync(connection, "corr-dup-a", BackfillCutoff - 100);
        await SeedRowAsync(connection, "corr-dup-b", BackfillCutoff - 100);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadKindAsync(connection, "corr-old")).ShouldBe("memory");
        (await ReadKindAsync(connection, "corr-dup-a")).ShouldBe("memory");
        (await ReadKindAsync(connection, "corr-dup-b")).ShouldBe("memory");
        (await ReadKindAsync(connection, "corr-boundary")).ShouldBeNull(
            "ties stay NULL — the backfill is strictly older-than, never older-or-equal");
        (await ReadKindAsync(connection, "corr-new")).ShouldBeNull(
            "post-cutoff rows predate no kind; their kind arrives on the write path, not the backfill");
    }

    [RetryFact]
    public async Task EnsureAsync_OnAV11Bank_PreservesAnExplicitKind_WhenRerun()
    {
        await using var connection = await OpenV11BankAsync();
        await SeedRowAsync(connection, "corr-old", BackfillCutoff - 1);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE search_quality SET kind = 'code' WHERE correlation_id = 'corr-old'");
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadKindAsync(connection, "corr-old")).ShouldBe("code",
            "the backfill only fills NULL kinds — a rerun must never clobber an explicit kind");
    }

    /// <summary>
    ///     Crash-dirty: the column exists (a previous run died after the ALTER) with only half
    ///     the rows backfilled — the rerun must skip the ALTER without a duplicate-column error
    ///     and finish the backfill.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_WithKindColumnPresentButBackfillHalfDone_CompletesWithoutDuplicateColumnError()
    {
        await using var connection = await OpenV11BankAsync();
        await SeedRowAsync(connection, "corr-old-a", BackfillCutoff - 1);
        await SeedRowAsync(connection, "corr-old-b", BackfillCutoff - 2);
        await connection.ExecuteAsync(
            "ALTER TABLE search_quality ADD COLUMN kind TEXT CHECK(kind IN ('memory','code','both'))");
        await connection.ExecuteAsync(
            "UPDATE search_quality SET kind = 'memory' WHERE correlation_id = 'corr-old-a'");
        await StampV11Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadKindAsync(connection, "corr-old-a")).ShouldBe("memory");
        (await ReadKindAsync(connection, "corr-old-b")).ShouldBe("memory");
        (await ReadVersionAsync(connection)).ShouldBe(12);
    }

    /// <summary>
    ///     Staged absence: v11-stamped, digest current, table dropped (a runtime DROP the digest
    ///     gate cannot see — the digest hashes the Ddl string, not the live objects). The v12
    ///     rung must recreate the table with the kind column; nothing else can.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAV11Bank_WithSearchQualityDropped_RecreatesItWithKind()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("DROP TABLE search_quality");
        await StampV11Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('search_quality')");
        columns.ShouldContain("kind");
        columns.ShouldContain("result_features",
            "the digest-DDL recreate must restore the full current shape (P6b), not just the v12 column");
        (await ReadVersionAsync(connection)).ShouldBe(12);
    }

    /// <summary>
    ///     Empty old-shape variant: a v11 bank whose search_quality holds no rows migrates
    ///     clean — the backfill is a set UPDATE with no row reads, so emptiness is a no-op,
    ///     not an edge in a materializer.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAnEmptyV11Bank_MigratesClean()
    {
        await using var connection = await OpenV11BankAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadVersionAsync(connection)).ShouldBe(12);
        var columns = await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('search_quality')");
        columns.ShouldContain("kind");
    }

    /// <summary>
    ///     Fixture-unchanged: grades and follow-through recorded against kind-NULL (pre-P3) rows
    ///     behave exactly as today after the migration — the backfill touches kind only.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAV11BankWithGradesAndFollowThrough_PreservesThemUnchanged()
    {
        await using var connection = await OpenV11BankAsync();
        await SeedRowAsync(connection, "corr-graded", BackfillCutoff - 10);
        await connection.ExecuteAsync(
            """
            UPDATE search_quality
            SET usefulness_grade = 5, grade_note = 'great',
                follow_through_count = 2, follow_through_files = '["a.md","b.md"]'
            WHERE correlation_id = 'corr-graded'
            """);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var row = await connection.QuerySingleAsync(
            "SELECT usefulness_grade AS Grade, grade_note AS Note, follow_through_count AS FtCount, "
            + "follow_through_files AS FtFiles, kind AS Kind FROM search_quality WHERE correlation_id = 'corr-graded'");
        ((int)row.Grade).ShouldBe(5);
        ((string)row.Note).ShouldBe("great");
        ((int)row.FtCount).ShouldBe(2);
        ((string)row.FtFiles).ShouldBe("[\"a.md\",\"b.md\"]");
        ((string?)row.Kind).ShouldBe("memory");
    }

    /// <summary>
    ///     Stages a v11 bank with the OLD search_quality shape: fresh Ensure, then drop the kind
    ///     column when the binary already has it (tolerates the pre-P3 binary where the probe
    ///     finds no column), seed nothing, stamp back to v11. Digest stays current throughout —
    ///     staging never touches the Ddl string — so only the version ladder can heal it.
    /// </summary>
    private static async Task<SqliteConnection> OpenV11BankAsync()
    {
        var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var columns = (await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('search_quality')")).ToList();
        if (columns.Contains("kind"))
        {
            await connection.ExecuteAsync("ALTER TABLE search_quality DROP COLUMN kind");
        }

        await StampV11Async(connection);
        return connection;
    }

    private static async Task StampV11Async(SqliteConnection connection)
    {
        await connection.ExecuteAsync("PRAGMA user_version = 11");
        (await ReadVersionAsync(connection)).ShouldBe(11);
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>("PRAGMA user_version");

    private static async Task SeedRowAsync(SqliteConnection connection, string correlationId, long createdAt) =>
        await connection.ExecuteAsync(
            "INSERT INTO search_quality (correlation_id, query, scope, project_id, session_id, "
            + "result_count, top_source_files, created_at) "
            + "VALUES (@Id, 'q', 'all', 'proj-a', 'sess-test', 1, '[]', @CreatedAt)",
            new { Id = correlationId, CreatedAt = createdAt });

    private static async Task<string?> ReadKindAsync(SqliteConnection connection, string correlationId) =>
        await connection.ExecuteScalarAsync<string?>(
            "SELECT kind FROM search_quality WHERE correlation_id = @Id", new { Id = correlationId });

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
