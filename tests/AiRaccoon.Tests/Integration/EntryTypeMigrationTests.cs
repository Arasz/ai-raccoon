using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     v20: <c>entries.entry_type</c> — the entry type a row holds, denormalised so the query
///     path can filter by it. The backfill classifies every row from its SOURCE, because the
///     row's own shape cannot say it: a transcript carries a source file and is chunked like an
///     ingested document, so the cheap signature would file every transcript as a document.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryTypeMigrationTests
{
    [RetryFact]
    public async Task EnsureAsync_AddsTheEntryTypeColumn()
    {
        await using var connection = await OpenLegacyBankAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('entries')");
        columns.ShouldContain("entry_type", "the v20 rung must add the entry_type column");
    }

    /// <summary>
    ///     The backfill classifies by source: a manual source is a memory, a TRANSCRIPT source is
    ///     a memory too (the agent wrote it), and a file source is a document.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_ClassifiesEveryRowByItsSource()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "note-hash", "manual", sourceFile: null);
        await SeedEntryAsync(connection, "transcript-hash", "transcript", sourceFile: "hermes/20260806_215718_fd7f66");
        await SeedEntryAsync(connection, "document-hash", "file", sourceFile: "docs/guide.md");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("memory");
        (await ReadEntryTypeAsync(connection, "transcript-hash")).ShouldBe("memory",
            "a transcript is agent-written memory - the session is only its locator");
        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file");
    }

    /// <summary>The backfill only fills NULL types: a rerun never clobbers an explicit one.</summary>
    [RetryFact]
    public async Task EnsureAsync_PreservesAnExplicitType_WhenRerun()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "note-hash", "manual", sourceFile: null);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE entries SET entry_type = 'file' WHERE hash = 'note-hash'");
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("file",
            "the backfill only fills NULL types - a rerun must never clobber an explicit type");
    }

    /// <summary>Crash-dirty: the column exists with only half the rows backfilled.</summary>
    [RetryFact]
    public async Task EnsureAsync_WithTheColumnPresentButTheBackfillHalfDone_FinishesIt()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "note-hash", "manual", sourceFile: null);
        await SeedEntryAsync(connection, "document-hash", "file", sourceFile: "docs/guide.md");
        await connection.ExecuteAsync("ALTER TABLE entries ADD COLUMN entry_type TEXT");
        await connection.ExecuteAsync("UPDATE entries SET entry_type = 'memory' WHERE hash = 'note-hash'");
        await StampV19Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("memory");
        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file",
            "the half-done backfill must finish on the rerun");
    }

    /// <summary>
    ///     A row with no provenance at all (a bank older than source normalisation) falls back to
    ///     the row's own shape: no source file and no chunks is a note, anything else is a document.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_ARowWithoutASource_FallsBackToItsShape()
    {
        await using var connection = await OpenLegacyBankAsync();
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, "
            + "updated_at, chunk_index, total_chunks) "
            + "VALUES ('legacy-hash', 'legacy.md', 'v', NULL, NULL, 'project', 'proj-a', 1, 1, -1, 0)");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "legacy-hash")).ShouldBe("memory",
            "a row with no source file and no chunks is a note");
    }

    private static async Task SeedEntryAsync(SqliteConnection connection, string hash, string sourceType,
        string? sourceFile)
    {
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO memory_source (source_type, source_locator, section) "
            + "VALUES (@SourceType, @Locator, NULL)",
            new { SourceType = sourceType, Locator = sourceFile ?? $"locator-{hash}" });
        var sourceId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM memory_source WHERE source_type = @SourceType AND source_locator = @Locator",
            new { SourceType = sourceType, Locator = sourceFile ?? $"locator-{hash}" });
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, "
            + "updated_at, source_id, chunk_index, total_chunks) "
            + "VALUES (@Hash, 'p.md', 'v', @SourceFile, NULL, 'project', 'proj-a', 1, 1, @SourceId, 0, 1)",
            new { Hash = hash, SourceFile = sourceFile, SourceId = sourceId });
    }

    private static async Task<string?> ReadEntryTypeAsync(SqliteConnection connection, string hash) =>
        await connection.ExecuteScalarAsync<string?>(
            "SELECT entry_type FROM entries WHERE hash = @Hash", new { Hash = hash });

    /// <summary>A bank at the version before this one: the column is dropped, the stamp rewound.</summary>
    private static async Task<SqliteConnection> OpenLegacyBankAsync()
    {
        var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('entries')")).ToList();
        if (columns.Contains("entry_type"))
        {
            await connection.ExecuteAsync("ALTER TABLE entries DROP COLUMN entry_type");
        }

        await StampV19Async(connection);
        return connection;
    }

    private static async Task StampV19Async(SqliteConnection connection)
    {
        await connection.ExecuteAsync("PRAGMA user_version = 19");
        (await connection.ExecuteScalarAsync<long>("PRAGMA user_version")).ShouldBe(19);
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
