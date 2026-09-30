# Re-chunk phase measurement: full run on root A

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step
`rechunk-measure-full`. Measurement only: no production code was changed.

Data root: /tmp/aira-rechunk-measure

The measured root is the APFS clone prepared by `2026-09-30-re-chunk-measure-setup.md` (70,763
entries, `user_version` 17, `embedding.device=coreml`, `embedding.threads=3`, no
`embedding.chunkBudget*` keys). The instrumented worktree build was served detached as

    src/AiRaccoon/bin/Debug/net10.0/AiRaccoon --data-root /tmp/aira-rechunk-measure --quiet serve --port 0

Product version `1.55.0`; the server's own startup line reads
`ai-raccoon: 1.55.0+3fcfba517ac697b69d61a5c307c1cbe3aeff87bc serving on port 54788`
(commit `3fcfba5`, read from the worktree's `refs/heads` file; no git command was run).

## Headline numbers

| Fact | Value | Source |
|---|---|---|
| Re-chunk phase 1 (re-chunking run) | **00:05:11.4600913** (311.46 s) | event 448, `09:43:25.6452710Z` |
| Phase 1 groups | **764 note + 1,043 mirror re-chunked**, 3,684 unchanged, 13 retryable, 0 unprovable, 1 terminal | event 448 |
| Re-chunk phase 2 (retry, scan-only) | **00:03:35.2905774** (215.29 s) | event 448, `10:06:02.9997020Z` |
| Phase 2 groups | 0 note + 0 mirror re-chunked, 5,491 unchanged, 13 retryable, 0 unprovable, 1 terminal | event 448 |
| Migration window 1 | `09:38:06Z -> 10:02:06Z` (1,440 s = 24m00s) | `model_migration` read-only polls; singleton row later overwritten |
| Migration window 2 | `10:02:27Z -> 10:06:02Z` (215 s = 3m35s) | final `model_migration` row |
| Full re-embed drain | 44,107 owed, 21,757 embedded, 23m52.17s (`drain.memory.duration_ms` = 1,432,172.6063 ms) | log stride series + metrics |
| Refusal coverage | first refusal `09:40:44.4558880Z`, last `10:04:55.2215140Z`, with a measured 21 s gap (`10:02:06` -> `10:02:27`) where a probe succeeded | quiet.log + probe captures |

## Host-quiet gate

Three `ps aux` snapshots were taken (waiting included): `ps-before-build.txt` (11:25 local),
`ps-before-serve.txt` (11:27 local), `ps-during-run.txt` (12:09 local). No convergence process was
active. The only ai-raccoon/dotnet entries are the always-on live-bank server (PID 73309, 0.1-0.2%
CPU, reading `~/.ai-raccoon/memory.db`) and idle MSBuild node-reuse daemons (0.0-1.7%). No process
was killed. The measured serve was launched only after this check, and the charging `aira-copy1022`
style convergence lane never appeared in any snapshot.

## AC1: instrumented build served on root A

Command:

```text
$ cat VERSION
1.55.0
$ ps aux | grep -v grep | grep -q "aira-rechunk-measure" || test -s /tmp/aira-rechunk-measure/quiet.log
$ echo $?
0
$ ps -p 69242 -o pid,etime,time,command | cat
  PID ELAPSED      TIME COMMAND
69242   41:09  28:57.35 src/AiRaccoon/bin/Debug/net10.0/AiRaccoon --data-root /tmp/aira-rechunk-measure --quiet serve --port 0
```

Build: `dotnet build` succeeded, 0 warnings, 0 errors (5.06 s incremental). The built
`AiRaccoon.Infrastructure.dll` carries the instrumented message templates (`Chunk-budget rebudget`,
`started under the model migration`, `row(s) in`, `chunk.rechunk.duration_ms`), verified by a
UTF-16 byte count.

## AC2: t0, event 448 and the stride series

Command (verbatim, passed):

```text
$ grep -E "Chunk-budget rebudget|Embed drain|started under the model migration" /tmp/aira-rechunk-measure/quiet.log | tail -40 | grep -q "Chunk-budget rebudget"
$ echo $?
0
```

Both 448 lines verbatim:

```text
2026-09-30T09:43:25.6452710+00:00 [Information] AiRaccoon.Infrastructure.Ingestion.ChunkBudgetReconciler: Chunk-budget rebudget at 1022 tokens: 764 note group(s) and 1043 mirror group(s) re-chunked, 3684 unchanged, 13 retryable, 0 unprovable, 1 terminal in 00:05:11.4600913
2026-09-30T10:06:02.9997020+00:00 [Information] AiRaccoon.Infrastructure.Ingestion.ChunkBudgetReconciler: Chunk-budget rebudget at 1022 tokens: 0 note group(s) and 0 mirror group(s) re-chunked, 5491 unchanged, 13 retryable, 0 unprovable, 1 terminal in 00:03:35.2905774
```

The six counts and Elapsed are therefore `764 | 1043 | 3684 | 13 | 0 | 1 | 00:05:11.4600913` for
the re-chunking run. the t0 line:

```text
2026-09-30T09:38:14.1789920+00:00 [Information] AiRaccoon.Infrastructure.Embedding.EntryEmbedder: Embed drain for Memory started under the model migration: 44107 row(s) owed
```

The stride series (18 lines, ~61 s each, cumulative elapsed) runs from
`3104 row(s) in 00:06:12.7883447` to `21408 row(s) in 00:23:29.4926442`; the full series with
per-stride deltas is saved at `/tmp/aira-rechunk-measure/final-evidence.txt` and printed below
under AC5. A second drain start line appears for the retry window:

```text
2026-09-30T10:02:27.7098530+00:00 [Information] AiRaccoon.Infrastructure.Embedding.EntryEmbedder: Embed drain for Memory started under the model migration: 0 row(s) owed
```

Caveat recorded, not a failure: the acceptance command samples only the last 40 matching lines.
It returned `exit 0` when run for this note's evidence right after the run (`12:09` local,
`10:09Z`), because the second 448 line was still recent; re-running the identical command at
`12:11` local against the same log returned `exit 1` once the embed pump had written more lines
(637 matching "Embed drain" lines in total). The underlying facts are unchanged and present; a
direct `grep "Chunk-budget rebudget"` is the stable check and still yields both lines.

