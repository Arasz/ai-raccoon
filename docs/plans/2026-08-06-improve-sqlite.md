# Plan: improve-sqlite — act on the SQLite3MC 2.4.0 / SQLite 3.53.4 feature-surface research

**Task:** improve-sqlite · **Date:** 2026-08-06 · **Branch:** task/improve-sqlite

## Context

`docs/work/2026-08-06-sqlite3mc-feature-surface.md` measured the bundle's surface (9 MEASURED findings, probe at /tmp/sqlite3mc-features). Its "What to use" verdict: no new runtime wiring needed; the value is (a) making the measured capability facts executable as tests, (b) one diagnostics addition, (c) closing the speculative session-API item, (d) covering the upgrade doc's in-flight test item (sync checkpoint on an encrypted bank — still absent from the suite). This plan implements exactly those.

Delegation note: only the deepseek provider is configured in this environment; the persona lanes (architect/code-reviewer = opus, engineer = sonnet) are unreachable. Plan/review run in-session at the orchestrator model (reduced rigor, per task skill); all objective gates (build 0 warnings, full suite, RED→GREEN) are unchanged.

## Work packages (independent, all in the task worktree)

### WP1 — SQLite engine capability contract tests (TDD)
Pin the measured surface so a future bundle swap cannot silently regress it (the 2.1.11→2.4.0 swap broke exactly this class of assumption).

- New file: `tests/AiRaccoon.Tests/Unit/storage/SqliteEngineCapabilityTests.cs`, traits `Category=Unit, Speed=Fast` (all `:memory:`/temp-file based, fast — runs in build.yml's Speed=Fast gate).
- Facts pinned (each its own [Fact], from the probe; floor assertions so patch bumps don't churn CI, but a major/minor regression fails):
  1. `raw.sqlite3_libversion()` >= 3.53.4 (the WAL-reset-fix floor the app now depends on).
  2. `SELECT sqlite3mc_version()` starts with "SQLite3 Multiple Ciphers 2.4".
  3. `PRAGMA compile_options` contains `TEMP_STORE=2` (encrypted banks never spill temp to disk) and `SECURE_DELETE` and `ENABLE_FTS5` and `ENABLE_PERCENTILE`.
  4. `jsonb()` and `jsonb_each()` execute (JSONB surface available).
  5. `median()` and `percentile_cont(x, 0.5)` simple form execute.
  6. 3.53 ALTER surface: `ALTER COLUMN … SET NOT NULL` enforces (NULL insert throws), `ALTER COLUMN … DROP NOT NULL` allows NULL, `ADD CONSTRAINT … CHECK` enforces, `DROP CONSTRAINT` removes.
  7. STRICT table rejects a wrong-typed value.
  8. vec0 pairing: `CREATE VIRTUAL TABLE … USING vec0(embedding float[2])` + KNN query works via the app's `EnableExtensions()+LoadVector()` sequence (HiraokaHyperTools flows transitively from Infrastructure).
- TDD honesty note: these are contract tests for already-measured behavior — they pass immediately; the RED evidence is the probe's recorded failures on the wrong forms (WITHIN GROUP syntax, bare `ADD CHECK`/`DROP NOT NULL`), referenced in the record.

**AC:** all facts green on the current bundle; Speed=Fast gate green.

### WP2 — Engine diagnostics at server startup (TDD)
Add one `[LoggerMessage]` line (per the high-performance-logging invariant): "ai-raccoon: SQLite engine {LibVersion} ({EngineVersion})" — libversion + sqlite3mc_version, Information level, in the existing startup path (HostExtensions next to `HttpTransportListening`, or Program.cs Log).

- `--version` output deliberately untouched: its shape is pinned by `scripts/manual-fresh-install-test.py` (AI_RACCOON_VERSION) and CliOutputRoutingTests — changing it is a contract change with no buyer.
- TDD: extend the host-startup test that pins `HttpTransportListening` (find it; pattern exists) to also assert the engine line contains a real version (>= 3.53).

**AC:** log line present in live smoke (`ai-raccoon --port 0` stderr) with real versions; unit test green.

### WP3 — Sync checkpoint on an encrypted bank (TDD)
The upgrade doc's in-flight item. New test (in `SyncServiceTests` or a sibling file): open the bank with `Password=…`, run `MemorySyncAsync` against `FakeCloudStore`, then assert:
- the push succeeded and the cloud snapshot cannot be opened without the key (SQLITE_NOTADB) and opens with it (encrypted snapshots stay encrypted — measured F9),
- rows round-trip through pull/merge.
SyncService's `openBank` delegate makes this a one-test change; `wal_checkpoint(TRUNCATE)` runs inside the sync path, so the test exercises the WAL-reset-fix-adjacent path on an encrypted bank.

**AC:** test green; full suite green.

### WP4 — Docs closure (no code)
- `docs/work/2026-08-06-sqlite3mc-2.4.0-upgrade.md` item 5 (ENABLE_SESSION delta-sync "speculative"): mark CLOSED — the session API is unreachable from Microsoft.Data.Sqlite 10.0.10 (measured F8); pointer to the feature-surface record.
- Same doc's "Tests (in flight)" line: update to reflect landed coverage (encrypted open/wrong-key/rekey already exist in SqliteConnectionFactoryEncryptionTests / EncryptionBitwardenIntegrationTests; checkpoint covered by WP3).

**AC:** docs consistent with code; no test impact.

## Order & gates

1. WP1 → 2. WP3 → 3. WP2 → 4. WP4. Each WP: tests first (WP1/WP3/WP2), commit per WP.
2. Final gates (orchestrator-run, in the worktree): `dotnet build` 0 warnings; full `dotnet test` (expect ~1309 passed / 0 failed / 4 pre-existing spec skips); WP2 live smoke.
3. PR `task/improve-sqlite` → code-reviewer gate → owner merge. Task finish protocol after merge.
