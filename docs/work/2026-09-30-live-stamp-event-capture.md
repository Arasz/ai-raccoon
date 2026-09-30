# live-stamp-opportunistic — LIVE next-restart stamp event CAPTURED

Task `air-254-vs-1022-retrieval-measurement-809` step `live-stamp-opportunistic`, issue #809.
The opportunistic event occurred: the owner-initiated live-server restart of 2026-09-30
≈15:13Z ran the third unproven pass (attempt 3 -> MaxRetryAttempts -> stamp).

Data root: none — this step ran no serve and no benchmark. Every probe below is a read-only
sqlite open (`file:$HOME/.ai-raccoon/memory.db?mode=ro`, sqlite3 3.54.0). The capture rides an
owner-initiated restart only; no run in this task ever used `~/.ai-raccoon` as a data root.

## BEFORE — convergence state as measured earlier (pasted, not prose)

From `docs/work/2026-09-30-stamp-gate-decision.md` (probe block verbatim):

```
probe_time_utc: 2026-09-30T09:06:10Z
$ sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro" "SELECT key,value FROM settings WHERE key LIKE 'embedding.chunkBudget%';"
embedding.chunkBudget.retryAttempts|1022:2

$ sqlite3 ... "SELECT COUNT(*) FROM settings WHERE key = 'embedding.chunkBudget';"
0
```

An earlier probe in the same session at 2026-09-30T08:28Z showed the same rows. Reading per
ADR-0125: the stamp was **absent** and the live bank sat at **2 of 3** unproven passes at the
resolved budget 1022.

## EVENT — the restart and the third pass (pasted probes)

```
probe_time_utc: 2026-09-30T15:41:24Z
$ sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro" "SELECT key||CHAR(9)||value FROM settings WHERE key LIKE 'embedding.chunkBudget%' ORDER BY key;"
embedding.chunkBudget	1022
embedding.chunkBudget.retryAttempts	1022:3
$ sqlite3 ... "SELECT id, engine, datetime(started_at,'unixepoch') AS started_utc, datetime(finished_at,'unixepoch') AS finished_utc FROM model_migration ORDER BY id DESC LIMIT 2;"
1|local:bundled#ef600cb98e973722d33646a86640270a2a6a968f4842f55f60f7cd52d9e6a502|2026-09-30 15:13:22|2026-09-30 15:14:55
$ sqlite3 ... "SELECT name||CHAR(9)||value||CHAR(9)||datetime(recorded_at,'unixepoch') FROM metrics WHERE name LIKE 'job.model-migration%' ORDER BY recorded_at DESC LIMIT 2;"
job.model-migration.duration_ms	92831.2693	2026-09-30 15:13:22
job.model-migration.duration_ms	82267.5515	2026-09-29 11:06:26
```

Corroboration (recorded by the webgpu-footprint lane's ambient probes, same hour):

- live `ai-raccoon` PID 73309 gone and `ai-raccoon serve --restart` PID 14180 started ≈17:15
  local (15:15Z) — the owner-initiated restart;
- `~/.ai-raccoon/memory.db-shm` mtime 17:13 local (15:13Z) matches `model_migration.started_at`.

## AFTER — convergence state (pasted probe, 2026-09-30T15:41:24Z)

```
embedding.chunkBudget	1022
embedding.chunkBudget.retryAttempts	1022:3
```

The stamp `embedding.chunkBudget=1022` is **written** and the attempt counter reads `1022:3`
— attempt 3 reached `MaxRetryAttempts` (3, ChunkBudgetReconciler.cs:77 per the stamp-gate
note) and stamped, exactly the `attempt 3 -> MaxRetryAttempts -> stamp` sequence this step
was armed to catch. The attempt progression is measured end to end: `1022:2` (09:06:10Z) ->
third pass -> `1022:3` + stamp (between 15:13:22Z and 15:41:24Z).

## Refusal window — ISO-8601 bounds and honesty

MEASURED bounds of the live third pass / short refusal window (from the bank's own rows,
pasted above):

- window open: **2026-09-30T15:13:22Z** (`model_migration.started_at`, epoch 1790781202)
- window closed: **2026-09-30T15:14:55Z** (`model_migration.finished_at`, epoch 1790781295)
- duration: **93.0 s** (`job.model-migration.duration_ms 92831.2693`, recorded 15:13:22Z)

Direct write-refusal probe timestamps INSIDE the window were not captured: the window was
already closed when the opportunistic trigger (the footprint lane's host-overlap note) reached
the orchestrator, and the live server's own logs predate the event (`quiet.log` mtime Sep 24,
`serve.log` mtime Sep 4 — both stale). This is a labelled gap, not a claim: the window bounds
above are MEASURED rows, the in-window refusal behaviour is INFERRED from those rows plus the
refusal-window character measured on root A (S8: 24m+3m35s full re-chunk, S9: 8m16s scan-only
— `docs/work/2026-09-30-re-chunk-phase-measurement.md`, `...-scan-only-measurement.md`).
No live write was attempted to provoke a refusal (the live bank is never written).

## Live-bank integrity

Every open in this capture: `sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro"` (read-only).
No data root in this task was ever `~/.ai-raccoon`; the stamp event rode the owner-initiated
restart (`ai-raccoon serve --restart`, PID 14180) only.
