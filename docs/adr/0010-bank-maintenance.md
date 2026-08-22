# 0010 — Bank maintenance: WAL checkpoint + vacuum/analyze cadence

Date: 2026-08-07

Status: Accepted

## Context

The bank's WAL was measured at 431,652,432 bytes against a 29 MB database file — a
manual `wal_checkpoint(TRUNCATE)` collapsed it to 0 bytes instantly, i.e. the WAL was
~100% checkpointable garbage. Nothing in the app checkpoints except
`SyncService.WaitForWalCheckpointAsync`, which only runs during a sync cycle. Separately,
47.9% of the main file's pages were freelist (3446/7200); a `VACUUM` halved the file
size with no measured downside.

Both processes (stdio and HTTP serve) need this: a stdio process is session-bound and
short-lived, so its own startup/shutdown are the only checkpoint boundaries it ever gets;
`serve` is long-lived but eventually exits via the idle watchdog, so its shutdown
checkpoint bounds the WAL there too.

## Decision

Run WAL checkpointing and periodic vacuum/analyze as a background hosted service,
registered in every mode (stdio and HTTP/S):

- **Checkpoint at every boundary and on a timer.** `wal_checkpoint(TRUNCATE)` runs once
  at startup (first action of the hosted service), once best-effort at shutdown, and then
  on a periodic timer (default 60 min). The returned `busy|log|checkpointed` tuple logs a
  Warning when `busy > 0` — never an error; the next tick retries.
- **VACUUM + ANALYZE on a longer cadence** (default 7 days), checked on each checkpoint
  tick: VACUUM then ANALYZE in that order (VACUUM drops `sqlite_stat1`, so ANALYZE must
  follow it). No `auto_vacuum`, no `wal_autocheckpoint` override, no schema change.
- **ANALYZE rides the vacuum tick** — there is no separate analyze cadence.
- **The vacuum clock is per-process and in-memory**, seeded on first run: short-lived
  stdio processes never vacuum; only a long-lived `serve` process does, and only once its
  cadence elapses.
- **The maintenance connection defers on contention.** It sets `busy_timeout=250ms` so a
  contended checkpoint returns `busy>0` quickly and retries next tick instead of blocking
  for the stock 5s busy timeout; the stock timeout is restored before the connection
  returns to the pool, so no borrower inherits the override.
- **Config lives in the settings table** — `maintenance.checkpoint-interval-minutes.global`
  (default 60) and `maintenance.vacuum-interval-days.global` (default 7). Bad values
  (unparseable, zero, negative) fall back to the default, never throw; values above the
  ceiling (36500 days) clamp rather than reject at the parse layer (the CLI rejects them
  outright before they reach the settings table). WP11-C (2026-08-22) added a third key
  on the same channel, `maintenance.embed-rows-per-run.global` (default 128, ceiling
  4096) — same clamp-at-parse/reject-at-CLI shape.
- **CLI:** `ai-raccoon maintenance interval <minutes>` / `maintenance vacuum-interval
  <days>` / `maintenance embed-rows-per-run <rows>` / `maintenance list`, same
  settings-table channel every other config family uses.

## Consequences

- The WAL stays bounded in both stdio and serve shapes without any manual intervention;
  the pathological ~100% checkpointable-garbage state measured before this decision
  cannot recur unnoticed.
- Vacuum/analyze keep the file compact and the query planner's statistics current on a
  cadence that costs nothing for short-lived stdio sessions (they simply never run it) and
  bounded cost for `serve`.
- Checkpoint/vacuum failures degrade rather than crash: a busy bank retries next tick
  without error spam (Warning 511/516); a run failure logs Error 513 and the loop
  survives; a settings read failure (Warning 514) falls back to defaults; a shutdown
  checkpoint failure (Warning 515) never blocks shutdown.
- No new schema, no new dependency — the service reuses the existing SQLite connection
  and settings-table configuration channel.

**Evidence:** `docs/work/archive/2026-08-06-sqlanalyze-usefulness.md` (the measured
WAL/freelist numbers this decision responds to); `docs/work/archive/2026-08-07-bank-maintenance-design.md`
(full design record, archived here since its content is now this ADR).
