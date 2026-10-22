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
    ///     The backfill classifies by the row's SHAPE: a row that mirrors a file on disk has
    ///     path = source_file and is a document; every other row is agent-written memory - a note
    ///     that cites a file, a note that came from a transcript, a note with no source at all.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_ClassifiesEveryRowByItsShape()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "document-hash", path: "docs/guide.md", sourceFile: "docs/guide.md");
        await SeedEntryAsync(connection, "citing-note-hash", path: "9f2c.md", sourceFile: "notes/probe.md");
        await SeedEntryAsync(connection, "transcript-hash", path: "9f2c.md", sourceFile: "hermes/20260806_215718_fd7f66");
        await SeedEntryAsync(connection, "plain-note-hash", path: "9f2c.md", sourceFile: null);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file");
        (await ReadEntryTypeAsync(connection, "citing-note-hash")).ShouldBe("memory",
            "a note that cites a file is agent-written memory - re-ingesting that file would not bring it back");
        (await ReadEntryTypeAsync(connection, "transcript-hash")).ShouldBe("memory",
            "a transcript is agent-written memory - the session is only its locator");
        (await ReadEntryTypeAsync(connection, "plain-note-hash")).ShouldBe("memory");
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
        await SeedEntryAsync(connection, "note-hash", path: "9f2c.md", sourceFile: null);
        await SeedEntryAsync(connection, "document-hash", path: "docs/guide.md", sourceFile: "docs/guide.md");
        await connection.ExecuteAsync("ALTER TABLE entries ADD COLUMN entry_type TEXT");
        await connection.ExecuteAsync("UPDATE entries SET entry_type = 'memory' WHERE hash = 'note-hash'");
        await StampV19Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("memory");
        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file",
            "the half-done backfill must finish on the rerun");
    }

    /// <summary>
    ///     A row with no source at all (a bank older than source normalisation) is a memory: it
    ///     mirrors no file.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_ARowWithoutASource_IsAMemory()
    {
        await using var connection = await OpenLegacyBankAsync();
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, "
            + "updated_at, chunk_index, total_chunks) "
            + "VALUES ('legacy-hash', 'legacy.md', 'v', NULL, NULL, 'project', 'proj-a', 1, 1, -1, 0)");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "legacy-hash")).ShouldBe("memory",
            "a row that mirrors no file is agent-written memory");
    }

    private static async Task SeedEntryAsync(SqliteConnection connection, string hash, string path,
        string? sourceFile)
    {
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO memory_source (source_type, source_locator, section) "
            + "VALUES (@SourceType, @Locator, NULL)",
            new { SourceType = sourceFile is null ? "manual" : "file", Locator = sourceFile ?? $"locator-{hash}" });
        var sourceId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM memory_source WHERE source_type = @SourceType AND source_locator = @Locator",
            new { SourceType = sourceFile is null ? "manual" : "file", Locator = sourceFile ?? $"locator-{hash}" });
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, "
            + "updated_at, source_id, chunk_index, total_chunks) "
            + "VALUES (@Hash, @Path, 'v', @SourceFile, NULL, 'project', 'proj-a', 1, 1, @SourceId, 0, 1)",
            new { Hash = hash, Path = path, SourceFile = sourceFile, SourceId = sourceId });
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
