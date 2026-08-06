# Bank maintenance design — WAL checkpoint + vacuum/analyze

Date: 2026-08-07. Status: implemented (R1/R2/R4).

## Measured problem

[2026-08-06-sqlanalyze-usefulness.md](./2026-08-06-sqlanalyze-usefulness.md) F2/F4:

- **F4** — the live bank's WAL was **431,652,432 bytes** against a 29 MB database; a manual
  `wal_checkpoint(TRUNCATE)` collapsed it to 0 bytes instantly (tuple `0|0|0`), i.e. the WAL
  was ~100% checkpointable garbage. Nothing in the app checkpoints except
  `SyncService.WaitForWalCheckpointAsync` (sync-only, never run).
- **F2** — 47.9% of the main file was freelist (3446/7200 pages); VACUUM halves the footprint
  (~48.1% smaller) with no downside measured.

## Decisions

- **R1 — checkpoint every process, at the boundaries and on a timer.** Every process runs
  `wal_checkpoint(TRUNCATE)` once at startup (first action of `ExecuteAsync`) and once
  best-effort at shutdown (`StopAsync`), then on a periodic timer (default 60 min). The tuple
  `busy|log|checkpointed` is read: `busy > 0` logs a Warning (deferred), never an error.
  Registered in **all modes** (stdio AND http serve): a stdio process is session-bound and
  short-lived, so its startup + shutdown boundary checkpoints are the only ones it gets;
  serve is long-lived but exits via the idle watchdog, so the shutdown checkpoint bounds the
  WAL there too. Boundary checkpoints keep the WAL small in both shapes.
- **R2 — VACUUM + ANALYZE on a longer cadence** (default 7 days), checked on each checkpoint
  tick, VACUUM then ANALYZE in that order (VACUUM drops `sqlite_stat1`). No `auto_vacuum`, no
  `wal_autocheckpoint` override, no schema changes, no enabled flag.
- **R4 — ANALYZE rides the vacuum tick**; there is no separate analyze cadence.
- **Per-process vacuum clock.** `_lastVacuumUtc` is in-memory and seeded on the first run, so
  short-lived processes never vacuum; the cadence is per-process, not persisted.
- **Defer-fast checkpoints.** The maintenance connection sets `busy_timeout=250` ms: a
  contended checkpoint returns `busy>0` quickly and retries next tick instead of blocking the
  maintenance connection for the stock 5 s. (Measured: with a reader pinning the WAL, a stock
  connection blocks the full busy timeout before returning the same `busy>0` tuple.)

## Settings keys (settings table is the only runtime config channel)

| Key | Default | Meaning |
| --- | --- | --- |
| `maintenance.checkpoint-interval-minutes.global` | 60 | Periodic checkpoint cadence |
| `maintenance.vacuum-interval-days.global` | 7 | VACUUM + ANALYZE cadence |

Bad values (unparseable, zero, negative) fall back to the defaults, never throw.

## Failure modes

- Checkpoint busy (reader/writer pins the WAL) → Warning 511, defer to next tick.
- Run failure → Error 513, loop survives.
- Settings read failure (e.g. missing table) → Warning 514, defaults used, loop survives.
- Shutdown checkpoint failure → Warning 515, shutdown proceeds.

## CLI

`ai-raccoon maintenance interval <minutes>` / `maintenance vacuum-interval <days>` /
`maintenance list` — same settings-table channel the service reads.
