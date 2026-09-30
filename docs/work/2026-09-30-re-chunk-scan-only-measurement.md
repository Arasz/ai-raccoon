# Re-chunk scan-only measurement: second quiet serve on root A

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809 item 3), step
`rechunk-measure-scanonly` (corroborating run). Measurement only: no production code was changed.

Data root: /tmp/aira-rechunk-measure

This run continues `2026-09-30-re-chunk-phase-measurement.md` (the full run, pass 1). It isolates
the scan-only rebudget pass and its refusal window by stopping pass 1's leftover serve and starting
a second quiet serve on the same disposable root. The headline: root A's scan-only pass took
**496.02 s**, which is 6.03x the 82.267 s live anchor, and the 13 retryable groups from pass 1 hit
their retry bound and became unprovable, so the `embedding.chunkBudget` stamp was finally written.

## Headline numbers

| Fact | Value | Source |
|---|---|---|
| Second-pass rebudget (event 448) | **00:08:16.0222926** (496.0222926 s) | pass-2 448 line |
| Second-pass groups | **0 note + 0 mirror re-chunked**, 5,491 unchanged, 0 retryable, 13 unprovable, 1 terminal | pass-2 448 line |
| Migration open window | `10:18:52Z -> 10:27:08Z` (496 s) | `model_migration` row |
| Migration maintenance job | 496,096 ms | `MaintenanceJobRunner` line |
| Refused tool-call window (observed) | `10:18:51Z -> 10:27:07Z`, first success `10:27:09Z`; 233 refusals | probe loop + quiet.log |
| Compared to 82.267 s live anchor | **6.03x, +413.755 s** | arithmetic below |
| 13 pass-1 retryable groups | became **13 unprovable**; `retryAttempts 1022:2 -> 1022:3`; stamp `embedding.chunkBudget=1022` written | 448 line + settings probes |
| Entries on root A | **21,757** (not the 40,855 the brief assumed), pending 0 | read-only probes |
| Serve build | `1.55.0+12e7a1e6b8b2c8af5b113ecf4ab7097655be7251` (pass 1 ran `+3fcfba5...`) | startup line + ref file |

## Context and state deviations

The brief's assumptions did not match root A, and probing settled each one before any action:

- **Entries settled at 40,855?** No. The read-only probe returned **21,757** entries. Pass 1's own
  note records the fall from 40,855 to 21,757 after re-chunking, so the brief carried the
  pre-migration figure. Nothing changed between pass 1 and this run (21,757 before and after).
- **The stamp to delete?** Absent already, exactly as pass 1 left it
  (`embedding.chunkBudget.retryAttempts|1022:2`, 0 rows for `embedding.chunkBudget`). No deletion was
  performed or needed.
- **Engine fingerprint?** Current: `embedding.engine|local:bundled#ef600cb9...`, matching
  `embedding.codeEngine`. The pending count was 0.
- **Worktree commit moved.** Pass 1 recorded `1.55.0+3fcfba5...`; this run's build and serve report
  `1.55.0+12e7a1e6b8b2c8af5b113ecf4ab7097655be7251`. The branch advanced between passes. I did not
  inspect the diff (house rule: no git commands), so commit drift is a comparability caveat for any
  pass-1-vs-pass-2 delta; everything in this note describes commit `12e7a1e6`.

Pass 1's stale serve was found alive (PID 69242, lsof confirmed it held
`/tmp/aira-rechunk-measure/memory.db`, PPID 1). It was still churning `Embed drain pass finished for
Code: 128 row(s)` when the run started. Stopping it was sanctioned, and the stop record is
`/tmp/aira-rechunk-measure/pass2-stop-record.txt`:

```text
=== STOP RECORD 2026-09-30T10:17:09Z ===
  PID  PPID ELAPSED      TIME COMMAND
69242     1   48:51  30:15.36 src/AiRaccoon/bin/Debug/net10.0/AiRaccoon --data-root /tmp/aira-rechunk-measure --quiet serve --port 0
SIGTERM sent to 69242
EXITED (kill -0 fails) at 2026-09-30T10:17:10Z
```

