# sqlite-memory / sqlite-vector / sqlite-sync store integration (verified on ai-raccon)

> **SUPERSEDED (P1)** — the store layer moved off these extensions onto our own
> managed `memory.db` (see `managed-sqlite-store-patterns.md`): no sqlite-memory /
> sqlite-vector loading or provisioning, no `raccoon_meta.db` (rating metadata is
> on-row now), workspaces are a real table in memory.db. This file stays for the
> sync extension (cloudsync still loads until the own-sync wave) and for
> historical reference — do NOT follow the workspace/rating notes below for new
> work.

Implementation-side traps found while wiring a C# .NET MCP server to the sqliteai
extensions (sqlite-memory 1.3.5, sqlite-vector 1.0.0, sqlite-sync 1.1.2), all verified
against the REAL binaries. The spec-side research (function catalog, embeddings API,
MCP SDK packages) lives in the design-specification skill's references; this file is the
store-layer counterpart.

## Loading the native extensions (Microsoft.Data.Sqlite)

- `connection.EnableExtensions(true)` then `connection.LoadExtension(path)` per module,
  in order: **vector → memory → cloudsync** (memory's `memory_search` requires vector).
- Use `SQLitePCLRaw.bundle_e_sqlite3`; do NOT use `bundle_winsqlite3` on Windows — it
  cannot load extensions. Microsoft.Data.Sqlite keeps the extension loaded across
  reopen on the same connection string, so load once per connection is fine.
- Extension loading may need PATH/LD_LIBRARY_PATH/DYLD_LIBRARY_PATH set so the module's
  own deps resolve (documented Microsoft.Data.Sqlite caveat).
- WAL + busy_timeout=5000 on every bank connection (multiple agent processes → multiple
  connections on one DB file).

## Module naming trap (real bug found)

The loadable-module **basename determines the SQLite entry point**: SQLite looks for
`sqlite3_<basename>_init`. The archives contain `vector.dylib`, `memory.dylib`,
`cloudsync.dylib` (entry points `sqlite3_vector_init`, `sqlite3_memory_init`,
`sqlite3_cloudsync_init`). Do NOT rename them to `vector0`/`memory0`/`sync0` — the entry
point lookup breaks at load time. Keep `ModulePrefix` = `vector`/`memory`/`cloudsync`
exactly, and verify entry points with `nm -gU <module> | grep sqlite3_.*_init` after
provisioning. (The first version of the catalog used `vector0` etc.; the integration
test caught it only after manual `nm` verification — check symbols, don't assume.)

## memory_add_text returns 1, not the hash

`SELECT memory_add_text(@content, @context)` returns INTEGER 1 (success) — it does NOT
return the content hash. Read the row back instead:

```sql
SELECT hash, path, context, value, created_at
FROM dbmem_content
WHERE context = @context AND value = @content
ORDER BY rowid DESC
LIMIT 1
```

Content-hash dedup makes (context, content) unique per context, so this is safe. The
first implementation cast the scalar to string and crashed with InvalidCastException
against the real extension (unit tests with fake stores never caught it — only the
real-extension integration test did).

## Deferred embeddings: writes fail without a configured model

Real behavior: `memory_add_text` errors with `memory_set_model must be called before
adding content` when no embedding model is configured. To make writes work before any
model exists (the FR-MEM-1.12 deferred path):

- On every bank open: `SELECT memory_set_option('defer_embeddings', 1)` — content is
  stored with `indexed=false`, invisible to `memory_search` until embedded.
- In `memory_configure` (after `memory_set_model`): `SELECT
  memory_set_option('defer_embeddings', 0)` so a configured model embeds immediately.
- `SELECT memory_embed_pending(@limit)` batches the deferred rows later; `SELECT
  memory_pending_count()` reports what's left.

Consequence for integration tests: with no model configured, search returns nothing for
deferred content — assert the STORAGE surface (write/stats/delete/share/list) in the
no-model integration suite, and leave the semantic search round-trip to unit tests over
the scope/merge logic or a manual test with a real GGUF model.

## Real-extension integration test pattern

The store's "done means proven" gate needs the real binaries. Pattern that keeps CI
green without them:

1. Provision the host RID's modules once (download pinned release tarballs, extract,
   verify SHA-256 — or copy from `~/.ai-raccon/extensions/<rid>/` if a dev already ran
   the server).
2. In the test, copy from that shared cache into a temp data root, then try
   `factory.OpenBankAsync()`; on `SqliteException` (extension missing) **skip the test
   by returning early** — never fail CI on hosts without binaries.
3. Run them filtered: `dotnet test --filter FullyQualifiedName~Integration`.

Verified integration surface on ai-raccon: write (project context), write dedup (same
hash), write with workspaceId (lands in `workspace:<id>`), share promotion (re-adds
content under `shared`), delete, stats (committed contexts incl. `shared`), list-by-
context.

## Workspace/sweep orchestration notes

- Workspace existence is a context namespace, not a table row: begin mints an id and
  returns `workspace:<id>`; status lists `ListContextAsync(workspace:<id>)`; consolidate
  re-writes kept entries to the project context then `memory_delete_context`; discard is
  just the delete. No registry table needed.
- Sweep (degradation) lists only the project context — the `shared` context is
  sweep-exempt by contract. Rating metadata lives in a separate local-only SQLite DB
  (`raccon_meta.db`) because `dbmem_content`'s schema is fixed (CRDT sync schema hash
  must match across replicas — never add columns to it).
- Sync: enumerate committed contexts (`shared` OR `project:%`, workspaces excluded),
  `memory_enable_sync(context)` per context, then `cloudsync_network_init(dbId)` +
  `cloudsync_network_set_apikey(key)` + `cloudsync_network_sync()` TWICE (send then
  receive) + `memory_reindex()`. Credentials only from env/config — never defaults.
