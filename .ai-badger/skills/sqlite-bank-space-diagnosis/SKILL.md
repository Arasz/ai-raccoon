---
name: sqlite-bank-space-diagnosis
description: "Use when a SQLite bank file or WAL is bloated: diagnose space read-only first (snapshot backup, sqlite3_analyzer, wal_checkpoint(TRUNCATE), VACUUM INTO to quantify reclaim), explain WAL growth mechanics (checkpointed-but-untruncated frames under pooling), the vec0 chunk count(*) trap, and VACUUM/checkpoint/ANALYZE ordering."
platforms: [macos, linux]
scope: optIn
metadata:
  hermes:
    tags: [sqlite, wal, vacuum, disk-space, diagnostics]
    related_skills: [evidence-first-research]
version: 1.0.0
author: ai-badger
license: MIT
---

# SQLite bank space & WAL diagnosis (physical layer)

Measured methods from a 2026-08 memory-bank audit. Complements the SQLite schema
review skill (schema semantics); this covers the physical layer — file size, WAL
mechanics and VACUUM semantics.

## Diagnosis workflow (read-only first)

1. **Snapshot before touching anything**: `sqlite3 <bank> ".backup /tmp/snap.db"` — the backup preserves page layout (freelist + page counts matched the live file exactly; cross-check with read-only
   `PRAGMA page_count/freelist_count/page_size`).
2. **sqlite3_analyzer** (official `sqlite-tools-osx-arm64-*.zip`, or
   `brew install sqlite-analyzer`): space utilization per table — freelist %, payload efficiency, non-sequential pages, overflow. It is a SPACE tool ONLY: zero coverage of query performance, the WAL file, or FTS behavior (the doc page
   contains no "wal"/"performance" mentions — grep before claiming otherwise).
3. **WAL check**: `PRAGMA wal_checkpoint(TRUNCATE)` on the SNAPSHOT (never the live bank mid-analysis). The result row `busy|log|checkpointed` is the signal:
   `0|0|0` = the whole WAL was already-checkpointed garbage and truncation is instant;
   `log > 0` = real checkpoint debt (unreplayed frames).
4. **Quantify reclaim**: `VACUUM INTO '/tmp/vac.db'` on the snapshot → logical content size; the freelist share of the main file is the reclaimable delta.
5. **Footprint math**: stat the WHOLE data dir — db + `-wal` + `-shm`. A 431 MB WAL next to a 29 MB db with `log=0` is 100 % reclaimable garbage, and the analyzer cannot see it.

## WAL growth mechanics (why a WAL is huge)

- Frames accumulate until a checkpoint TRUNCATEs. Passive auto-checkpoints (default 1000 pages) keep content synced (`log=0`) but CANNOT truncate while any other connection holds a read lock; with connection pooling + several long-lived
  processes (stdio bridges, serve) the "last connection closed" close-time truncation never fires → the WAL grows unbounded with already-checkpointed frames.
- Measured: 431 MB WAL born the second the first bridge processes started, grown in
  ~4 h of traffic; `0|0|0` on the snapshot; TRUNCATE → 0 bytes in under a second.
- If the app has a checkpoint only inside a feature that never ran (e.g. a cloud-sync path), that is the root cause of unbounded growth — grep the codebase for
  `wal_checkpoint` before designing a fix.

## vec0 chunk storage — the count (*) trap

vec0 stores vectors in chunk tables (`vec_entries_vector_chunks00`, schema
`(rowid, vectors BLOB)`). ONE chunk row holds capacity for **1024 vectors**: for
`float[384]` that is exactly 1,572,864 bytes per row. So "4 rows, 1540 pages" is NOT an emptied index — it is 2 full + 2 partial chunks for ~1772 vectors (~43 % slot utilization; `vec_entries_chunks` validity bitmaps mark live slots).
Before concluding "vectors deleted/empty": check dbstat per-table page counts (`SELECT name, sum(pgsize), count(*) FROM dbstat GROUP BY name`), `SELECT rowid,
length(vectors)` (capacity math must check out), and the validity rows. This misread nearly shipped as a false finding (2026-08-07).

## SQLite semantics that matter here

- **WAL mode: a READ transaction does NOT block VACUUM** (writers and readers coexist); only the single WRITE lock does — hold `BEGIN IMMEDIATE` on another connection to simulate VACUUM-busy in tests (a plain `BEGIN` + SELECT will not do).
- VACUUM rewrites the whole file **through the WAL** → a maintenance pass should checkpoint AGAIN after VACUUM; and ANALYZE must run AFTER VACUUM (VACUUM drops
  `sqlite_stat1`).
- VACUUM under contention throws `SQLITE_BUSY` (5) / `SQLITE_LOCKED` (6) after the busy timeout — catch and treat as defer (Warning), never an Error.
- `PRAGMA wal_checkpoint` returns a ROW — `ExecuteNonQueryAsync` discards it; read the tuple (`GetInt32(0)` = busy).
- Per-connection PRAGMAs (`busy_timeout`) survive pool return — restore the factory default before disposal or the next borrower inherits them.
- `TimeSpan.FromDays` overflows for intervals > ~10.7 M days — clamp parse inputs.
- Absent ANALYZE → zero `sqlite_stat*` tables (planner heuristics; low impact for point-lookup + FTS5/vec0 workloads — those manage their own indexes).

## Maintenance fix pattern (bounded footprint)

A BackgroundService in EVERY process that opens the bank: startup checkpoint (bounds the WAL for short-lived processes — their only maintenance), `StopAsync` final best-effort checkpoint, periodic timer (hourly) `wal_checkpoint(TRUNCATE)`,
and VACUUM + ANALYZE on a weekly cadence with an in-memory per-process clock (short-lived processes never vacuum). Busy → defer + retry next tick. Settings-driven intervals with safe fallbacks. Measured result: 461 MB → ~16–20 MB steady
state; TRUNCATE
~0 s, VACUUM ~160 ms on a 29 MB bank. Implementation reference: the project PR #79 (`BankMaintenanceHostedService`).

## Gotchas

- The vec0 chunk `count(*)` trap: vec0 tables count chunks, not rows — quantify reclaim with sqlite3_analyzer, not count.
- Order matters: VACUUM INTO quantifies reclaim read-only; checkpoint(TRUNCATE) only truncates WAL frames.