## AC3: phase wall-clock from 448's Elapsed

Command (verbatim, passed):

```text
$ grep -q "in [0-9][0-9]:[0-9][0-9]:" /tmp/aira-rechunk-measure/quiet.log
$ echo $?
0
```

No t-bracket fallback was needed: the phase's own `Elapsed` is on the 448 lines above. The
`includes ReconcileVecDimensionsAsync` caveat therefore does not apply to these numbers. The
elapsed excludes the vec-dimension reconcile by construction (the reconciler brackets only its own
work, `ChunkBudgetReconciler.cs:114`) and includes the budget scan, group scan/reassembly, and the
final column repair.

## AC4: refusal capture and window derivation

Command (verbatim, passed):

```text
$ grep -q "refused" /tmp/aira-rechunk-measure/quiet.log
$ echo $?
0
```

A real `memory_search` against the running server (MCP `tools/call` over HTTP on
`http://127.0.0.1:54788/mcp`, token from `/tmp/aira-rechunk-measure/mcp-token`) was refused. Raw
capture (`probe-cyc5.txt`, `TS=2026-09-30T09:40:44Z`):

```text
event: message
data: {"result":{"content":[{"type":"text","text":"model-migration-in-progress: ai-raccoon: a model migration is in progress; try again once it finishes (memory_search)"}],"isError":true},"id":1,"jsonrpc":"2.0"}
```

The matching server-side line:

```text
2026-09-30T09:40:44.4558880+00:00 [Information] AiRaccoon.Tools.ToolRefusals: "memory_search" refused: model-migration-in-progress
```

A pre-migration baseline search succeeded at `09:30:37Z` (12,921 bytes of results). Every refusal
line in the log:

```text
2026-09-30T09:40:44.4558880+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:42:36.3986720+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:44:19.5428520+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:46:31.3719440+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:48:45.9800210+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:52:10.4178780+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:55:32.9136930+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:58:55.7633790+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T09:59:24.3315090+00:00 "memory_search" refused: model-migration-in-progress
2026-09-30T10:04:55.2215140+00:00 "memory_search" refused: model-migration-in-progress
```

Window derivation, from read-only probes of the singleton `model_migration` row:

- Window 1: `started_at=1790761086` (`2026-09-30T09:38:06Z`) to `finished_at=1790762526`
  (`2026-09-30T10:02:06Z`), 24m00s. The singleton row was overwritten by the second open, so these
  values are what the read-only polls observed while the window was open; the drain-start line
  (`09:38:14`) and the job duration (1,432,276.95 ms) corroborate them.
- Gap: a probe at `10:02:25Z` succeeded (12,501 bytes), between the close and the reopen.
- Window 2: `started_at=1790762547` (`10:02:27Z`) to `finished_at=1790762762` (`10:06:02Z`), 3m35s,
  read as the final row.
- Union span: `09:38:06Z -> 10:06:02Z` = 27m56s. Refusals were only guaranteed inside the two
  windows; the 21 s gap is a measured search-available interval.
- Observation caveat: the first refusal was probed at `09:40:44Z`, 2m38s after the window opened
  (the poll chain was mid-wait). The window start comes from the bank, not from the first probe.

