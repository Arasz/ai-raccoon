# Research: the effect of the two leftovers #790 shipped with

> Corrected 2026-09-28: F4's memory-corpus figures did not predict v2's reach. See the correction under F4.

**Date:** 2026-09-27
**Question:** What does each leftover from #788/#790 — code files the repair still re-reads, and the 100 s limit kept on prune-orphans, stats and noise summary — actually cost or break?

```chart:range
title: report query ms on the live bank snapshot (min..median..max of 3)
prune-orphans report: 4.3..4.4..5.0
noise summary: 0.037..0.040..0.054
```

## Findings

### F1 — The v2 repair re-reads a must-cut code file once per bank, ever, not on every pass [READ]

`ChunkBoundaryRepairJob.Interval` is null, and a null interval means "run exactly once per bank, ever"; the job does not override `HasWorkAsync`, whose default is false. So the re-read happens on the first maintenance pass after upgrading to 1.53.7 and then never again, until someone renames the job to v3.

**Evidence:** `src/AiRaccoon.Infrastructure/Maintenance/ChunkBoundaryRepairJob.cs:24-30`; `src/AiRaccoon.Infrastructure/Maintenance/IMaintenanceJob.cs:17-18,34`.

### F2 — A re-read that writes the same chunks keeps their rows and embeddings [READ]

