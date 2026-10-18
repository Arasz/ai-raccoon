# Research: SQLite re-preparing statements in the middle of memory_search

**Date:** 2026-10-18
**Question:** Why does a CPU sample of concurrent memory_search FTS queries show `sqlite3Reprepare` under `sqlite3_step`, does it cost search latency, and what should be done about it?

```chart:range
title: FTS query µs (p10..p50..p90, 300 runs each)
no pragma, run 1: 846..874..968
pragma before, run 1: 818..842..880
no pragma, run 2: 832..848..888
pragma before, run 2: 827..862..1086
```

## Findings

### F1 — Every bank open runs `PRAGMA foreign_keys = ON`, including opens that reuse a pooled connection [READ]

`OpenWithPragmasAsync` runs three pragmas after `OpenAsync`: `foreign_keys = ON`, `journal_mode=WAL`, `busy_timeout=5000`. Pooled opens go through it too, so the statement runs once per search's bank open.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:566-582`. In the probe's statement profile, `PRAGMA foreign_keys = ON` ran once per open: 6 times for 6 single searches, and 41 times in the concurrent rounds.

### F2 — Setting `foreign_keys` expires every prepared statement on the connection, even when the value does not change [MEASURED]

A statement was prepared and stepped once. One pragma then ran, the statement was stepped again, and `sqlite3_stmt_status(SQLITE_STMTSTATUS_REPREPARE)` was read. `busy_timeout=5000` gave 0, `journal_mode=WAL` gave 0, `foreign_keys = ON` (already ON) gave 1, and reading `foreign_keys` gave 0. So the write form of the pragma is what forces the re-prepare.

**Evidence:** temporary xUnit probe (since deleted) in the `air-parallel-memory-search` worktree. It used a 1,500-entry scratch bank from `TestData.CreateInfrastructureOptions` and `SqliteConnectionFactory` with `NullKeyProvider`. Calls were raw `SQLitePCL.raw.sqlite3_prepare_v2` / `sqlite3_exec` / `sqlite3_stmt_status(st, 5, 0)` on `connection.Handle`. Machine: Apple Silicon laptop (arm64), macOS, Debug build, `dotnet exec AiRaccoon.Tests.dll --filter-class`.

### F3 — The re-prepares inside `sqlite3_step` are FTS5's own cached statements [INFERRED]

The search's statement profile is dominated by FTS5's internal statements, which FTS5 prepares once and keeps for reuse. `SELECT sz FROM 'main'.'entries_fts_docsize' WHERE id=?` ran about 1,500 times per search, and `SELECT pgno FROM 'main'.'entries_fts_idx' ...` about 935 times. FTS5 steps them from inside the outer query's step. After F2's expiry, their first use re-prepares there. That matches a `sqlite3Reprepare` frame under `sqlite3_step`. This reasons from F1, F2, the measured profile (sqlite3_profile hook, same probe), and the agent's `sample` stack reported in PR #879. No stack was captured that names the statement being re-prepared.

### F4 — No schema change happens during searches, so SQLITE_SCHEMA is not a second cause [MEASURED]

`PRAGMA schema_version` read 95 before the searches, 95 after 6 single searches, and 95 after 6 rounds of 8 concurrent searches. No search-path step rewrites the schema, so no connection sees a schema mismatch. The build also has no `ENABLE_STAT4` (`PRAGMA compile_options` lists only FTS3/4/5 and LIKE options). That rules out STAT4's re-prepare when a bound value changes.

**Evidence:** same probe, same machine. Read `schema_version` through a fresh `OpenBankAsync` before and after each round set, and `PRAGMA compile_options` once.

### F5 — The re-prepare costs no measurable latency [MEASURED]

On one connection, a bm25 FTS query (`MATCH 'widget OR shelf OR memory OR search' ORDER BY r LIMIT 50`) ran 300 times without the pragma and 300 times with `PRAGMA foreign_keys = ON` before each run. The order was alternated and repeated twice. p50 was 874 / 848 µs without the pragma and 842 / 862 µs with it. That difference is inside the run-to-run spread, roughly ±30 µs on about 850 µs. A live search totals 200–300 ms (memory_performance `search.total` p50 197 ms on the live bank), so even a several-hundred-µs re-prepare would be under 1%.

**Evidence:** same probe, same machine. Stopwatch around prepare + step-to-done + finalize; the laptop was under load from other sessions. A second run gave 838 / 833 µs without the pragma and 836 / 838 µs with it.

### F6 — Recommendation: leave it [INFERRED]

From F5, there is no latency to recover. A fix is cheap if the frame becomes noise in future profiling: read `PRAGMA foreign_keys` and set it only when it is 0, since a read does not expire statements (F2). A pooled connection keeps the flag, so after the first open this would never write. It is not worth a release on its own.

### F7 — Microsoft.Data.Sqlite's `Foreign Keys=True` connection-string option would behave the same way [UNVERIFIED]

Moving the pragma into the connection string looks like an alternative fix. Whether Microsoft.Data.Sqlite writes the pragma on every pooled open was not checked.

## Still open

- Larger banks were not measured. F5 used 1,500 entries. The re-prepare cost depends on the statement, not the row count, so it should not grow with the bank, but nobody has measured it on the live bank (a 949 MB `memory.db`).
- F3 rests on inference. A `sample` with symbols that shows which statement `sqlite3Reprepare` compiles, or a counter on FTS5's internal statements, would settle it.
- F7: read the Microsoft.Data.Sqlite 10.0.13 `SqliteConnection.Open` source before choosing between the connection-string option and a read-then-set guard.
