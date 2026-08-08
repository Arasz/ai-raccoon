# Managed SQLite store layer (Microsoft.Data.Sqlite + Dapper + FTS5 + sqlite-vec)

Verified while replacing a native-SQLite-extension store with a self-managed
`memory.db` layer: schema init, FTS5 external-content search, vec0 from NuGet,
and the Dapper/C# traps that cost real debugging time. Prefer this shape over
loading third-party SQLite extensions — it is the one with a test story.

## Schema-init-on-every-open

One factory `OpenBankAsync` runs an idempotent DDL batch after pragmas +
extension loading. Microsoft.Data.Sqlite executes multiple statements in a
single `CommandText` (no batching setup needed):

```sql
CREATE TABLE IF NOT EXISTS entries (id INTEGER PRIMARY KEY, hash TEXT, ...,
  embed_state TEXT NOT NULL DEFAULT 'pending' CHECK(embed_state IN ('pending','embedded')), embedding BLOB NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(value, content='entries', content_rowid='id');
CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(embedding float[384]);
CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
  INSERT INTO entries_fts(rowid, value) VALUES (new.id, new.value); END;
-- AFTER DELETE: INSERT INTO entries_fts(entries_fts, rowid, value) VALUES('delete', old.id, old.value);
-- AFTER UPDATE OF value: 'delete' old row, then insert new
CREATE INDEX IF NOT EXISTS ...;
```

Keep `CREATE ... IF NOT EXISTS` everywhere so every bank open is safe and
cheap. Add columns the schema needs later (e.g. `workspace_id`) NOW, without
the constraints a later wave adds — schema evolution by wave is much cheaper
than ALTER TABLE.

## FTS5 external-content search

- The index is keyed by the content table's rowid: `content='entries',
  content_rowid='id'`. Triggers keep it in sync — never hand-insert.
- **`bm25(fts)` is negative and unbounded** (0 is best, more negative =
  worse). `ORDER BY bm25(fts)` ASC, and if you merge per-bucket results by
  keeping the "best" (max) ranking, negatives still work (closest to 0 wins).
  A 0..1 `minScore` threshold CANNOT be applied to raw BM25 rank — either
  normalize first or leave the threshold off with a TODO until the fusion
  wave owns normalization.
- **snippet() marker trap**: `snippet(fts, col, '…', '…', '…', 12)` wraps EACH
  matched token in the markers — output like `…semantic… …e2e… fact` breaks
  substring assertions on the snippet. Use EMPTY before/after markers for
  plain-text snippets: `snippet(fts, 0, '', '', '…', 12)`. Column index is
  0-based (0 = first fts5 column).
- Malformed `MATCH` queries throw `SqliteException` (unbalanced quotes, stray
  operators). Until query normalization exists, catch per-bucket and return
  empty for that bucket instead of failing the whole search.

## sqlite-vec (vec0) from NuGet — no native provisioning

`HiraokaHyperTools.sqlite-vec` (pin 0.1.9; pre-v1, re-verify on upgrade)
ships `Microsoft.Data.Sqlite.SqliteVectorExtensions.LoadVector()` and native
`vec0.{dylib,so,dll}` under `runtimes/<rid>/native/`. The .NET host resolves
them via its native-library search path, so **`connection.LoadVector()`
works in test output dirs with zero provisioning** — no downloads, no
extension dirs, no RID gates (unlike the sqliteai tarballs).

- Call `connection.EnableExtensions()` BEFORE `LoadVector()` (it's a
  `LoadExtension("vec0")` under the hood).
- Load it unconditionally in the factory — it is a compile-time NuGet dep,
  so tests should NOT pass a `loadExtensions: _ => { }` seam to skip it.
  Keep the seam only for optional sqliteai natives (cloudsync).
- vec0 table DDL: `CREATE VIRTUAL TABLE ... USING vec0(embedding float[384])`.
  Dimension is fixed at creation — an empty placeholder table is fine; the
  embedding wave owns recreating it if the model dimension differs.
- Optional natives (cloudsync): guard the load with `File.Exists(path)` — a
  missing module then disables that feature loudly at call time ("no such
  function") instead of failing every bank open. This is what lets E2E tests
  run with no provisioned natives at all.

## Dapper materialization traps (both cost real debugging time)

1. **Record ctor matching breaks on SQLite INTEGER → int.** Dapper
   materializes SQLite INTEGER columns as `long`; a record
   `record RatingRow(long CreatedAt, int AccessCount)` fails with
   *"A parameterless default constructor or one matching signature ... is
   required for ... materialization"* — the ctor match misses on type.
   Fix: use a **mutable class DTO with settable properties** for anything
   Dapper reads (this codebase already did that everywhere for the same
   reason — blob-affinity columns in the extension era). Records are fine
   only when you control the SELECT aliases AND the column types line up.
2. **`IS` with bound NULL params for nullable-column filtering.** A bucket
   filter like `scope IS @scope AND context_label IS @contextLabel AND
   workspace_id IS @workspaceId` matches NULL=NULL and 'a'='a' in one
   expression — the clean way to filter on nullable columns when Dapper
   passes nulls. (The alternative — COALESCE gymnastics — is worse.)
3. `SqliteConnection` has NO `LastInsertRowId` property — use
   `SELECT last_insert_rowid()`.
4. **Dapper `ExecuteAsync(sql, param, cancellationToken)` does not compile
   (CS1503)** — the overload's 4th positional parameter is `IDbTransaction?`,
   not a token. Wrap the call in a `CommandDefinition`:
   `connection.ExecuteAsync(new CommandDefinition(sql, new {...},
   cancellationToken: ct))`. The codebase's `Def(sql, parameters, ct)` helper
   exists precisely for this — reuse it instead of hand-rolling.

## C# language gotcha: anonymous-type spread does not exist

`new { hash = "h", ...bucketParams }` → **CS8635 "Unexpected character
sequence '...'"** (verified on .NET 10 SDK with LangVersion latest — the
anonymous-type spread was proposed but never shipped; only collection
expressions support `..`). Write the full anonymous type out explicitly, or
use a named DTO. This one bit three times in one session before being
checked.

## Interop/behavior notes

- TimeProvider: inject `TimeProvider` (DI: `AddSingleton(TimeProvider.System)`)
  into the store for `created_at`/`updated_at`/`last_accessed_at`; tests use
  `FakeTimeProvider` for deterministic timestamps.
- On-row metadata replaces any sidecar meta DB: rating bump on search =
  `UPDATE entries SET access_count = access_count + 1, last_accessed_at = @now,
  rating = @rating WHERE hash = @hash`, rating computed via the domain
  RatingPolicy from (access_count+1, age from created_at).
- Search contract fields (hash, seq, ranking, path, snippet): keep the shape
  even when interim — `0 AS Seq` literal, raw BM25 ranking, FTS5 snippet.
- FR-NM-1 single-file gate: assert the bank dir contains exactly `memory.db`
  (no `raccoon_meta.db`), metadata columns exist on the entry row
  (`pragma_table_info`), and workspace lifecycle writes land in `workspaces`
  inside memory.db. With a managed store these tests RUN everywhere — no
  provisioning, no skips.