Why there were two windows (INFERRED from code plus the observed counts): the clone's stored
engine fingerprint (`local:bundled#777268da...`) no longer matched the bundled model's current
fingerprint (`local:bundled#ef600cb9...`), so the first open took the engine-change path with
`markAllEmbeddedPending=true` (all 40,855 rows became pending). That path does not consume the
budget-drift guard (`_budgetDriftAttempted`), and the phase withheld the `embedding.chunkBudget`
stamp because 13 groups stayed retryable; the next maintenance poll (`10:02:27`) re-opened the
migration for the deferred retry, drained 0 rows, and the same 13 retryable groups kept the stamp
absent. The process's guard is now spent, so no third window opened in this serve. Post-run
settings show `embedding.chunkBudget.retryAttempts|1022:2` and no `chunkBudget` stamp; at this
attempt count the next server start would convert the 13 remaining groups to terminal at the retry
bound (`MaxRetryAttempts = 3`) and write the stamp.

## AC5: post-close metrics, stride anomaly, code-reindex rows, device pin

Command (verbatim, passed; full 3,765-row output in `/tmp/aira-rechunk-measure/ac5-verbatim.txt`):

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT name FROM metrics WHERE name LIKE 'drain.memory.%' OR name='job.model-migration.duration_ms';" | grep -q drain
$ echo $?
0
```

Today's rows (recorded_at > 1790759000), the meaningful subset:

```text
chunk.rechunk.duration_ms|Histogram|311460.0913|ms
chunk.rechunk.groups|Histogram|1807.0|count
drain.memory.duration_ms|Histogram|1432172.6063|ms
drain.memory.rows|Histogram|21757.0|count
chunk.rechunk.duration_ms|Histogram|215290.5774|ms
chunk.rechunk.groups|Histogram|0.0|count
drain.memory.duration_ms|Histogram|215291.1077|ms
drain.memory.rows|Histogram|0.0|count
job.model-migration.duration_ms|Histogram|1432276.9538|ms
job.model-migration.duration_ms|Histogram|215291.3937|ms
job.code-reindex.rows|Gauge|38962.0|count      (then 30130 ... 20626 over the run)
```

The rows are positive, so `chunk.rechunk.duration_ms` matches event 448's Elapsed (311,460.09 ms)
and its groups metric matches `764 + 1,043 = 1,807`.

Stride-rate anomaly. The first stride's cumulative rate is 8.3 rows/s, but that number is depressed
by design: the cumulative elapsed starts at the drain start and includes the 311 s phase. The
per-stride deltas show the real embed throughput: ~20.3, 19.6, 19.1, 19.3, 19.6, 19.1, 18.9, 21.2,
19.6 rows/s from `09:45` to `09:54`, then a decline to 15.9, 15.3, 15.8, 14.9, 14.1, 15.2, 14.1
rows/s from `09:55` to `10:01`. That is a roughly 30% throughput decay across the drain, with one
spike at `09:52:35`. The cause was not investigated (HYPOTHESIS: GC, DB growth, or CoreML scheduling
under sustained load). Full per-stride table in `final-evidence.txt`.

`job.code-reindex` contention. The code corpus re-embed ran beside the memory drain: the job's
`rows` gauge fell from 38,962 to 20,626 during the run, and each code-reindex pass measured 0.08 to
0.33 ms. No lock-contention signature appears in the metrics or in quiet.log. The model-migration
lease covers the memory outbox only (ADR-0087), consistent with the two corpora draining in
parallel.

Explicit device pin. `embedding.device|coreml` and `embedding.threads|3` are set in the bank, and
the startup log records `Embedding session created: intra-op threads 3 (setting), execution
provider WebGPU (CoreML compiling)` followed 62.1 s later by `Embedding now runs on the Neural
Engine: sessions loaded from cache in 62.1 s and passed the parity probe.` The Neural Engine was
the serving device for the measured drain; the early rows were embedded on the WebGPU fallback
before the CoreML sessions finished loading.

## AC6: the PR-3.R7 budget statement

The step's budget statement, preserved verbatim: **the drain estimate is a CONSERVATIVE UPPER
BOUND (1.0-1.6 h) whose basis is stated; the likelier range is 25-50 min (post-re-chunk row
counts), and the phase scales with group count to an estimate of 15-17 min; the 3 h wall-clock
budget stands as the ceiling.**

Its basis, from `docs/work/2026-09-28-config-d-chunk-budgets-plan.md` F4: a real 25,917-entry bank
drained in 357 s on the old engine (ADR-0076), ADR-0108 quotes "roughly 6 minutes of refused tool
calls" for that bank, and MLX embed cost per 1k tokens is flat from 128 to 1022 tokens (46-57 ms
per 1k). Re-chunking at 1022 shrinks the row count roughly 4x where content was split, so the
drain stays token-bound, not row-bound; the 1.0-1.6 h figure is the conservative upper bound once
overlay duplication, file reads, and the phase's own scan cost are added. The likelier 25-50 min
range assumes post-re-chunk row counts; the 15-17 min figure is what the phase itself scales to
with group count.

Against this run: phase 1 measured **5m11s** over 5,505 groups, and the whole drain measured
**24m00s** for 21,757 embedded rows (`drain.memory.duration_ms` 23m52s). Both sit inside the
likelier range, and the phase sits below the 15-17 min estimate. The 3 h wall-clock budget was
never approached. The lower-than-estimated phase time has a known confound: the corpus was already
re-chunked once by `chunk-boundary-repair-v2` before event 448 ran, so 3,684 of 5,505 groups were
already at budget (unchanged) and only 1,807 needed replacement. A pristine first-run phase over
the original 70,763-row corpus would have more groups to replace and should be expected to take
longer than the 5m11s measured here.

## What else happened (interference and deviations)

- The clone carried live watch registrations, including `/Users/arasz/RiderProjects/ai-raccoon`.
  The serve held the watch lease (PID 69242) and re-ingested repo files while the measurement ran.
  Entries fell from 70,763 at clone time to 40,855 before the migration opened, then to 21,757
  after the re-chunk. HYPOTHESIS: both the watcher re-ingest and `chunk-boundary-repair-v2`
  contributed to the pre-migration drop; the split between them is not verified because the watch
  digest logs no per-file line.
- The once-ever `chunk-boundary-repair-v2` maintenance job ran first and took 551,182 ms (9m11.2s),
  ahead of `model-migration` in registration order. The migration therefore opened only at
  `09:38:06`, ~10 minutes after startup. The config-D phase then ran over the already-reduced
  corpus. This is the main reason the numbers above should not be read as a pristine first-run
  migration measurement.
- The first migration was the engine-change path (all 40,855 rows re-embedded), not the pure
  budget-drift path. The drain window is therefore embedding-dominated; the re-chunk phase is
  5m11s of a 24m00s outage.
- The `--quiet` file also received raw stdout fragments from the shell redirect (for example
  `trl+C to shut down.` and a truncated host-lifetime timestamp). They are console noise beside the
  structured log, not data loss.

## Rejected alternatives

- Serving anything but the worktree's instrumented apphost, or any port but the ephemeral `--port 0`
  (never 7721, never the live bank). All bank access for the measurement was read-only on root A;
  the live bank was never opened beyond the always-on server's own reads.
- Disabling the clone's watch registrations before the run. It would have changed the pinned root's
  runtime state; the interference is recorded instead.
- Deriving the outage window from probe timestamps. Probes are sparse observations; the bank's
  `started_at`/`finished_at` are authoritative and are what the note reports.
- The t-bracket fallback for phase wall-clock. Event 448's `Elapsed` is present, so no bracket that
  includes `ReconcileVecDimensionsAsync` was used.
- Stopping the serve mid-run to "reset" the retry window. That would have restarted the drain and
  changed the measurement; the process (PID 69242) was left running after the accepted run.
- Manual `sleep`-based polling. The harness blocks shell sleeps because they park the event loop;
  short bounded waits were chained instead, and the raw `ps`/probe/state artifacts are the record.

## Acceptance criteria

| AC | Check | Result |
|----|-------|--------|
| 1 | `ps aux \| grep -v grep \| grep -q "aira-rechunk-measure" \|\| test -s .../quiet.log` + version recorded | PASS (`exit 0`; `1.55.0+3fcfba5...`, port 54788) |
| 2 | message-text grep yields t0, the 448 counts + Elapsed, and the stride series | PASS at capture (`exit 0`; both 448 lines and 18 strides captured). The verbatim command is tail-window sensitive: it read `exit 1` when re-run ~2 h later against the grown log; direct greps still find every line. |
| 3 | `in [0-9][0-9]:[0-9][0-9]:` present from 448's Elapsed | PASS (`exit 0`; no t-bracket fallback used) |
| 4 | `refused` in quiet.log + real refused `memory_search` with ISO-8601; window from read-only probes | PASS (`exit 0`; refusal at `09:40:44Z`, window 1 `09:38:06-10:02:06Z`, window 2 `10:02:27-10:06:02Z`) |
| 5 | metrics rows > 0; stride anomaly, code-reindex rows, device pin recorded | PASS (`exit 0`; 3,765 matching rows; anomaly + rows + pin above) |
| 6 | budget statement (upper bound + basis + likelier range) in this note | PASS (this section) |

Raw evidence lives in `/tmp/aira-rechunk-measure/`: `quiet.log`, `final-evidence.txt`,
`key-events.txt`, `probe-*.txt`, `ps-*.txt`, `build.log`, `ac5-verbatim.txt`, `mcp-probe.sh`,
`state.sh`, `collect-evidence.sh`.