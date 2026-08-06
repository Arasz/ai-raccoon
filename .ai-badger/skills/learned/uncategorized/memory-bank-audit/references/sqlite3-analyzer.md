# sqlite3_analyzer — space-utilization audit of the memory bank

Tool: `sqlite3_analyzer` (https://www.sqlite.org/sqlanalyze.html — "The sqlite3_analyzer.exe Utility Program"). A TCL program over the `dbstat` virtual table; measures how efficiently disk space is used per table/index. The report is also
valid SQL — the raw data can be loaded into a shell and re-queried.

## Scope (read from the doc, 2026-08-06)

- Covers: page counts, freelist, payload/unused bytes, b-tree depth, fanout, overflow, non-sequential pages (fragmentation), per table and per index.
- Does NOT cover: query performance or index effectiveness for queries (doc: 0 hits for "performance|speed|slow"), and the WAL file (doc: 0 hits for "wal"). For query questions use EXPLAIN QUERY PLAN / ANALYZE + sqlite_stat1 instead.

## Getting the binary (sqlite.org anti-robot links)

- download.html hrefs are JS-injected (`d391(...)` calls in a setTimeout) — plain `grep href` finds nothing. But the page embeds a machine-readable comment: `PRODUCT,VERSION,RELATIVE-URL,SIZE-IN-BYTES,SHA3-HASH` CSV (first line `PRODUCT,`),
  stable format, documented on the page.
- URL pattern: `https://www.sqlite.org/<year>/sqlite-tools-<os>-<arch>-<ver>.zip`, e.g. `2026/sqlite-tools-osx-arm64-3530400.zip` (macOS arm64, ~4.3 MB; also linux-x64, osx-x64, win-x64/arm64).
- The zip extracts FLAT — no subdirectory; the binary is `sqlite3_analyzer` at the zip root.
- Binaries are unsigned; if downloaded via browser, `xattr -d com.apple.quarantine sqlite3_analyzer` first.
- Alternative: `brew install sqlite-analyzer`.

## Safe workflow on a live WAL-mode bank

1. Snapshot: `sqlite3 ~/.ai-raccoon/memory.db ".backup /tmp/memory-backup.db"` — `.backup` preserves file layout INCLUDING the freelist (measured: identical to live, 7200 pages / 3446 freelist / 4096 page size). Do not `VACUUM INTO` for the
   snapshot — that packs the file and hides the fragmentation you are auditing.
2. Sanity-check the snapshot: `sqlite3 /tmp/memory-backup.db "PRAGMA integrity_check;"` → ok.
3. Analyze: `/path/sqlite3_analyzer /tmp/memory-backup.db > report.txt` — ~1 s on a 29 MB bank; tee the full report (1489 lines at this bank size).
4. Quantify reclaim: `sqlite3 /tmp/memory-backup.db "VACUUM INTO '/tmp/memory-vacuumed.db';"` then compare sizes and re-analyze the vacuumed copy (freelist → 0, payload % rises).
5. Cross-check live, read-only, no lock: `sqlite3 ~/.ai-raccoon/memory.db "PRAGMA page_count; PRAGMA freelist_count; PRAGMA page_size;"` — safe against a busy writer.

## Baseline measured 2026-08-06 (live bank ~/.ai-raccoon/memory.db, analyzer 3.53.4, macOS arm64)

- 29,491,200 bytes; 7200 pages; freelist 3446 (47.9%); user payload 11,861,934 bytes (40.2%); 24 tables, 17 indices (2 WITHOUT ROWID).
- VACUUM INTO → 15,298,560 bytes (−48.1%), 0% freelist, 77.5% payload.
- ENTRIES (core table): 10,572 rows, 1962 pages total, 33.7% unused bytes, 41.9% non-sequential pages, avg payload 497.7 B/entry, 3-level b-tree.
- Sidecar: memory.db-wal 431,652,432 bytes — 14× the main file; invisible to the analyzer (checkpoint/reader question, separate investigation).
- Other DBs for comparison: tests jsaa-memory.db fixture 8.4% freelist / 75.8% payload; ~/.hermes/memory_store.db 0% freelist (32 pages).

## Pitfalls

- Encrypted (SQLCipher) banks: the analyzer opens through the normal pager and offers no key hook — a SQLCipher bank fails at open. Not tested end-to-end (reasoned from pager design); decrypt to a scratch copy first if ever needed.
- Analyze the `.backup`, never a vacuumed copy, when the question is "what is the live bank wasting" — vacuuming destroys the evidence.
- The tool reads the db file through the pager, so committed WAL content is included, but WAL SIZE is never reported — a bloated WAL beside a small DB is the classic blind spot.
