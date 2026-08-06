# Bank maintenance fix shape (user-approved 2026-08-07)

Design decision for the `sqli-impro` task: periodic SQLite maintenance for the AiRaccoon bank. Replaces the earlier "hosted-service tick only" note in physical-layer-space-wal-audit.md — this is the version the user approved after
correcting a scoping mistake.

## The correction ("stdio is short lived" — user f: feedback)

The first proposal registered the maintenance service for STDIO transports only, reasoning that the lingering hermes stdio bridges were the WAL-growth driver (measured: 7 processes, several alive 4h+). Wrong direction: stdio processes are
per-session and short-lived BY DESIGN. A
`PeriodicTimer` in such a process is unreliable — a 2-minute session never ticks, a weekly VACUUM cadence essentially never fires. Timers need a long-lived host.

But serve-only has its own hole, also measured: the WAL was born 19:49 when the first stdio bridges started; the serve process didn't exist until 22:04 — the first ~2h15m of WAL growth was pure stdio traffic with no long-lived host alive.
Serve-only maintenance would miss exactly the traffic that grows the WAL (and the idle watchdog exits serve after 4h idle anyway).

## Approved shape: one lifecycle-aware service, registered in ALL modes

- **Startup pass** — first action of `ExecuteAsync`, before the loop: `PRAGMA wal_checkpoint(TRUNCATE)`. Every process (stdio or serve) gets one. Cost measured ~0 (the 431 MB WAL truncated instantly). Bounds the WAL to per-session write
  volume: a session accumulates a few MB, the next session start truncates. Busy contention (another process mid-read) → TRUNCATE returns busy>0, harmless, retried at the next boundary.
- **Shutdown pass** — `StopAsync` override: final best-effort TRUNCATE on clean exit.
- **Periodic timer** — hourly TRUNCATE + weekly VACUUM+ANALYZE. Only meaningful in the long-lived serve process; in short stdio sessions the timer simply never fires — the boundary passes did the work.
- VACUUM needs exclusive access; with N processes holding the bank it may hit SQLITE_BUSY after busy_timeout — log and retry next weekly tick, acceptable.
- Serve-down stretches get session-boundary checkpoints only; VACUUM waits until serve is next up — acceptable: truncation is the 431 MB fix, VACUUM the 14 MB nicety.

## Registration facts (ai-raccoon, read 2026-08-07)

- `Dependencies.RegisterMemoryServices(options, mcpTransport)` is the single registration point, called by BOTH hosts: `McpServerSetup.CreateAppHost` (stdio-only, plain Host) and
  `CreateWebHost` (HTTP/S). Unconditional `AddHostedService<...>()` there = all modes.
- Stdio-only mode currently runs ZERO hosted services (extraction/watch are HTTP-gated in
  `RegisterExtractionBackgroundService`); this is the first all-mode hosted service.
- `TimeProvider`: `RegisterMemoryServices` registers `TimeProvider.System`; the web host registers its fake-clock test seam AFTER it (line 69) — last registration wins, so tests of the web host keep their seam.
- Pattern to copy: `ExtractionHostedService` (PeriodicTimer + TimeProvider + `RunOnceAsync` seam
    + interval re-read per tick + best-effort catch + nested `Log` [LoggerMessage] class).
- Settings keys, if configurable: follow `ExtractionConfigKeys` (Core layer, `Parse*` with safe fallbacks); CLI verbs route via `ConfigCommands` — there is NO generic key-value setter.
