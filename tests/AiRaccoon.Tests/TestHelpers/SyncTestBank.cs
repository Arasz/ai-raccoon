using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A hand-built bank for sync tests: the minimal schema, the openers that mirror the DI
///     <c>openBank</c>/<c>openSnapshot</c>/<c>openReadOnly</c> delegates (vec0 loaded on all three,
///     snapshot read-write, read-only check read-only, optional password), and
///     <see cref="CreateService" />. The schema carries a <c>vec_entries</c> vec0 table and the two
///     production triggers that write it, so a sync DELETE or reindex UPDATE on <c>entries</c> fails
///     with "no such module: vec0" on the bank and snapshot openers when they skip <c>LoadVector</c>. The
///     read-only opener loads it for parity with DI only: <c>PRAGMA quick_check</c> never reads vec0.
///     <c>chunk_index</c> defaults to 0 here while production defaults it to -1, so a test that
///     cares about the sentinel writes it explicitly in its INSERT.
/// </summary>
public sealed class SyncTestBank(string bankPath, string? password = null)
{
    private const string Schema = """
CREATE TABLE IF NOT EXISTS entries (
    id INTEGER PRIMARY KEY,
    hash TEXT,
    path TEXT,
    value TEXT,
    source_file TEXT NULL,
    section TEXT NULL,
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
    embed_state TEXT NOT NULL DEFAULT 'pending',
    embedding BLOB NULL,
    heading_path TEXT NULL,
    structure_embedding BLOB NULL,
    chunk_index INTEGER NOT NULL DEFAULT 0,
    total_chunks INTEGER NOT NULL DEFAULT 0,
    source_id INTEGER NULL
);
CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS workspaces (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, agent_id TEXT NULL,
    name TEXT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL, closed_at INTEGER NULL);
CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NULL, created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS sync_tombstones (project_id TEXT NOT NULL, hash TEXT NOT NULL, scope TEXT NOT NULL,
    context_label TEXT NULL, deleted_at INTEGER NOT NULL, received_at INTEGER NULL, PRIMARY KEY (project_id, hash, scope));
CREATE TABLE IF NOT EXISTS memory_source (
    id INTEGER PRIMARY KEY,
    source_type TEXT NOT NULL,
    source_locator TEXT NOT NULL,
    section TEXT NULL,
    heading_path TEXT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source_identity
    ON memory_source(source_type, source_locator, COALESCE(section, ''));
CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id);
CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(ctx TEXT, embedding float[4] distance_metric=cosine);
CREATE TRIGGER IF NOT EXISTS vec_entries_pending AFTER UPDATE OF embed_state ON entries
WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
BEGIN
    DELETE FROM vec_entries WHERE rowid = OLD.id;
END;
CREATE TRIGGER IF NOT EXISTS vec_entries_ad AFTER DELETE ON entries BEGIN
    DELETE FROM vec_entries WHERE rowid = OLD.id;
END;
""";

    public string BankPath { get; } = bankPath;

    private string Connect(string path, string mode = "") =>
        $"Data Source={path}{(password is null ? "" : $";Password={password}")}{mode}";

    /// <summary>Opens <paramref name="path" /> and ensures the test schema exists on it.</summary>
    public async Task<SqliteConnection> CreateAndOpenAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(Connect(path));
        await conn.OpenAsync(ct);
        conn.EnableExtensions();
        conn.LoadVector();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>The bank at <see cref="BankPath" />, schema ensured.</summary>
    public Task<SqliteConnection> OpenBankAsync(CancellationToken ct = default) => CreateAndOpenAsync(BankPath, ct);

    /// <summary>Read-write open of a snapshot file: the workspace strip DELETEs + VACUUMs it.</summary>
    public async Task<SqliteConnection> OpenSnapshotAsync(string path, CancellationToken ct)
    {
        var conn = new SqliteConnection(Connect(path));
        await conn.OpenAsync(ct);
        conn.EnableExtensions();
        conn.LoadVector();
        return conn;
    }

    /// <summary>Read-only open of a snapshot file (the integrity check only reads).</summary>
    public async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken ct)
    {
        var conn = new SqliteConnection(Connect(path, ";Mode=ReadOnly"));
        await conn.OpenAsync(ct);
        conn.EnableExtensions();
        conn.LoadVector();
        return conn;
    }

    /// <summary>A <see cref="SyncService" /> over this bank and <paramref name="cloud" />.</summary>
    public SyncService CreateService(ICloudStore cloud, ILogger<SyncService>? logger = null) =>
        CreateService(_ => Task.FromResult(cloud), logger);

    /// <summary>As above, with a resolver that runs once per sync cycle.</summary>
    public SyncService CreateService(Func<CancellationToken, Task<ICloudStore>> resolveCloud,
        ILogger<SyncService>? logger = null) =>
        new(resolveCloud, OpenBankAsync, OpenSnapshotAsync, OpenReadOnlyAsync, TimeProvider.System,
            logger ?? NullLogger<SyncService>.Instance);
}
