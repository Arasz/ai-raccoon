using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Our single-file bank schema (plan §2.2): entries with on-row metadata, workspaces,
///     settings, an FTS5 external-content index over entries(value) and an (empty until P4/P6)
///     vec0 table. Idempotent — safe to run on every bank open.
/// </summary>
internal static class MemorySchema
{
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                   content='entries',
                                   content_rowid='id'
                               );

                               -- vec0 stays empty until P4 embeds; P4 owns the embedding
                               -- dimension if the model is not all-MiniLM (384).
                               CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(embedding float[384]);

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
                                   INSERT INTO entries_fts(rowid, value) VALUES (new.id, new.value);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ad AFTER DELETE ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value) VALUES ('delete', old.id, old.value);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_au AFTER UPDATE OF value ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value) VALUES ('delete', old.id, old.value);
                                   INSERT INTO entries_fts(rowid, value) VALUES (new.id, new.value);
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
