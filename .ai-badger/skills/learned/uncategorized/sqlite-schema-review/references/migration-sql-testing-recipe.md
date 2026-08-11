# Migration SQL testing recipe

Verify migration SQL against a real bank copy, not just unit tests.

## Steps

1. **Copy the real bank** (never the live one):
   ```bash
   cp /path/to/memory.db /tmp/migration-test.db
   ```

2. **Strip the target objects** to simulate pre-migration state:
   ```sql
   DROP TABLE IF EXISTS memory_source;
   DROP INDEX IF EXISTS idx_entries_source_id;
   -- For column removal: rename table, recreate without column, copy data, drop old
   ALTER TABLE entries RENAME TO entries_old;
   CREATE TABLE entries (... /* without new columns */);
   INSERT INTO entries SELECT ... /* old columns only */ FROM entries_old;
   DROP TABLE entries_old;
   PRAGMA user_version = <previous_version>;
   ```

3. **Run migration SQL step by step**, checking `changes()` after each INSERT/UPDATE.

4. **Verify invariants**:
    - `SELECT count(*) FROM entries WHERE new_fk_column IS NULL` — should be 0
    - `SELECT count(*) FROM entries WHERE new_fk_column NOT IN (SELECT id FROM parent)` — should be 0
    - FTS row count matches entries row count
    - Triggers fire: INSERT test row → verify FTS/vec index rows exist
    - Triggers fire: DELETE test row → verify FTS/vec index rows removed

5. **Test edge cases**:
    - NULL values in dedup keys (IS vs = comparison)
    - Empty string vs NULL in UNIQUE/COALESCE indexes
    - FK enforcement: `PRAGMA foreign_keys = ON` then insert with nonexistent parent key
    - `changes()` after `INSERT OR IGNORE` (returns 0 if swallowed, not the previous row's id)

## Key SQLite gotchas to verify

- `ALTER TABLE ADD COLUMN ... REFERENCES` — REFERENCES silently dropped (finding #11)
- `UNIQUE(... expression ...)` in CREATE TABLE — illegal, use CREATE UNIQUE INDEX (finding #10)
- `INSERT OR IGNORE` + `last_insert_rowid()` — stale after swallowed insert (finding #2)
- `ms.col IS entries.col` — NULL-safe comparison, correct for nullable FK lookups (finding #8)
