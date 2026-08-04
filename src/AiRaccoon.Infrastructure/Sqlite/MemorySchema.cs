using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Single-file bank schema (plan §2.2): entries, workspaces, settings, an FTS5
///     external-content index (value, source_file, section) and content + structure vec0
///     tables. Idempotent on every bank open; legacy banks migrate (see MigrateAsync).
/// </summary>
internal static class MemorySchema
{
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds the Wave 2 (source_file, section) and Wave 6 (heading_path, structure_embedding)
    ///     columns when missing and rebuilds the FTS index when it predates the three-column
    ///     shape. Fresh banks are untouched.
    /// </summary>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('entries')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("source_file"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN source_file TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("section"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN section TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("heading_path"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN heading_path TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("structure_embedding"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN structure_embedding BLOB",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        var ftsSql = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (ftsSql is not null && ftsSql.Contains("source_file", StringComparison.Ordinal)
            && ftsSql.Contains("section", StringComparison.Ordinal))
        {
            // New shape in place; verify it is not an empty shell left by a crash between the
            // DROP/CREATE and the repopulate — the triggers keep it in sync only from here on.
            var ftsRows = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT count(*) FROM entries_fts", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var entryRows = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT count(*) FROM entries", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (ftsRows == entryRows)
            {
                return;
            }
        }

        // The old index cannot host weighted source/section columns: drop it with its
        // triggers, recreate in the new shape, and repopulate from the content table.
        // One transaction, so a crash mid-rebuild cannot leave a bank without an FTS index
        // (or with an empty shell) that never heals on reopen.
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DROP TRIGGER IF EXISTS entries_fts_ai;
                        DROP TRIGGER IF EXISTS entries_fts_ad;
                        DROP TRIGGER IF EXISTS entries_fts_au;
                        DROP TABLE IF EXISTS entries_fts;

                        CREATE VIRTUAL TABLE entries_fts USING fts5(
                            value,
                            source_file,
                            section,
                            content='entries',
                            content_rowid='id'
                        );

                        CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
                            INSERT INTO entries_fts(rowid, value, source_file, section)
                            VALUES (new.id, new.value, new.source_file, new.section);
                        END;

                        CREATE TRIGGER entries_fts_ad AFTER DELETE ON entries BEGIN
                            INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                            VALUES ('delete', old.id, old.value, old.source_file, old.section);
                        END;

                        CREATE TRIGGER entries_fts_au AFTER UPDATE OF value, source_file, section ON entries BEGIN
                            INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                            VALUES ('delete', old.id, old.value, old.source_file, old.section);
                            INSERT INTO entries_fts(rowid, value, source_file, section)
                            VALUES (new.id, new.value, new.source_file, new.section);
                        END;

                        INSERT INTO entries_fts(rowid, value, source_file, section)
                        SELECT id, value, source_file, section FROM entries;
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    // workspace_id exists now for the P8 structural-isolation wave; the FK and the
    // "workspace XOR committed scope" CHECK land with P8.
    private const string Ddl = """
                               CREATE TABLE IF NOT EXISTS workspaces (
                                   id TEXT PRIMARY KEY,
                                   project_id TEXT NOT NULL,
                                   agent_id TEXT NULL,
                                   name TEXT NULL,
                                   status TEXT NOT NULL,
                                   created_at INTEGER NOT NULL,
                                   closed_at INTEGER NULL
                               );

                               CREATE TABLE IF NOT EXISTS entries (
                                   id INTEGER PRIMARY KEY,
                                   hash TEXT,
                                   path TEXT,
                                   value TEXT,
                                   source_file TEXT,
                                   section TEXT,
                                   scope TEXT CHECK(scope IN ('shared','project','custom')) NULL,
                                   project_id TEXT NULL,
                                   context_label TEXT NULL,
                                   workspace_id TEXT NULL,
                                   agent_id TEXT NULL,
                                   created_at INTEGER NOT NULL,
                                   updated_at INTEGER NOT NULL,
                                   access_count INTEGER NOT NULL DEFAULT 0,
                                   last_accessed_at INTEGER NULL,
                                   rating REAL NOT NULL DEFAULT 0.5,
                                   ttl_days INTEGER NULL,
                                   embed_state TEXT NOT NULL DEFAULT 'pending' CHECK(embed_state IN ('pending','embedded')),
                                   embedding BLOB NULL,
                                   heading_path TEXT NULL,
                                   structure_embedding BLOB NULL,
                                   FOREIGN KEY (workspace_id) REFERENCES workspaces(id) ON DELETE RESTRICT,
                                   CHECK ((workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL))
                               );

                               CREATE TABLE IF NOT EXISTS settings (
                                   key TEXT PRIMARY KEY,
                                   value TEXT NOT NULL
                               );

                               CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                                   value,
                                   source_file,
                                   section,
                                   content='entries',
                                   content_rowid='id'
                               );

                               -- vec0 stays empty until P4 embeds; P4 owns the embedding
                               -- dimension if the model is not all-MiniLM (384).
                               CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(embedding float[384]);

                               -- Wave 6 structure modality: heading-path vectors, rowid = entry id.
                               -- Written only by the re-runnable backfill (StructureBackfillService);
                               -- the delete trigger keeps orphan rows out when an entry goes away.
                               CREATE VIRTUAL TABLE IF NOT EXISTS vec_structure USING vec0(embedding float[384]);

                               CREATE TRIGGER IF NOT EXISTS vec_structure_ad AFTER DELETE ON entries BEGIN
                                   DELETE FROM vec_structure WHERE rowid = OLD.id;
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
                                   INSERT INTO entries_fts(rowid, value, source_file, section)
                                   VALUES (new.id, new.value, new.source_file, new.section);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ad AFTER DELETE ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                   VALUES ('delete', old.id, old.value, old.source_file, old.section);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_au AFTER UPDATE OF value, source_file, section ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                   VALUES ('delete', old.id, old.value, old.source_file, old.section);
                                   INSERT INTO entries_fts(rowid, value, source_file, section)
                                   VALUES (new.id, new.value, new.source_file, new.section);
                               END;

                               -- vec0 has no triggers: embedding rows follow embed_state.
                               -- Marking embedded upserts the vec row (delete-then-insert so a
                               -- re-embed replaces rather than duplicates); marking pending or
                               -- deleting the entry removes it.
                               CREATE TRIGGER IF NOT EXISTS vec_entries_au AFTER UPDATE OF embed_state ON entries
                               WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
                               BEGIN
                                   DELETE FROM vec_entries WHERE rowid = NEW.id;
                                   INSERT INTO vec_entries(rowid, embedding) VALUES (NEW.id, NEW.embedding);
                               END;

                               CREATE TRIGGER IF NOT EXISTS vec_entries_pending AFTER UPDATE OF embed_state ON entries
                               WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
                               BEGIN
                                   DELETE FROM vec_entries WHERE rowid = OLD.id;
                               END;

                               CREATE TRIGGER IF NOT EXISTS vec_entries_ad AFTER DELETE ON entries BEGIN
                                   DELETE FROM vec_entries WHERE rowid = OLD.id;
                               END;

                               CREATE TABLE IF NOT EXISTS sync_meta (
                                   key TEXT PRIMARY KEY,
                                   value TEXT NOT NULL
                               );

                               CREATE TABLE IF NOT EXISTS sync_tombstones (
                                   hash TEXT NOT NULL,
                                   scope TEXT NOT NULL,
                                   deleted_at INTEGER NOT NULL,
                                   PRIMARY KEY (hash, scope)
                               );

                               -- File-watcher feature: persisted watch registrations and per-path
                               -- fingerprints (hash-skip). D3-normalized paths; runtime state is not persisted.
                               CREATE TABLE IF NOT EXISTS watches (
                                   project_id      TEXT NOT NULL,
                                   path            TEXT NOT NULL,
                                   created_at      INTEGER NOT NULL,
                                   last_change_ts  INTEGER NOT NULL,       -- catch-up watermark (D1)
                                   PRIMARY KEY (project_id, path)
                               );

                               CREATE TABLE IF NOT EXISTS watch_files (
                                   project_id      TEXT NOT NULL,
                                   path            TEXT NOT NULL,
                                   file_hash       TEXT NOT NULL,          -- SHA-256(path + full content)
                                   updated_at      INTEGER NOT NULL,
                                   PRIMARY KEY (project_id, path)
                               );

                               CREATE INDEX IF NOT EXISTS idx_entries_scope_project ON entries(scope, project_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(hash);
                               CREATE INDEX IF NOT EXISTS idx_entries_workspace ON entries(workspace_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_embed_state ON entries(embed_state, project_id);
                               CREATE INDEX IF NOT EXISTS idx_watches_project ON watches(project_id);
                               """;
}
