# Research: Usefulness of SQLite's sqlite3_analyzer (sqlanalyze.html) for the AiRaccoon memory bank

**Date:** 2026-08-06 **Question:** How useful is SQLite's sqlanalyze / sqlite3_analyzer utility as a diagnostic for the AiRaccoon project's SQLite databases?

## Findings

### F1 — The tool is a space-utilization analyzer: it measures how efficiently disk space is used per table and index, nothing else [READ]

sqlanalyze.html documents `sqlite3_analyzer`, a command-line program that "measures and displays how much and how efficiently space is used by individual tables and indexes within an SQLite database file". It is implemented as a TCL program
over the `dbstat` virtual table, and its ASCII report is itself valid SQL — the raw data can be loaded into a shell and queried for deeper digging. The report covers page counts, freelist, payload/unused bytes, b-tree depth, fanout,
overflow, and fragmentation per table.

**Evidence:** https://www.sqlite.org/sqlanalyze.html §1 "The sqlite3_analyzer.exe Utility Program" and §1.1 "Implementation" (fetched 2026-08-06, saved at /tmp/sqlanalyze.txt).

### F2 — Running it on the AiRaccoon memory bank immediately surfaced a real, quantified problem: 47.9% of the file is freelist, and VACUUM halves it [MEASURED]

On a `.backup` snapshot of the live bank (`~/.ai-raccoon/memory.db`): 7200 pages (29,491,200 bytes), 3446 freelist pages (47.9%), user payload 11,861,934 bytes (40.2% of the file). A `VACUUM INTO` copy is 15,298,560 bytes — **48.1%
smaller** — with 0% freelist and 77.5% payload efficiency. The snapshot faithfully reflects the live file: read-only `PRAGMA page_count` / `freelist_count` on the live DB return exactly 7200 / 3446. The biggest consumer is `ENTRIES` (10,572
rows, 1962 pages, 33.7% unused bytes, 41.9% non-sequential pages), which matches the sweep/extraction churn the bank undergoes.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db ".backup /tmp/memory-backup.db"` then `/tmp/sqlanalyzer/sqlite3_analyzer /tmp/memory-backup.db` (sqlite3_analyzer 3.53.4, official macOS arm64 tools zip, Apple Silicon MacBook, macOS 26.5.2);
`sqlite3 /tmp/memory-backup.db "VACUUM INTO '/tmp/memory-vacuumed.db'"` re-analyzed (15,298,560 bytes, 0% freelist); live cross-check `PRAGMA page_count; PRAGMA freelist_count; PRAGMA page_size;` → 7200 / 3446 / 4096.

### F3 — It has nothing to say about query performance, indexes' effectiveness for queries, or FTS behavior — the project's actual hot questions [READ]

The documentation contains zero occurrences of "wal" and zero of "performance", "speed", or "slow". The report's only actionable lever is space: VACUUM/auto-vacuum, page-size tuning, overflow. For the hybrid-search / FTS5 / query-plan
questions AiRaccoon cares about, the right tools remain `EXPLAIN QUERY PLAN`, `ANALYZE` + `sqlite_stat1`, and timing — not this one. The one VACUUM mention in the doc concerns root-page relocation after running it, not advice.

**Evidence:** `grep -ic "wal" /tmp/sqlanalyze.txt` → 0; `grep -in "vacuum|performance|speed|slow"` → only the auto-vacuum glossary lines and the VACUUM root-page note.

### F4 — The tool is blind to the WAL, which is where the bank's real disk problem currently lives [MEASURED]

The live bank's footprint is 29.5 MB main file **plus a 431,652,432-byte WAL** (as of 2026-08-07 00:00). The analyzer reads the database file through the pager/dbstat and reports nothing about the sidecar WAL file; nothing in its output
even mentions WAL mode. So the dominant disk-space issue for this deployment (a WAL 14× the size of the database, almost certainly an unconverged checkpoint situation) is invisible to the tool — found only by `ls`.

**Evidence:** `ls -la ~/.ai-raccoon/` → memory.db 29,491,200 bytes, memory.db-wal 431,652,432 bytes (measured 2026-08-07 00:00); doc coverage as in F3 (`grep -ic "wal"` → 0).

### F5 — Acquisition and operation cost are negligible: official zip or Homebrew, one command, ~1 second on this bank [MEASURED]

The official `sqlite-tools-osx-arm64-3530400.zip` (4.29 MB, unsigned binaries needing `xattr -d com.apple.quarantine`) contains `sqlite3_analyzer`; `brew search` also lists a `sqlite-analyzer` formula. Full analysis of the 29 MB bank (24
tables, 17 indices, 2 WITHOUT ROWID, FTS5 shadow tables) completed in about a second. On a live WAL-mode bank the safe pattern is snapshot-then-analyze (`.backup` first), which costs one extra command.

**Evidence:** `curl https://www.sqlite.org/2026/sqlite-tools-osx-arm64-3530400.zip` → 4,494,692 bytes, verified `unzip -l`; `brew search sqlite` lists sqlite-analyzer; `time /tmp/sqlanalyzer/sqlite3_analyzer /tmp/memory-backup.db` finished
sub-second and produced a 1489-line report.

### F6 — Encrypted (SQLCipher) banks are out of reach without a key — untested here, reasoned from how it opens files [INFERRED]

The analyzer opens the database through the normal SQLite file interface, so a SQLCipher-encrypted bank (`SQLite3MC.PCLRaw.bundle` cipher path this project supports) would fail at open unless the key is supplied, which the utility offers no
way to do. This was not tested: the live bank is plaintext (opened fine with the stock `sqlite3`), so the encrypted path remains a corner case. If encrypted-bank analysis is ever needed, the practical route is decrypting to a scratch copy
first.

**Reasoned from:** how SQLite opens database files (pager-level decryption hook required for SQLCipher, not present in stock builds) combined with F2's observed open behavior on the plaintext bank; not exercised end-to-end.

## Still open

- Why is the WAL 431 MB against a 29 MB database — a checkpoint never running, a long-lived reader pinning it, or the served process's PRAGMA settings? This is the larger disk question for this deployment, and it is invisible to this tool.
  Settled by inspecting the app's connection/checkpoint configuration, not by more analysis.
- Whether the ~48% reclaimable space is worth acting on: VACUUM on the live bank needs care (WAL-mode, long-running server); the sqlite-memory library's support for auto_vacuum/incremental_vacuum is unverified. The reclaim number (14.1 MB)
  is small next to the WAL problem.
- Analyzer behavior on a SQLCipher-encrypted scratch bank — not exercised; would confirm or refute F6.
- The jsaa-memory.db test fixture shows 8.4% freelist with a healthy 75.8% payload — minor, but the cause (test churn without vacuum) is unverified.

## Grade mix

Seven findings: three MEASURED (F2, F4, F5), two READ (F1, F3), one INFERRED (F6), one hybrid MEASURED/READ (F4 carries the doc-coverage READ). No UNVERIFIED. The headline numbers (freelist 47.9%, VACUUM → 48.1% smaller, WAL 431 MB) are all
measured on this machine against the live bank; the tool's scope is read from the official doc.
