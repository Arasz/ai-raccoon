# Physical-layer audit: space, WAL, holders

Use when the bank looks bloated, disk footprint >> logical content, the WAL grew huge, or after bulk deletes/sweeps. Complements the logical-content audit in SKILL.md. All mutations happen on COPIES — never on the live bank.

## Tooling

- `sqlite3_analyzer` — official SQLite space-utilization analyzer (TCL over the dbstat virtual table). `brew install sqlite-analyzer`, or the official tools zip. The sqlite.org/download.html links are JS-injected, but the page embeds a
  machine-readable CSV comment (`PRODUCT,VERSION,RELATIVE-URL,...`) — the URL is
  `https://www.sqlite.org/<RELATIVE-URL>` (macOS arm64: `sqlite-tools-osx-arm64-<ver>.zip`; unsigned binaries need `xattr -d com.apple.quarantine`).
- The analyzer report is itself valid SQL — the raw stats can be loaded into sqlite3 and queried further.
- Scope limit: it reads only the database FILE. Blind to the WAL sidecar and to query performance (its doc has zero mentions of either).

## Workflow

1. **Snapshot**: `sqlite3 <bank> ".backup /tmp/bank-snap.db"`. `.backup` preserves the page layout (freelist included). Verify fidelity: live `PRAGMA page_count` /
   `freelist_count` must equal the snapshot's (measured identical: 7200 / 3446).
2. **Space report**: `sqlite3_analyzer /tmp/bank-snap.db` → freelist % of file, payload %, per-table pages (tables+indices, then separately), unused bytes, non-sequential (fragmented) pages, b-tree depth, overflow.
3. **Logical content**: `sqlite3 snap "VACUUM INTO '/tmp/bank-vac.db'"`; compare sizes. db − vac = reclaimable freelist. Time the VACUUM (and an in-place `VACUUM` on another copy) to ground the fix-cost claim.
4. **WAL forensics**: `cp <bank> <bank>-wal <bank>-shm /tmp/walx/`, then
   `sqlite3 /tmp/walx/<bank> "PRAGMA wal_checkpoint(TRUNCATE);"` → tuple (busy, uncheckpointed_frames, checkpointed_by_call). `0|0|0` and the WAL file drops to 0 bytes = the entire WAL was already-checkpointed garbage (crash would replay
   nothing). WAL that does NOT shrink = real uncheckpointed frames (checkpointing broken), distinct from not-truncating (readers block it — see mechanism below).
5. **Holders**: `lsof <bank>` (list every PID) + `ps -o pid,lstart,command -p <pids>`. Date files with APFS birth time: `stat -f "%N born=%SB mtime=%Sm"` — a WAL born the same second as the first bridge process is a strong correlation.
6. **Growth**: correlate op logs (jsonl timestamps) with WAL mtime; take two size samples 150 s apart to prove idle-flat vs active-growing.
7. **Planner stats**: `SELECT count(*) FROM sqlite_master WHERE name LIKE 'sqlite_stat%'`
   — 0 means ANALYZE never ran. Low impact for point-lookup + FTS5/vec0 workloads.
8. **Resolve count-vs-pages surprises** with
   `SELECT name, sum(pgsize), count(*) FROM dbstat GROUP BY name ORDER BY 2 DESC` and row-content probes (`PRAGMA table_info`, `length()`) BEFORE concluding anything is empty or bloated.

## WAL-never-truncates mechanism (diagnosed 2026-08-06)

- WAL-mode auto-checkpoint (default 1000 pages) is PASSIVE: it copies frames into the db file but only TRUNCATES when a checkpoint reaches the end of the WAL with no other readers. With connection pooling (Microsoft.Data.Sqlite default ON)
  in N long-lived processes, the "last connection closed" close-time truncate never fires and a reader-free window rarely opens → the WAL file grows unbounded with already-checkpointed frames.
- Symptoms: WAL 10–100× the db file, checkpoint tuple `log=0`, file flat when idle.
- Code audit: `grep -rn "wal_checkpoint|wal_autocheckpoint|auto_vacuum|VACUUM|ANALYZE"`
  — absence of hits = no maintenance path. The only TRUNCATE may live in a sync/backup flow that has never run (check its metadata table row count).
- Fix shape: periodic `PRAGMA wal_checkpoint(TRUNCATE)` (hosted-service tick, measured cost ~0) + periodic `VACUUM` (measured 160 ms on a 29.5 MB bank). Measured 2026-08-06: 461 MB on disk (db 29.5 + wal 431.7 + shm 0.8) for 15.4 MB logical
  content = 96.7% overhead.

## vec0 (sqlite-vec) pitfalls

- Chunk tables allocate in **1024-vector capacity units**: one chunk row holds
  `1024 × vector_bytes` (float[384] = 1536 B → rows of 1,572,864 B). `count(*)` on chunk tables counts CHUNK ROWS, not vectors. Check validity bitmaps in `<table>_chunks` and rowid counts in `<table>_rowids` before claiming the vector index
  is empty.
- dbstat page counts for chunk tables are capacity allocation; VACUUM cannot shrink multi-MB blob rows — that is not bloat and must not be reported as such.

## Baseline numbers (AiRaccoon bank, 2026-08-06/07 — for future comparison)

- db: 29,491,200 B / 7200 pages / 4096 B; freelist 3446 (47.9%); payload 40.2%;
  `VACUUM INTO` → 15,368,192 B. 24 tables, 17 indices, auto_vacuum=0, no sqlite_stat.
- WAL: 431,652,432 B, born 19:49:13 the same second as the first bridge processes, 100% checkpointable (`0|0|0`), flat when idle.
- vec0: 1772 entries all `embed_state='embedded'`, 2,721,792 B embedding blobs, 4 chunk rows × 1,572,864 B, 2 of 4 validity bitmaps populated.
- 7 concurrent AiRaccoon processes held the bank (stdio bridges + `serve` + `--quiet`).
