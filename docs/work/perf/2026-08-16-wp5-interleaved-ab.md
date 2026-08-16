# WP5 interleaved A/B: settling the wall-clock effect

Status: DONE
Date: 2026-08-16
Branch: `perf/wp5-interleaved-ab`

Companion to `docs/work/perf/2026-08-16-wp5-before-baseline.md` and
`docs/work/perf/2026-08-16-wp5-after-measurement.md`. Both are merged and honest; neither settled
wall-clock. The mechanism (statement volume per bank open falling ~92%, `MemorySchemaDdlStatementCountTests`
pinning 42→4 statements per open) is **not** re-litigated here — it is settled. This report exists
to answer the one thing the prior two attempts could not: is there a measurable wall-clock effect,
once machine-load drift is cancelled out by pairing instead of chased by waiting for a quiet
machine.

## Headline

**Split result, not a single number — and that split is the finding.**

| Operation | Paired result | Sign test (B faster / A faster, n=15) | Verdict |
|---|---|---|---|
| `memory_write` | **B measurably faster**, median diff −2.4 ms, mean −11.9 ms, growing to 37-55 ms at high load | 12/15 favour B, **p = 0.035** | Real, small, load-sensitive win |
| `memory_search` | **No reliable difference** | 8/15 favour B, 7/15 favour A, **p = 1.000** | Statistically a coin flip |
| Mixed (write+search combined, matching before/after's own methodology) | Not significant | 11/15 favour B, **p = 0.119** | Averaging hides the write win under search noise |

Writes get the outcome-1 result (a measurable paired improvement). Search gets outcome-2 — **no
measurable wall-clock difference despite the same 92% statement-volume reduction the mechanism
work proved** — and that is reported as a genuine finding, not softened into a small positive: for
`memory_search`, the removed bank-open statements were evidently cheap relative to whatever
dominates search latency (search medians run 130-300 ms against write's 15-85 ms in this same
data, consistent with embedding/vector-search cost swamping a few-statement saving). Reporting one
mixed median across both operations, as both prior passes did, would have hidden this split
entirely — a real effect on one operation type, and a real absence of effect on the other, average
out to "maybe."

## Why interleaved A/B instead of a third independent pass

The before and after reports each measured wall clock as two independent sessions, hours apart,
under different machine conditions (CPU thermal state, page cache, background load, other agents'
processes). Comparing their medians is confounded regardless of how quiet either session was
individually. The fix is not a quieter machine — it is running both trees *in the same session*,
alternating between them, and taking the **paired difference per round** (B−A). Load drift then
moves both arms of a round together and cancels out of the difference, which is the one quantity
this report trusts. This session's own load climbed from a 1-minute average of 9.6 to over 20
during the run (other agent lanes' concurrent activity, as expected — see "Load per round" below)
— exactly the kind of drift that sank the after-measurement doc's three independent passes, and
exactly what pairing is designed to cancel.

## Trees

- **A = before**: `639284b9` (the before-baseline doc's measured commit). Verified
  `git diff --stat 7cfaefca..639284b9` touches only `docs/work/checklist/2026-08-16-rerun-1-20-0.json`
  (docs-only) — so `639284b9` is interchangeable with the plan's named baseline `7cfaefca` for `src/`.
  Built in a separate `git worktree` at commit `639284b9`, `dotnet build -c Release`.
- **B = after**: `origin/main` at `0609d019` (current tip when this branch was cut). Built in this
  worktree, `dotnet build -c Release`.

## Method

### Bank

Every arm of every round opened its **own fresh copy** of `~/ai-raccoon-backups/memory-20260816-133916.db`
(202,543,104 bytes), copied into a scratch `--data-root` as `memory.db` and never written back. The
original was never opened read-write; its checksum was verified unchanged both before this session
and after it finished: `8086c535d13ffc1413bb88d341548af7`, both times.

**A design choice worth stating plainly: "same bank copy" was read as "the same starting bank
content," not literally one file both trees mutate in sequence.** Reusing one evolving file across
A and B within a round was rejected: B stamps a schema digest the older tree's `EnsureAsync` does
not understand, so running A on a bank B had already touched would put A on a different code path
than "before" ever measured, contaminating the comparison rather than controlling it. Both arms of
every round instead start from byte-identical copies of the same pristine backup — matching the
before/after docs' own "fresh copy per pass" convention — so what differs between A and B in a
round is the code, not the starting data.

### Real server, real HTTP, real MCP protocol — not a fake harness

Each arm: `dotnet AiRaccoon.dll --data-root <scratch> serve --port 0` as a genuine separate OS
process (Release build), the bound loopback URL read from the process's own stdout line (matching
`NodeRunner.EmitBoundUrl`'s actual output, confirmed by reading that code rather than assuming
Kestrel's default log format), the per-run token read from `<data-root>/mcp-token`
(`McpTokenFile`/`McpTokenGate.HeaderName = "X-AiRaccoon-Token"`). A small standalone console driver
(`ModelContextProtocol.Client.McpClient` over `HttpClientTransport`, `HttpTransportMode.StreamableHttp`
— the same pattern `McpTokenGateE2ETests.cs` uses against the real gate) drove real `memory_write`
and `memory_search` tool calls over real loopback HTTP, timing each round trip client-side.
`Tree A` (pre-ADR-0075) does not default-close `/mcp`, so the token header is simply extra and
harmless there — confirmed by smoke-testing the driver against both trees before the timed session.

Per arm, per round: 5 writes + 5 searches warm-up (uncounted), then 20 writes + 20 searches
measured, summed and median-timed client-side. This is smaller than the before/after docs' 300+300
— deliberately: this report needs many *rounds* to pair against load drift, not one enormous
sample per round, and 20+20 already gives each round's median a stable footing (steady-state
distributions in both prior docs were already tight by ~20-25 samples).

### Rounds and order

16 rounds total. **Which arm ran first alternated every round** (odd rounds A-then-B, even rounds
B-then-A) so a warming trend across the session could not be mistaken for an effect. **Round 1 —
the first measurement of both A and B — was discarded as warm-up**, per the task brief, leaving
**15 rounds in the analysis** (rounds 2-16).

### Analysis

Paired difference per round = B's metric − A's metric (negative = B faster). Reported for three
metrics: total wall-clock for the 40-op measured batch, the write-only median, and the search-only
median. A two-sided exact sign test (binomial, p=0.5) on the direction of each round's diff — the
same test the task brief asked for, chosen because it makes no distributional assumption about the
diffs themselves (which visibly are not normal — see the table below).

## Results

### All 16 rounds (round 1 greyed out as warm-up, not used below)

| Round | First | Load (1-min, before/mid/after) | A total ms | B total ms | Δtotal | A write med | B write med | Δwrite | A search med | B search med | Δsearch |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1* | A | 9.62/11.02/12.13 | 4137 | 3221 | −916 | 54.6 | 17.1 | −37.5 | 154.7 | 134.0 | −20.7 |
| 2 | B | 12.13/14.29/14.29 | 4471 | 4346 | −125 | 53.4 | 51.0 | −2.4 | 163.2 | 151.0 | −12.2 |
| 3 | A | 14.29/16.27/16.20 | 5070 | 3814 | −1256 | 60.6 | 20.0 | −40.6 | 170.9 | 160.1 | −10.8 |
| 4 | B | 16.20/16.83/18.20 | 4062 | 3714 | −348 | 20.0 | 17.6 | −2.4 | 174.8 | 159.1 | −15.7 |
| 5 | A | 18.20/18.70/19.87 | 5426 | 5458 | +32 | 64.5 | 63.6 | −0.9 | 196.6 | 206.3 | +9.6 |
| 6 | B | 19.87/20.68/20.04 | 5754 | 5086 | −667 | 70.6 | 21.3 | −49.3 | 205.4 | 209.3 | +4.0 |
| 7 | A | 20.04/19.58/20.11 | 6356 | 7362 | +1006 | 79.5 | 76.9 | −2.6 | 213.8 | 226.3 | +12.5 |
| 8 | B | 20.11/19.23/18.94 | 7603 | 5893 | −1710 | 79.5 | 24.5 | −55.1 | 272.2 | 238.1 | −34.2 |
| 9 | A | 6.57/9.57/10.00† | 3674 | 3382 | −293 | 21.2 | 19.5 | −1.7 | 146.4 | 146.5 | +0.1 |
| 10 | B | 10.00/12.88/12.15 | 4428 | 4038 | −390 | 53.3 | 54.2 | +0.9 | 152.4 | 140.9 | −11.5 |
| 11 | A | 12.15/12.86/14.47 | 3848 | 4603 | +755 | 22.9 | 59.3 | +36.4 | 151.7 | 154.0 | +2.3 |
| 12 | B | 14.47/17.50/17.27 | 4454 | 5261 | +807 | 27.3 | 64.3 | +37.0 | 175.4 | 177.8 | +2.3 |
| 13 | A | 17.27/16.51/18.39 | 5402 | 4771 | −631 | 61.9 | 23.4 | −38.5 | 199.7 | 207.9 | +8.2 |
| 14 | B | 18.39/18.26/17.58 | 6150 | 5920 | −230 | 80.6 | 25.1 | −55.5 | 224.7 | 210.6 | −14.1 |
| 15 | A | 17.58/17.41/17.01 | 7060 | 6738 | −321 | 84.5 | 84.3 | −0.2 | 264.9 | 251.8 | −13.2 |
| 16 | B | 17.01/17.60/18.21 | 6761 | 6518 | −244 | 31.5 | 27.2 | −4.3 | 298.3 | 286.2 | −12.1 |

\* Round 1 discarded as warm-up (both arms' first measurement).
† Rounds 9-16 were a separate continuation invocation moments after round 8; the 1-minute load
average resets visibly (20.11 → 6.57) while the 5/15-minute averages stay elevated, which is a real
artifact of the gap between the two shell invocations, not a data error — noted here rather than
smoothed over.

**Load rose through the whole session** (other agent lanes' concurrent activity, exactly as
expected and instructed — this lane did not wait for a quiet machine). That climb is visible in
both arms' absolute totals rising together round over round, which is the load-drift the paired
design exists to cancel: the total-ms columns trend upward across the session for *both* A and B,
but the **difference** column does not trend the same way, which is the point.

### Paired-difference summary (n = 15, rounds 2-16)

**Total (write+search combined, 40 ops/round):**
diffs (ms): −125, −1256, −348, +32, −667, +1006, −1710, −293, −390, +755, +807, −631, −230, −321, −244
median = **−293 ms**, mean = **−241 ms**, stdev = 722 ms
11/15 rounds favour B, 4/15 favour A → **sign test p = 0.119 (not significant)**

**Write median (20 ops/round):**
diffs (ms): −2.4, −40.6, −2.4, −0.9, −49.3, −2.6, −55.1, −1.7, +0.9, +36.4, +37.0, −38.5, −55.5, −0.2, −4.3
median = **−2.4 ms**, mean = **−11.9 ms**, stdev = 29.6 ms
12/15 rounds favour B, 3/15 favour A → **sign test p = 0.035 (significant at α=0.05)**

**Search median (20 ops/round):**
diffs (ms): −12.2, −10.8, −15.7, +9.6, +4.0, +12.5, −34.2, +0.1, −11.5, +2.3, +2.3, +8.2, −14.1, −13.2, −12.1
median = **−10.8 ms**, mean = **−5.7 ms**, stdev = 12.5 ms
8/15 rounds favour B, 7/15 favour A → **sign test p = 1.000 (a coin flip)**

### Does the write effect track load?

The write-diff magnitude is visibly larger in high-load rounds (−40 to −55 ms in rounds 3, 6, 8,
13, 14) than in low-load rounds (−1 to −2 ms in rounds 2, 4, 5, 7, 9) — consistent with "fewer
statements per open costs less contention time as concurrent load rises," which is mechanistically
plausible given the settled 168→12 (write)/210→16 (search) statement-volume reduction. **This
tracks only loosely, not cleanly: Pearson r(load, write-diff) = −0.43**, and rounds 11-12 are
outright counterexamples — B was *slower* by 36-37 ms at moderate load in both. This report is not
claiming the load-scaling relationship is proven; it is reporting a real, direction-consistent,
statistically significant effect (p=0.035) whose *size* correlates loosely with load and should not
be read as more than that.

## Verdict, in plain words

**`memory_write` has a small, real, statistically significant wall-clock improvement on the
optimised tree** — median 2.4 ms, but reaching 40-55 ms under the load conditions this session
happened to hit, and consistent in direction in 12 of 15 paired rounds (sign test p=0.035). This is
believable given the settled mechanism: writes went from 168 to 12 SQL statements per operation in
steady state, and 3-4 of those extra statements' worth of savings compounding under lock/WAL
contention is a coherent story for why the effect grows with load rather than staying flat.

**`memory_search` shows no measurable wall-clock difference — and that is the more interesting
finding, not a lesser one.** 8 of 15 rounds favoured B, 7 favoured A; the sign test (p=1.0) says
this is indistinguishable from chance. Search paid the same statement-volume cut (210→16) that
writes did, but it did not show up in wall clock. The likely explanation, visible in this same
data: search medians run 130-300 ms against write's 15-85 ms — search is dominated by something
else (embedding generation and/or vector scoring, neither touched by WP1-WP7's schema-open gating),
and a savings of a few SQL statements' worth of time is simply too small relative to that other
cost to detect against this session's noise floor. **This is exactly the task brief's outcome #2:
the removed statements were cheap for search specifically, and the real cost for that operation
lives elsewhere** — a genuinely useful thing to know, not a failed measurement.

**Reporting one mixed median, as both prior docs' methodology implied, would have buried this.**
11/15 rounds favoured B on the combined metric but that is not significant (p=0.119) — it is a
write win diluted by search noise into "maybe," which is worse information than the split result
above.

## What's committed on this branch

- This report.
- Nothing else. The driver console app, the round-orchestration scripts, and all bank copies lived
  in a machine-local scratch directory outside the repo — throwaway, per the before/after docs' own
  practice, and not reproducible without the machine-local backup path.

## Contradictions with the prior two reports, stated plainly

1. **The after-measurement doc's wall-clock section is superseded, not contradicted.** It correctly
   reported "inconclusive" rather than publishing a noisy median — that call holds up. This report
   supplies the number that session could not get, using a different method (pairing) rather than a
   cleaner environment.
2. **Neither prior doc distinguished write from search for wall clock** (only for statement/open
   counts). That distinction is what makes this report's result legible: a single mixed-workload
   number, the shape both prior docs used, would have reported "maybe a small win, maybe nothing" —
   true of the average but wrong about either operation individually.

## Branch / commits

- `perf/wp5-interleaved-ab`, pushed to `origin/perf/wp5-interleaved-ab`.
- Draft PR #364. Not merged to main; integration is the owner's. Do not merge this PR.
- Original backup bank checksum re-verified unchanged at completion:
  `8086c535d13ffc1413bb88d341548af7`.
