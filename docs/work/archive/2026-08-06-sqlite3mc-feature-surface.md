# Research: SQLite3MC 2.4.0 / SQLite 3.53.4 feature surface — what AiRaccoon can use

**Date:** 2026-08-06 **Question:** Which new SQLite and SQLite3MC capabilities does the SQLite3MC.PCLRaw.bundle 2.4.0 + SQLitePCLRaw.core 3.x upgrade make available to AiRaccoon, and which of them should the project adopt?

Extends `docs/work/2026-08-06-sqlite3mc-2.4.0-upgrade.md` (package-level upgrade record, encryption focus). This record maps the new SQLite 3.49.1 → 3.53.4 feature surface to the app's actual usage.

## Findings

### F1 — The bundle is SQLite 3.53.4 with SQLite3MC 2.4.0, and the app's provider wiring is unaffected [MEASURED]

A probe console app pinned to the repo's exact versions (SQLite3MC.PCLRaw.bundle 2.4.0, SQLitePCLRaw.core 3.0.5, Microsoft.Data.Sqlite.Core 10.0.10, HiraokaHyperTools.sqlite-vec 0.1.9) reports libversion 3.53.4, source id
`2026-07-24 bf7c7f30…` (matches the 3.53.4 changelog), `sqlite3mc_version()` → "SQLite3 Multiple Ciphers 2.4.0". The app's reflection-based provider init (SqliteEncryptionInit) works unchanged.

**Evidence:** `/tmp/sqlite3mc-features` probe (this Mac, arm64, net10.0, `dotnet run`), run 2026-08-06; `raw.sqlite3_libversion()`, `SELECT sqlite3mc_version()`.

### F2 — The 3.53.0 WAL-reset corruption fix covers the app's exact risky paths [READ]

3.53.0's headline fix is the WAL-reset database corruption bug. AiRaccoon exercises both affected shapes: SyncService runs `PRAGMA wal_checkpoint(TRUNCATE)` on every sync, and RekeyBankAsync switches journal modes. The probe re-ran WAL +
`wal_checkpoint(TRUNCATE)` and `PRAGMA rekey` on an encrypted bank under 2.4.0 — both complete cleanly.

**Evidence:** sqlite.org/changes.html `version_3_53_0` ("Fix the WAL-reset database corruption bug"); probe output "encrypted: WAL+checkpoint -> wal" and "rekey in WAL: ok". App paths:
`src/AiRaccoon.Infrastructure/Sync/SyncService.cs:49-56` and `SqliteConnectionFactory.cs:122-124`.

### F3 — The encrypted-bank temp-file leak is already closed: TEMP_STORE=2 is compiled in [MEASURED]

SQLite3MC's docs warn that TEMP tables and `:memory:` databases are not encrypted and recommend `SQLITE_TEMP_STORE=2/3` (or `PRAGMA temp_store=MEMORY`). The bundle is compiled with `TEMP_STORE=2` — temp b-trees and spill files stay in
memory by default — so an encrypted bank never spills plaintext intermediates to disk. `PRAGMA temp_store` reports 0 (unset), but the effective setting is the compile default = memory. No code change needed; do not add a temp_store pragma.

**Evidence:** probe `PRAGMA compile_options` (55 options, includes `TEMP_STORE=2`, `SECURE_DELETE`) and `PRAGMA temp_store` → 0. Recommendation source: https://utelle.github.io/SQLite3MultipleCiphers/ (Overview, "Technical functionality").

### F4 — JSONB functions are fully available; the app has no JSON consumer [MEASURED]

`jsonb()`, `jsonb_each()` (3.51), `jsonb_tree()` (3.51), `json_array_insert()` (3.53) all execute. This holds despite `compile_options` showing no `ENABLE_JSONB` define — the functions are present in this build regardless (mechanism
unverified, see Still open). The codebase uses no JSON functions and stores no JSON columns today (search over src for json_*/jsonb*: zero hits), so there is nothing to adopt; the capability is there if settings or entries ever grow a JSON
payload.

**Evidence:** probe outputs `jsonb(): OK -> 1`, `jsonb_each: OK -> 2`, `jsonb_tree: OK -> 4`, `json_array_insert: OK -> [1]`; `search_files` over `src/` for `json_each|json_extract|jsonb|fts5|…` returned only FTS5/vec0/sync matches.

### F5 — Math and percentile functions are compiled in, but the SQL-standard WITHIN GROUP form is not [MEASURED]

`sqrt()`, `median()`, and `percentile_cont(Y, P)` (simple two-argument form) work; `percentile_cont(P) WITHIN GROUP (ORDER BY Y)` fails with a syntax error because that syntax needs `SQLITE_ENABLE_ORDERED_SET_AGGREGATES`, which is absent.
Usable if stats ever need percentiles (e.g. memory_stats latency distributions) — simple form only.

**Evidence:** probe outputs `math sqrt: OK -> 4`, `percentile median: OK -> 2`, `percentile_cont(Y,P) simple form: OK -> 2`, WITHIN GROUP form → "near \" (\": syntax error". Syntax requirement: https://www.sqlite.org/percentile.html.