PID 73309 (`ai-raccoon serve --restart`, the live server) was never touched. Its state reads
`embedding.chunkBudget` 0 stamp rows and `embedding.chunkBudget.retryAttempts|1022:2` after the run,
the same as before.

## Host-solo gate

Three `ps aux` snapshots bracket the measured serve:

- `ps-pass2-t0.txt` (10:16:26Z): pass-1's stale serve still running, no other convergence run.
- `ps-pass2-quiet-gate.txt` (10:18:11Z): taken **after** stopping PID 69242 and **before** the
  measured serve. No `ai-raccoon` convergence or embed run exists. The live server is present at
  0.3-3.9% CPU; every other `ai-raccoon` process is an idle MCP client stub at 0.0%.
- `ps-pass2-during.txt` (10:21:27Z): during the window. The only active `ai-raccoon` work is the
  measured serve (PID 21524 at 90.2% CPU); the live server sits at 4.2%; nothing else above 0.0%.

Recorded caveat: other lanes ran `dotnet test` (job-search worktree) with two test hosts at
~33-35% CPU during the quiet-gate snapshot. Those runs were gone by the during-window snapshot
(10:21:27Z), so at most the first 2.5 minutes of the 8m16s window could have overlapped another
lane's test load. I did not stop them; they belong to another session, and the host-solo check this
task names is about `ai-raccoon` convergence/embed runs, which were absent.

## AC1: root A probed read-only; live bank untouched

Entry count, exact command, at 10:16:53Z while pass 1's serve still held the WAL (this is the
`mode=ro` form the AC names):

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT COUNT(*) FROM entries;"
21757
```

The same count read back at 10:27:47Z after the measured serve stopped (see the harness note
below):

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro&immutable=1' "SELECT COUNT(*) FROM entries;"
21757
```

Harness note, recorded honestly: after a clean shutdown removes `memory.db-wal`/`-shm`, a fresh
`mode=ro` open fails with `unable to open database file (14)` because a read-only connection cannot
create the WAL shm. `immutable=1` reads the main DB (it returned the stale migration row while the
serve ran, since it ignores the WAL), so it was used only after the writer stopped. The AC's exact
command works while a serve holds the WAL, which is when the bank is live.

