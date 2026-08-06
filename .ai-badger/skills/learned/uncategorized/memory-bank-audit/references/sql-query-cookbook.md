# SQL query cookbook for memory.db audits

DB connection: `DB="file:$HOME/.ai-raccoon/memory.db?mode=ro"` then `sqlite3 "$DB" "<sql>"`.
All `created_at` values are unixepoch seconds (UTC). macOS `stat -f "%Sm"` is LOCAL time (UTC+2 in this env).

## Core state
```sql
SELECT COUNT(*) FROM entries;
SELECT scope, COUNT(*) FROM entries GROUP BY 1;
SELECT project_id, COUNT(*) FROM entries GROUP BY 1;
SELECT embed_state, COUNT(*) FROM entries GROUP BY 1;
SELECT MIN(datetime(created_at,'unixepoch')), MAX(datetime(created_at,'unixepoch')) FROM entries;
PRAGMA page_count; PRAGMA page_size; PRAGMA freelist_count;  -- storage: (page_count-freelist)*page_size / rows ≈ bytes/row
```

## Version accumulation (true re-ingest generations)
```sql
-- files with rows on 2+ distinct days, or creation spread > 60s → real edit-cycle generations
SELECT source_file, COUNT(*) c, COUNT(DISTINCT date(created_at,'unixepoch')) days
FROM entries WHERE source_file IS NOT NULL AND source_file != ''
GROUP BY source_file HAVING days > 1 ORDER BY c DESC;
SELECT source_file, COUNT(*) c, MAX(created_at)-MIN(created_at) spread_s
FROM entries WHERE source_file IS NOT NULL AND source_file != ''
GROUP BY source_file HAVING spread_s > 60 ORDER BY spread_s DESC;
-- top churned files with per-file unique-hash ratio (dup detection per file)
SELECT source_file, COUNT(*) rows, COUNT(DISTINCT hash) unique_hashes
FROM entries WHERE source_file LIKE '%<file>%' GROUP BY 1;
```

## Dedup semantics
```sql
-- duplicate content: groups + rows affected (as % of total = duplication rate)
SELECT COUNT(*) dup_groups, SUM(c) rows_in_dups FROM (SELECT hash, COUNT(*) c FROM entries GROUP BY hash HAVING c>1);
SELECT hash, COUNT(*) c, GROUP_CONCAT(DISTINCT scope), GROUP_CONCAT(DISTINCT project_id)
FROM entries GROUP BY hash HAVING c>1 ORDER BY c DESC LIMIT 8;
-- cross-scope collisions (shared copies by design?)
SELECT COUNT(*) FROM entries e WHERE e.scope='project' AND e.hash IN (SELECT hash FROM entries WHERE scope='shared');
-- mechanism test: does the dup chunk's text appear once or many times in the source doc?
grep -c "<text-prefix>" <source-doc>   -- 1 copy + N rows = concurrent-watcher race; N copies = blind-insert of repeated content
-- transaction signature: contiguous ids = single transaction; interleaved = concurrent transactions
SELECT id, datetime(created_at,'unixepoch'), substr(hash,1,12) FROM entries WHERE source_file LIKE '%<file>%' ORDER BY id;
```

## Self-correction / staleness
```sql
SELECT COUNT(ttl_days) FROM entries;
SELECT rating, COUNT(*) FROM entries GROUP BY 1 ORDER BY 2 DESC;
SELECT CASE WHEN access_count=0 THEN '0' ELSE '1+' END a, COUNT(*) FROM entries GROUP BY 1;
SELECT MAX(datetime(last_accessed_at,'unixepoch')) FROM entries;           -- date the last real search hit
SELECT COUNT(*) FROM entries WHERE access_count>0 AND last_accessed_at IS NOT NULL
  AND datetime(last_accessed_at,'unixepoch') > '2026-08-06 12:00:00';     -- bumps in a window
SELECT id, project_id, scope, datetime(created_at,'unixepoch'), rating, access_count, substr(value,1,200)
FROM entries WHERE id=<stale-id>;                                          -- stale-row forensics
SELECT key, value FROM settings ORDER BY key;                              -- sweep.threshold / extract.* / watch.* present?
SELECT * FROM sync_meta; SELECT * FROM sync_tombstones;
SELECT project_id, status, COUNT(*) FROM workspaces GROUP BY 1,2;
SELECT COUNT(workspace_id) FROM entries;
```

## Windowed vs total reconciliation (insert-stamp vs commit-stamp)
```sql
-- run in ONE connection; window counts rows stamped in the last 10 min, totals count committed rows
SELECT (SELECT COUNT(*) FROM entries) total,
       (SELECT COUNT(*) FROM entries WHERE created_at > strftime('%s','now') - 600) last10,
       (SELECT MAX(id) FROM entries) maxid;
-- exact window slice for event attribution
SELECT source_file, COUNT(*) FROM entries
WHERE created_at BETWEEN strftime('%s','2026-08-06 12:36:00') AND strftime('%s','2026-08-06 12:46:00') GROUP BY 1 ORDER BY 2 DESC;
```

## Growth decomposition
```sql
SELECT date(created_at,'unixepoch') d, COUNT(*) FROM entries GROUP BY 1 ORDER BY 1;
SELECT project_id, strftime('%H',created_at,'unixepoch') h, COUNT(*) FROM entries
WHERE date(created_at,'unixepoch')='<date>' GROUP BY 1,2 ORDER BY 2,1;  -- map hours to discrete events
```

## v3 audit baseline snapshot (2026-08-06 ~13:05 UTC) — for future comparison
- 2899 rows: ai-badger 612 / ai-raccoon 1526 / jsaa 761; scopes project 2895 / shared 3 / custom 1; all embedded, 0 pending.
- Bank born 2026-08-05 17:42:11 UTC. Aug 5 = 1403 (one-time bulk load), Aug 6 = 1496 (jsaa re-ingest 761 + ai-badger watch-init 171 + organic new-docs ~564).
- Duplication: 176 hash dup groups / 412 rows (14.2%), all intra-project; 0 cross-scope collisions.
- Version accumulation: ZERO files with 2+ generations (1.0.9 watcher = replace-by-path).
- ttl_days: 0 rows; access 0: 2740 (94.5%); ratings 0.5 default for unaccessed; shared rows (4302/4303/4305) first accessed 13:07:44 UTC.
- sections 673, heading_path 0, structure_embedding 0; sync_meta/tombstones 0; workspaces 3 acme test rows; watch errors flat at 76.
- Key code anchors: WatchDigestExecutor.cs:57-58 (delete-then-ingest), SqliteMemoryStore.cs:882-898 (racy check-then-insert), DegradationPolicy.cs:10 (sweep requires per-entry TTL).