### F6 — SQLite 3.53 ALTER TABLE constraint editing works and enforces [MEASURED]

The new in-place constraint operations are real: `ALTER TABLE t ALTER COLUMN c SET NOT NULL` (subsequent NULL insert fails with "NOT NULL constraint failed"), `ALTER COLUMN c DROP NOT NULL` (NULL insert then succeeds),
`ALTER TABLE t ADD CONSTRAINT name CHECK (expr)` ("CHECK constraint failed" on violation), `DROP CONSTRAINT name`. Migrations can now tighten or relax column constraints without a table rebuild.

**Evidence:** probe `ProbeSeq` runs (per-statement, enforcement verified); syntax from https://www.sqlite.org/lang_altertable.html §6 ("ALTER TABLE ALTER COLUMN", added in 3.53.0). Wrong guesses (`ALTER TABLE … ADD CHECK`, `DROP NOT NULL`
bare) are syntax errors — the ALTER COLUMN / ADD CONSTRAINT forms are the real ones.

### F7 — vec0 0.1.9 pairs cleanly with SQLite 3.53.4 through the app's init path [MEASURED]

`CREATE VIRTUAL TABLE … USING vec0(embedding float[2])` + insert + KNN `MATCH … k = 2` query all succeed when the probe replicates the app's `EnableExtensions()` + `LoadVector()` sequence. The pinned sqlite-vec build is compatible with the
new SQLite core; keep the existing pin-and-reharness convention.

**Evidence:** probe `vec0 create+insert+knn: OK -> 1` with `vec: true` (LoadVector). App init: `SqliteConnectionFactory.cs:49-51`.

### F8 — The session/changeset extension is unreachable from managed code [MEASURED]

`ENABLE_SESSION` is compiled in, but Microsoft.Data.Sqlite 10.0.10 exports no session or changeset API (reflection over the assembly: zero types matching "Session"). The upgrade doc's speculative "delta sync via session API" would require
hand-written P/Invoke wrappers over `sqlite3session_*` — a real project with no consumer, and the rsync evaluation (2026-08-06) showed delta sync is not needed at the 29.5 MB bank scale. Close this line.

**Evidence:** probe reflection dump "Microsoft.Data.Sqlite session/changeset API types: 0"; `ENABLE_SESSION` present in `PRAGMA compile_options`.

### F9 — VACUUM INTO snapshots of an encrypted bank are themselves encrypted [MEASURED]

The sync snapshot path leaks nothing: a snapshot taken from a keyed bank fails to open without the key (SQLITE_NOTADB, 26) and opens normally with it. Uploaded snapshots from encrypted installs are ciphertext on the object store.

**Evidence:** probe "snapshot WITHOUT key: FAIL as expected -> SQLite Error 26" / "snapshot WITH key: 1 rows". This closes the in-flight test item from the upgrade doc ("sync checkpoint on an encrypted bank").

### F10 — VLE, AEGIS/Ascon, and WAL-rekey decisions from the upgrade doc stand [READ]

The upgrade record already evaluated Value Level Encryption (verified working, reserved for a future plaintext-bank + protect-secrets mode), the additional AEGIS/Ascon ciphers (not adopted; chacha20 remains default), and WAL-mode rekey
(supported but keeps the previous salt; the app keeps the fresh-salt DELETE path). The probe re-confirmed WAL rekey completes. No change to those decisions.

**Evidence:** `docs/work/2026-08-06-sqlite3mc-2.4.0-upgrade.md` §"What we can use from 2.4.0"; probe "rekey in WAL: ok".

## What to use

- **Adopt now: nothing new.** The upgrade's value is already delivered: WAL-reset fix on the exact paths the app runs (F2), CVE removal, temp-in-memory for encrypted banks (F3), secure-delete page zeroing (F3 compile options), vec0
  compatibility (F7), encrypted snapshots (F9). There is no new capability that needs wiring today.
- **Use when a need appears:** 3.53 ALTER constraint ops for future migrations instead of table rebuilds (F6); jsonb family if a JSON payload consumer arrives (F4); `percentile_cont(Y,P)` simple form for stats (F5); `sqlite3mc_version()` in
  diagnostics/version output (trivial, F1).
- **Do not use:** VLE (no consumer), AEGIS/Ascon (chacha20 fine), session/changeset (unreachable from Microsoft.Data.Sqlite — close the delta-sync idea, F8), WITHIN GROUP percentile syntax (compile option absent, F5).
- **No new PRAGMAs:** temp_store is already handled at compile level (F3); don't add cipher pragmas (banks self-describe).

## Still open

- Why `jsonb()` works without `ENABLE_JSONB` in `compile_options` — mechanism unverified (the functions demonstrably execute; only the build-flag explanation is unknown).
- Whether SQLite3MC 2.5.0 (published on GitHub 2026-08-02, not yet on NuGet) changes any of this — untested; per the upgrade doc, bump when it lands on NuGet.
- The session-extension P/Invoke path was not attempted; it remains possible in principle, sized as a real project, with no consumer.

Grade mix: 9 measured, 1 read.