The stamp probe before the second serve (the brief's "delete if present" instruction) and after it:

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' \
    "SELECT key, value FROM settings WHERE key LIKE 'embedding.chunkBudget%';"
embedding.chunkBudget.retryAttempts|1022:2

$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' \
    "SELECT COUNT(*) FROM settings WHERE key='embedding.chunkBudget';"
0

# after the second serve's migration closes
embedding.chunkBudget.retryAttempts|1022:3
embedding.chunkBudget|1022
```

So the stamp was absent at the start, and the second pass wrote it. Live bank probe at 10:16:53Z:

```text
$ sqlite3 'file:/Users/arasz/.ai-raccoon/memory.db?mode=ro' \
    "SELECT COUNT(*) FROM settings WHERE key='embedding.chunkBudget';"
0

$ sqlite3 'file:/Users/arasz/.ai-raccoon/memory.db?mode=ro' \
    "SELECT key, value FROM settings WHERE key LIKE 'embedding.chunkBudget%';"
embedding.chunkBudget.retryAttempts|1022:2
```

Live bank untouched: 0 stamp rows, retryAttempts `1022:2` before and after.

## AC2: the second quiet serve and its 448 line

Build: `dotnet build` succeeded, 0 warnings, 0 errors, 22.70 s (`pass2-build.log`); `VERSION` is
`1.55.0`; the instrumented worktree ref is `12e7a1e6...`. The serve was launched exactly as the
brief names, output appended to the same `quiet.log`, with a marker line written first so pass-1 and
pass-2 lines are separable (marker is line 4730; pass-1 lines end at 4729):

```text
2026-09-30T10:18:44Z PASS2-SECTION-BEGIN second quiet serve on root A after SIGTERM stop of stale PID 69242; pass-1 lines end at line 4729
```

Startup and open, from the pass-2 section:

```text
2026-09-30T10:18:50.8753520+00:00 [Information] AiRaccoon.Hosting.Node.NodeRunner: ai-raccoon: 1.55.0+12e7a1e6b8b2c8af5b113ecf4ab7097655be7251 serving on port 58366 from '.../air-254-vs-1022-retrieval-measurement-809/src/AiRaccoon/bin/Debug/net10.0/' (http://127.0.0.1:58366/mcp)
2026-09-30T10:18:52.7079660+00:00 [Information] AiRaccoon.Infrastructure.Embedding.EntryEmbedder: Embed drain for Memory started under the model migration: 0 row(s) owed
2026-09-30T10:18:53.2377690+00:00 [Information] AiRaccoon.Tools.ToolRefusals: "memory_search" refused: model-migration-in-progress
```

The 0-row migration shape the brief predicted is present on both edges:

```text
2026-09-30T10:18:52.7079660+00:00 [Information] AiRaccoon.Infrastructure.Embedding.EntryEmbedder: Embed drain for Memory started under the model migration: 0 row(s) owed
2026-09-30T10:27:08.7874800+00:00 [Information] AiRaccoon.Infrastructure.Embedding.EntryEmbedder: Embed drain pass finished for Memory: 0 row(s)
```

The second-pass 448 line, verbatim:

```text
2026-09-30T10:27:08.7803070+00:00 [Information] AiRaccoon.Infrastructure.Ingestion.ChunkBudgetReconciler: Chunk-budget rebudget at 1022 tokens: 0 note group(s) and 0 mirror group(s) re-chunked, 5491 unchanged, 0 retryable, 13 unprovable, 1 terminal in 00:08:16.0222926
```

Counts and Elapsed: `0 | 0 | 5491 | 0 | 13 | 1 | 00:08:16.0222926`. The pass was scan-only (zero
groups replaced) and still took 496.02 s.

Both AC2 commands were run; results recorded honestly. The whole-log command passed at 10:27:38Z
because the pass-2 448 line was still inside the tail-20 window (the Code drain had only written
eight lines after it):

```text
$ grep -E "Chunk-budget rebudget|Embed drain" /tmp/aira-rechunk-measure/quiet.log | tail -20 | grep -q "Chunk-budget rebudget"
$ echo $?
0
```

The section-scoped command also passed:

```text
$ tail -n +4730 /tmp/aira-rechunk-measure/quiet.log | grep -E "Chunk-budget rebudget|Embed drain" | tail -20 | grep -q "Chunk-budget rebudget"
$ echo $?
0
```

A direct section grep is the stable check; it finds exactly one pass-2 line:

```text
$ tail -n +4730 /tmp/aira-rechunk-measure/quiet.log | grep -c 'Chunk-budget rebudget'
1
```

The pass-1 lesson holds: the whole-log tail-20 check is time-sensitive. It reads `exit=0` now only
because the `Embed drain ... for Code` lines keep arriving; a minute's growth pushes the 448 line out
of the window and the same command flips to `exit=1`. The section-scoped check is the one to trust.

## AC3: refusal capture, window arithmetic, and comparability

The AC3 command passed:

```text
$ grep -q "refused" /tmp/aira-rechunk-measure/quiet.log
$ echo $?
0
```

A real `memory_search` against the second serve was refused 233 times (see below). The first raw
capture, written by the detached probe loop and saved verbatim in
`/tmp/aira-rechunk-measure/probe-pass2-refused.txt`:

```text
TS=2026-09-30T10:18:51Z PORT=58366
event: message
data: {"result":{"content":[{"type":"text","text":"model-migration-in-progress: ai-raccoon: a model migration is in progress; try again once it finishes (memory_search)"}],"isError":true},"id":1,"jsonrpc":"2.0"}
```

The matching server-side line (first of 233):

```text
2026-09-30T10:18:53.2377690+00:00 [Information] AiRaccoon.Tools.ToolRefusals: "memory_search" refused: model-migration-in-progress
```

A probe loop (`mcp-probe-pass2-loop.sh`, one `tools/call` every ~2 s) logged 240 records: 2 before
the port existed, 233 refusals, and the rest successes. The window edges:

| Edge | Value |
|---|---|
| Bank open (`started_at`, epoch 1790763532) | `2026-09-30T10:18:52Z` |
| First client refusal (stamp taken before the HTTP call) | `10:18:51Z` |
| First server-side refusal line | `10:18:53.2377690Z` |
| Last server-side refusal line | `10:27:07.0299030Z` |
| First successful post-window probe | `10:27:09Z` |
| Bank close (`finished_at`, epoch 1790764028) | `2026-09-30T10:27:08Z` |

Window arithmetic against the 82.267 s live anchor:

- Phase Elapsed: **496.0222926 s** = 496.0222926 / 82.267 = **6.0294x**, +413.755 s (+502.9%).
- Bank window: `10:18:52 -> 10:27:08` = **496 s**.
- Server-refusal span: `10:18:53.2377690 -> 10:27:07.0299030` = **493.792 s**.
- The maintenance job's own duration is **496,096 ms**, which agrees.

Three independent sources (448 Elapsed, bank row, job line) agree on ~496 s, so the figure is not a
log-sampling artifact. The brief predicted a short window near the live anchor; on root A the same
scan-only pass was six times longer. That is the measurement.

Product version and device pin: the serve reports `1.55.0+12e7a1e6...`, and root A carries
`embedding.device|coreml` with `embedding.threads|3`. Warm-cache comparability caveat: the root's
`coreml-cache` is a symlink to `/Users/arasz/.ai-raccoon/coreml-cache`, shared with the live server.
This run's Neural Engine sessions loaded from that warm cache in **6.2 s / 7.1 s**, against pass 1's
62.1 s cold load. The session load happened at `10:27:09`, after the migration closed, and the
scan-only pass embeds nothing, so the warm cache did not accelerate the measured 496 s. Any future
cold-cache comparison of this phase should expect the difference to sit in session load, not in the
scan.

## The 13 retryable groups' fate

Pass 1 left 13 note groups retryable with `embedding.chunkBudget.retryAttempts|1022:2` and no stamp.
This is the open question the corroborating run was asked to answer, and the answer is: on the next
server start they became **unprovable** (0 retryable, 13 unprovable in the 448 line) and the stamp
was written. The mechanism is in `src/AiRaccoon.Infrastructure/Ingestion/ChunkBudgetReconciler.cs`:
`attempts + 1 >= MaxRetryAttempts` (line 241, `MaxRetryAttempts = 3` at line 80) moves the residual
groups to `unprovable`, resets `retryable` to 0, and persists `retryAttempts = 3` (line 245); the
stamp gate then fires because `retryable == 0`. The observed settings after the pass match exactly:
`embedding.chunkBudget.retryAttempts|1022:3` and `embedding.chunkBudget|1022`. The 13 groups are
terminal for this budget, and later server starts on root A will short-circuit the drift trigger
instead of re-opening the migration.

## Why pass 2 took 496 s when pass 1's scan-only phase took 215 s

Same group population, same counts of unchanged/terminal groups, different wall clock:

| Pass | Phase | Counts (note/mirror re-chunked, unchanged, retryable, unprovable, terminal) | Elapsed |
|---|---|---|---|
| 1, window 2 | scan-only | 0, 0, 5,491, 13, 0, 1 | 215.2905774 s |
| 2 | scan-only | 0, 0, 5,491, 0, 13, 1 | 496.0222926 s |

Pass 2 is 2.30x pass 1's scan-only phase. Two HYPOTHESES explain most of it; neither is directly
instrumented:

- **HYPOTHESIS (cold page cache):** the rebudget walks every mirror group and calls
  `scanner.Scan(group.SourceFile, ...)`, which reads the source file from disk. Pass 1's window 2 ran
  minutes after window 1 had already scanned those same files in the same process. Pass 2 ran in a
  fresh process, cold.
- **HYPOTHESIS (startup-time concurrency):** pass 2 opened the migration 1.8 s after the server
  started listening, while watch activation and other startup work were still settling, and another
  lane's `dotnet test` runs overlapped part of the early window. Pass 1's window 2 ran in a
  long-warmed process.

What is not a candidate: code-corpus contention. The `Embed drain ... for Code` pump only resumed at
`10:27:08.802`, after the migration closed, and the `code-reindex` maintenance job logged 1 ms
during the window.

## What I rejected

- **Deleting the stamp.** It was absent (0 rows). A blind `DELETE` would be a pointless write on a
  bank the brief marks disposable but read-only for this step.
- **`immutable=1` as the primary probe.** It ignores the WAL and read the stale migration row while
  the serve was up; `mode=ro` reads the live view. `immutable=1` was used only after the writer
  stopped.
- **Restarting the probe loop after noticing its status label never says "REFUSED".** The loop's
  label check looked for the word `refused`, but the response body says
  `model-migration-in-progress`; the label was cosmetic. Restarting would have truncated
  `probe-pass2-loop.txt`, so I parsed the raw bodies post hoc. All 233 refusal bodies are preserved
  verbatim.
- **A third serve to test the cold-cache hypothesis.** Out of scope for this step, and it would have
  added unmeasured state. The hypothesis is labelled as such.
- **Stopping the other lane's `dotnet test` runs.** Not mine, not an `ai-raccoon` run; recorded as
  load context instead.
- **Blocking waits.** The harness blocks `sleep`, so the run used detached loops plus bounded `perl`
  waits, as pass 1 did.

## Acceptance criteria

| AC | Check | Result |
|----|-------|--------|
| 1 | Root A `mode=ro` count is a clean integer; stamp deletion or recorded ABSENCE logged; live bank still 0 stamp rows / `1022:2` | **PASS**: 21,757; stamp ABSENT (0 rows); live bank `0` and `1022:2` |
| 2 | Second serve captured; 0-row migration; 'Chunk-budget rebudget' counts + Elapsed; tail-20 + section-scoped greps both recorded | **PASS**: both greps `exit=0` at 10:27:38Z; section grep finds 1 line; Elapsed `00:08:16.0222926`; memory drain `0 row(s) owed` / `0 row(s)` |
| 3 | `grep -q "refused"` + ISO-8601 capture; version, `embedding.device=coreml`, warm coreml-cache caveat | **PASS**: `exit=0`; first refusal `10:18:51Z` (server line `10:18:53.2377690`); `1.55.0+12e7a1e6...`; `coreml`/threads 3; sessions 6.2/7.1 s from the shared cache |

## Raw evidence

All under `/tmp/aira-rechunk-measure/` unless noted.

- `quiet.log` (marker at line 4730 separates the passes), `pass2-448.txt`, `ac2-ac3-checks.txt`,
  `probe-pass2-loop.txt`, `probe-pass2-refused.txt`, `state-pass2-loop.txt`
- `probe-pass2-pre.txt`, `probe-pass2-post-window.txt`, `probe-pass2-live-bank.txt`
- `ps-pass2-t0.txt`, `ps-pass2-quiet-gate.txt`, `ps-pass2-during.txt`, `ps-pass2-summary.txt`
- `pass2-stop-record.txt`, `pass2-launch-record.txt`, `pass2-build.log`
- `mcp-probe-pass2-loop.sh`, `state-pass2-loop.sh`
- Product ref read without git: worktree ref file `12e7a1e6b8b2c8af5b113ecf4ab7097655be7251`
  (runtime startup line carries the same hash).