The repair re-ingests through `ReplaceAsync`, which runs ingest-then-prune. Code rows go in with `INSERT … ON CONFLICT DO NOTHING` keyed on (project, path, hash), so an identical chunk is found again rather than rewritten. Only its position and line range are refreshed. The prune keeps every hash the ingest returned. Nothing new is left `pending`, and hybrid search does not demote the file.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:918-933` (InsertCodeEntry), `:941-944` (UpdateCodeChunkPosition refreshes line range); `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Replace.cs:52-61,138-183`; `src/AiRaccoon.Infrastructure/Ingestion/CodeIngestor.cs:13-14,71,85`.

### F3 — Per affected file, the cost is one disk read, one code-chunker pass, one short write lock and a fingerprint rewrite [INFERRED]

This follows from F2 and from `ReplaceCoreAsync`. The chunker runs outside the lock. `BEGIN IMMEDIATE` covers only the prune and the `watch_files` upsert. Because the job now counts only changed files (`ChunkBoundaryRepair.cs:163-172`), `RunAsync` returns false for a pure re-read, so it no longer triggers the extra embed sweep the maintenance pass runs on `true` (`IMaintenanceJob.cs:20-25`, `ChunkBoundaryRepairJob.cs:38`). No user-visible behaviour changes. The re-read costs some CPU and I/O, once.

### F4 — On the live bank, no code files would be re-read at all [MEASURED]

In the code corpus, 0 of 30,993 adjacent chunk pairs (5,484 files) end and begin inside a term, which is the `ChunkSeam.CutsATerm` test that gates the re-read. The same detector does fire on the memory corpus, with 5 seams in 3 files (two lighthouse JSON reports and one plan), so it is not vacuous. The live ledger still shows only `chunk-boundary-repair-v1` stamped, which means v2 has not run here yet. When it does run, the code pass will re-read nothing on this bank.

**Correction (2026-09-28): the memory-corpus half of F4 understated what v2 would do.** The code
corpus result stands. But "5 seams in 3 files" is not a prediction of how many memory files v2
repairs, and it was read as one. On the live bank, v2 ran one second after the 1.53.7 restart
(00:05:22) and re-chunked 180 files (9,361 new rows, including a 50 MB `TestResults` log with
5,811 rows) and renumbered 44 more. It wrote 0 new code rows, as F4 measured. A replay on a copy of the 23:31 pre-upgrade backup gave the
same numbers. This finding counted only mid-term cuts (`CutsATerm`). v2 also selects any file whose
stored positions `ChunkPositionScanner.Moves(partition, null)` would change, which is the
duplicate-position shape #788 fixed, and that is how 174 of the 180 were chosen. A second v2 run
changes nothing. Source: the 1.53.7 release checklist,
`docs/work/checklist/2026-09-28-1.53.7-release.json`.

**Evidence:** `sqlite3 "file:~/.ai-raccoon/memory.db?mode=ro" ".backup <scratch>/bank.db"` on an Apple M4, 2026-09-27 23:31. Then a Python port of `CutsATerm` (`str.isalnum() or '_'` per `TokenBudget.IsTermChar`, `src/AiRaccoon.Core/Chunking/TokenBudget.cs:61`; `ChunkSeam.cs:7-13`) over `code_entries`, grouped by (project_id, path) and ordered like `ChunkBoundaryRepair.cs:161`. Ledger: `select * from maintenance_jobs where name like 'chunk-boundary%'` gives `chunk-boundary-repair-v1|1790525818|1`.

### F5 — The three reports that keep the 100 s deadline answer in milliseconds on a 900 MB bank [MEASURED]

- **Prune-orphans report:** one `NOT EXISTS` probe per queue row, took 4.3–5.0 ms (1,000 queue rows against 72,590 entries).
- **Noise summary:** a `GROUP BY` over `noise_entries`, which has 0 rows here, took 0.04–0.05 ms.
- **Stats pragmas:** the last timed pragma took 0.012–0.015 ms.

At these timings the 100 s limit is more than four orders of magnitude away from anything these reports do.

**Evidence:** `sqlite3 <scratch>/bank.db` with `.timer on`, 3 runs each, same snapshot and machine as F4. The SQL was copied from `PromotionQueueSql.cs:93-100` (with `ProjectRows.Scopes = ('project','custom')`, `ProjectRows.cs:12,18`), `NoiseEntrySql.cs:11` and `SqliteMaintenanceStatsStore.cs:17-41`.

### F6 — The orphan report is bounded by the queue capacity, not the bank size [READ]

The promotion queue's default capacity is 1,000, and the live queue holds exactly 1,000. The report's work therefore grows with the capacity setting, not with the number of entries. Each probe hits `idx_entries_hash`. The report also runs in a deferred read transaction, which WAL does not make wait behind a writer.

**Evidence:** `src/AiRaccoon.Core/Memory/ExtractionConfigKeys.cs:22`; `src/AiRaccoon.Infrastructure/Sqlite/SqlitePromotionQueueStore.cs:239-258`; `idx_entries_hash` from `sqlite_master` on the snapshot.

### F7 — The stats report's one unbounded step is a PASSIVE checkpoint, whose cost follows WAL size [INFERRED]

`GetStatsAsync` runs `PRAGMA wal_checkpoint(PASSIVE)` (`SqliteMaintenanceStatsStore.cs:45-52`). A PASSIVE checkpoint never waits on readers or writers, but it copies every frame it can into the database file, so its time grows with the backlog it has to copy. The live WAL is 4.7 MB. This inference comes from SQLite's documented PASSIVE semantics and that file size; the checkpoint was not timed, because the snapshot has no WAL (see Still open). A checkpoint needs a WAL in the gigabytes before 100 s becomes plausible, and the maintenance loop checkpoints on its own schedule.

### F8 — When the limit does fire, the message still blames an absent server [READ]

A report that exceeds the deadline surfaces as `SettingsServerUnavailableException`, which reads as "no settings server answered". That is the misleading wording #790 fixed for the three repair reports, and it remains for these three. So the cost of this leftover is a wrong diagnosis if the server stalls, not a false failure on a healthy one. A server that fails to answer these queries within 100 s really is unwell, so failing is the right outcome; only the wording is wrong.

**Evidence:** `src/AiRaccoon/Settings/ServerSettingsStore.cs:181-209` (the three calls pass no deadline, so they get `_requestDeadline`), `:221-229`; `src/AiRaccoon/Settings/CliSettingsBackend.cs:23,93,97-101`; commit `4e7699f2` message ("reported as 'no settings server answered' while the server was alive").

## Still open

- **The PASSIVE checkpoint in `stats` was not timed against a real WAL.** The snapshot copy has no WAL, so its checkpoint returned `0|0|0`. To settle F7, time `PRAGMA wal_checkpoint(PASSIVE)` on a bank copy that has its `-wal` file attached, ideally one bloated to around 1 GB.
- **The re-read cost per code file on a bank that does have must-cut code files is unmeasured.** This bank has none (F4). Settling it needs a bank with a whitespace-free identifier longer than the code chunk budget.
- **Server-side queueing ahead of these handlers was not examined.** For example, a request may wait on a maintenance pass holding the only bank connection or a startup schema step. That is the only path by which F5's millisecond queries could approach 100 s.
