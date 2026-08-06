# sqlite-memory / sqlite-vector / sqlite-sync verified semantics

Verified against upstream source (fetched 2026-08, `main` branch — **re-verify
against the exact pinned tag** before relying on version-specific behavior).
Used in a wrapper review that found 4 real bugs the wrapper's own tests missed.

## How to fetch

```bash
curl -sL https://raw.githubusercontent.com/sqliteai/sqlite-memory/main/src/sqlite-memory.c > /tmp/sqlite-memory.c
curl -sL https://raw.githubusercontent.com/sqliteai/sqlite-memory/main/src/dbmem-search.c > /tmp/dbmem-search.c
```

## Verified facts (with the function to read)

1. **`memory_add_*` dedup is GLOBAL by content hash in default mode.**
   - `dbmem_storage_hash_compute` (sqlite-memory.c:194): with
     `preserve_duplicate_paths=0` (the default) and no path, hash = content
     hash only.
   - `dbmem_database_check_if_stored` (sqlite-memory.c:689):
     `SELECT length FROM dbmem_content WHERE hash=? LIMIT 1` — **no context
     filter**. Same content added to a second context is *skipped entirely*,
     no row is created.
   - Consequence: a "share"/"promote" wrapper that re-adds existing content
     into another context (`shared`) is a silent no-op; a "write" wrapper whose
     read-back is `WHERE context=@ctx AND value=@content` throws
     `InvalidOperationException` for cross-context duplicates.
   - Fix direction: give the re-added content a distinct logical path via
     `memory_add_content(path, value, ctx)` (path-scoped hash); plain
     `memory_add_text` still dedups by content hash even with
     `preserve_duplicate_paths=1` (path is empty).

2. **`memory_set_option` PERSISTS in `dbmem_settings`.**
   - `dbmem_settings_write` (sqlite-memory.c): `REPLACE INTO dbmem_settings
     (key, value)`. Options survive across connections ("Later connections
     reuse the saved provider/model").
   - Consequence: a connection factory that runs
     `memory_set_option('defer_embeddings', 1)` on **every open** clobbers the
     `defer_embeddings=0` that a configure step wrote earlier — the
     "configure then writes embed immediately" path breaks on the next
     connection.

3. **`memory_embed_pending(n)` requires a positive INTEGER when given 1 arg.**
   - `dbmem_embed_pending` (sqlite-memory.c:4030): with one arg, non-integer
     or `<= 0` → error "expects a positive INTEGER limit". Binding `NULL`
     (C# `DBNull`) errors. Call the 0-arg form or bind `-1` (means "all").

4. **`memory_delete(hash)` is GLOBAL across contexts.**
   - `dbmem_database_delete_hash` (sqlite-memory.c:730): deletes
     `dbmem_vault_fts`, `dbmem_vault`, `dbmem_content_source`, `dbmem_content`
     rows WHERE hash matches — every context. `memory_delete_context(ctx)` is
     the context-scoped variant. A sweep that deletes by hash can remove a
     `shared` copy of the same content.

5. **`memory_search` is an eponymous virtual table with hidden columns.**
   - `dbmem-search.c:467`: `CREATE TABLE x(query hidden, max_entries hidden,
     context hidden, hash, seq, ranking, path, snippet)`.
   - The `context` hidden column takes a **comma-separated list**, matched via
     `INSTR(',' || ?3 || ',', ',' || fts.context || ',') > 0` — a single
     context string works as a filter.
   - `dbmem_context_load_vector` (sqlite-memory.c): search ERRORS with "no
     content has been indexed yet" when `dimension == 0` (no embeddings ever
     computed) — no FTS-only fallback despite what some specs claim.

## Empirical verification (probe on real binaries, 2026-08)

The facts above were confirmed and **corrected** by running the real 1.3.5/1.0.0 binaries
via Microsoft.Data.Sqlite. Two of the reference's fix directions were incomplete:

1. **`memory_add_content(path, value, ctx)` does NOT create the second row unless
   `preserve_duplicate_paths=1` is set.** Probe result: with the default, even a distinct
   path is skipped (content-hash dedup ignores the path). Fix requires BOTH:
   `memory_set_option('preserve_duplicate_paths', 1)` at bank open AND a distinct logical
   path (e.g. `shared/<original-path>`). Same content then yields two rows with different
   path-scoped hashes (probe: `project:acme=1 shared=1`).

2. **`memory_add_content` never dedups, even same path + same content twice** → 2 rows.
   A "promote"/"share" wrapper must do its own existence check
   (`SELECT 1 FROM dbmem_content WHERE path=@path AND context=@ctx LIMIT 1`) before
   inserting, or re-sharing duplicates the row. Conversely `memory_add_text` (empty path)
   STILL dedups with `preserve_duplicate_paths=1` — FR-MEM "duplicate content written once"
   survives the flag.

3. **Cross-context duplicates via `memory_add_text`**: same content added to context B when
   it already exists in context A → skipped, no row in B, and a read-back of
   `WHERE context=@ctx AND value=@content` throws. Read-back must fall back to any row with
   the content (`ORDER BY CASE WHEN context=@ctx THEN 0 ELSE 1 END`).

4. **`memory_get_option('provider')` returns empty (not NULL) when no model configured** —
   usable as the "is a model configured?" gate for per-open option defaults.

5. **`memory_embed_pending()` 0-arg form works** ("process all"); the 1-arg form rejects
   NULL/≤0. Both error with "no embedding model configured" when no model is set — that is
   the expected deferred-mode error, not a wiring bug.

## Probe technique (how to verify, not guess)

System Python's `sqlite3` and the macOS system `sqlite3` CLI both **block extension loading**
(`AttributeError: enable_load_extension` / "no such function: load_extension"). The reliable
probe is a tiny throwaway .NET console app in /tmp:

```
dotnet new console -o /tmp/probe && cd /tmp/probe
dotnet add package Microsoft.Data.Sqlite   # version matching the repo
# Program.cs: new SqliteConnection("Data Source=/tmp/probe.db"); Open;
#   EnableExtensions(true); LoadExtension(<vector>); LoadExtension(<memory>);
#   run memory_set_option / memory_add_text / SELECT ... FROM dbmem_content; Dump per context
```

Copy the real modules into place first (from `~/.ai-raccon/extensions/<rid>/` or the pinned
GitHub release tarballs), then drive the exact SQL the wrapper emits. This settles dedup
scope, option persistence, and return-value semantics with measured evidence — cheaper than
re-reading the C source and immune to `main`-vs-tag drift.

**Security advisory check**: SQLitePCLRaw.lib.e_sqlite3 ≤ 2.1.11 has a high-severity advisory
(GHSA-2m69-gcr7-jv3q, vulnerable bundled SQLite). Repos pinning `SQLitePCLRaw.bundle_e_sqlite3`
2.1.12 are unaffected; a probe project that floats the version may emit NU1903 for the
transitive 2.1.11 — pin to match the repo before treating the warning as a repo problem.

## Review checklist derived from these

- [ ] Re-add/promote path creates a real row in the target context (search it,
      don't trust the returned record).
- [ ] Per-connection option defaults don't clobber persisted configuration.
- [ ] Optional parameters bound to NULL can't hit strict-argument errors.
- [ ] Delete-by-key semantics match the wrapper's context guarantees.
- [ ] Virtual-table hidden-column filters are comma-list compatible.
