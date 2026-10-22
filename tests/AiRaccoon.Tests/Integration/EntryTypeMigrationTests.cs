using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     v20: <c>entries.entry_type</c> — the KIND a row holds, denormalised so the query path can
///     filter by it. The column is NOT NULL with the default <c>none</c>: an entry has exactly one
///     kind, and a row that carries none yet says so in the column rather than by a null. The
///     backfill classifies every row by its SHAPE, because the row's own source cannot say it: a
///     transcript carries a source file and is chunked like an ingested document, so the cheap
///     signature would file every transcript as a document.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryTypeMigrationTests
{
    private sealed record EntryTypeColumn(string Name, long NotNull, string? DefaultValue);

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

    /// <summary>
    ///     The backfill only fills rows that carry no kind: a rerun never clobbers an explicit one.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_PreservesAnExplicitType_WhenRerun()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "note-hash", "manual", sourceFile: null);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE entries SET entry_type = 'file' WHERE hash = 'note-hash'");
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("file",
            "the backfill only fills rows that carry no kind - a rerun must never clobber an explicit one");
    }

    /// <summary>
    ///     The column is NOT NULL with the default <c>none</c>: a row that carries no kind says so
    ///     in the column, and the column cannot hold a null at all.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_TheColumnIsNotNull_WithNoneAsItsDefault()
    {
        await using var connection = await OpenLegacyBankAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var column = (await connection.QueryAsync<EntryTypeColumn>(
                "SELECT name, \"notnull\" AS \"NotNull\", dflt_value AS DefaultValue FROM pragma_table_info('entries') WHERE name = 'entry_type'"))
            .Single();
        column.NotNull.ShouldBe(1, "the kind column is NOT NULL: a row holds one kind, and 'none' says it has none");
        column.DefaultValue.ShouldBe("'none'");
    }

    /// <summary>Crash-dirty: the column exists with only half the rows backfilled.</summary>
    [RetryFact]
    public async Task EnsureAsync_WithTheColumnPresentButTheBackfillHalfDone_FinishesIt()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "note-hash", path: "9f2c.md", sourceFile: null);
        await SeedEntryAsync(connection, "document-hash", path: "docs/guide.md", sourceFile: "docs/guide.md");
        await connection.ExecuteAsync("ALTER TABLE entries ADD COLUMN entry_type TEXT NOT NULL DEFAULT 'none'");
        await connection.ExecuteAsync("UPDATE entries SET entry_type = 'memory' WHERE hash = 'note-hash'");
        await StampV19Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "note-hash")).ShouldBe("memory");
        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file",
            "the half-done backfill must finish on the rerun");
    }

    /// <summary>
    ///     A bank touched by an earlier build of this schema carries the column as NULLABLE, and a
    ///     row the backfill has not reached is NULL there. The rerun has to finish those rows too,
    ///     or a NULL row is a row the typed query cannot place — the same state written two ways.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_WithTheNullableColumnOfAnEarlierBuild_FinishesItsNullRows()
    {
        await using var connection = await OpenLegacyBankAsync();
        await SeedEntryAsync(connection, "document-hash", path: "docs/guide.md", sourceFile: "docs/guide.md");
        await connection.ExecuteAsync("ALTER TABLE entries ADD COLUMN entry_type TEXT");
        await StampV19Async(connection);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync(connection, "document-hash")).ShouldBe("file",
            "a NULL row from a bank touched by an earlier build must still be classified");
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
            // The typed embed triggers name the column, and SQLite refuses to drop a column a
            // trigger refers to. A legacy bank has neither the column nor those triggers, so both
            // go together; the ladder restores the triggers with the typed tables.
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS vec_entries_au");
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS vec_structure_au");
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
