using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Our single-file bank schema (plan §2.2): entries with on-row metadata, workspaces,
///     settings, an FTS5 external-content index over entries(value, source_file) and an (empty
///     until P4/P6) vec0 table. Idempotent — safe to run on every bank open. Wave 2 (plan C §3):
///     source_file carries the original file path so the FTS index can weight source matches
///     above body-text matches; legacy banks are migrated on open (see MigrateAsync).
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
    ///     Wave 2 migration: legacy banks lack source_file and section (and index only
    ///     entries.value). The columns are added when missing; the FTS index is rebuilt with
    ///     the three-column shape (value, source_file, section) when it still carries an old
    ///     shape. Fresh banks created by Ddl are already in the new shape and are untouched.
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

        var ftsSql = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (ftsSql is null || (ftsSql.Contains("source_file", StringComparison.Ordinal)
                              && ftsSql.Contains("section", StringComparison.Ordinal)))
        {
            return;
        }

        // The old index cannot host weighted source/section columns: drop it with its
        // triggers, recreate in the new shape, and repopulate from the content table.
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

                               CREATE INDEX IF NOT EXISTS idx_entries_scope_project ON entries(scope, project_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(hash);
                               CREATE INDEX IF NOT EXISTS idx_entries_workspace ON entries(workspace_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_embed_state ON entries(embed_state, project_id);
                               """;
}
