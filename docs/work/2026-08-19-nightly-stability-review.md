# Nightly test stability & usefulness — MoE review (2026-08-19)

Three parallel expert lanes (flake root-cause, nightly design, flake-history audit) reviewed the
nightly's 4-red-in-10 record, led by the 2026-08-19 `caughtMidDrain` failure. Full lane reports in
the delegation transcripts; this file records the synthesis and the decisions it produced.

## Attribution correction

The mid-drain failure the task opened with belongs to **branch run 32234742724** (head
`dae82ec9`, the adjust-nightly branch nightly, 08:52 UTC) — not the 03:01 scheduled nightly,
which failed on `TableRetrievalGateTests.ReversedRanking_FailsTheFloors` (already fixed by #389).
The mid-drain test is unchanged between main and that branch, so the flake lives in main's suite.
Three consecutive nightlies failed on three different contention-sensitive tests.

## Lane A — mid-drain flake root cause

**Verdict: production relay correct; test at fault.** The drain's first batch (ONNX session load
+ 32 rows, ~1-2s locally, far more under load) never completed inside the test's 90s blind wait on
a ~2× slower-than-usual runner. The baked-in failure message ("the relay likely finished before
this test could catch it mid-way") is **wrong by timing arithmetic**: the drain cannot start
before the first 15s on-demand poll tick, and a 5ms poll loop cannot miss a multi-second,
autocommitted-per-row drain. The consistent readings — drain started but batch 1 exceeded the cap,
or every relay attempt threw — are both invisible because the test discards the serve processes'
stdout/stderr (fire-and-forget `ReadToEndAsync`). Local reproduction: 3/3 catches, recovery cycle
(75s commit→closed, dominated by the 60s lease TTL) works exactly as ADR-0076 designs. The
mid-drain test has failed 1-in-5 executions since moving to nightly (#388).

## Lane C — flake-history audit

| Date | Test | Class | Verdict |
|---|---|---|---|
| 08-12 | watch BDD "Ten pending paths…" | B | UNADDRESSED — 2s fake budget = 20 ticks × ~20ms real sleep (0.4s real) is too few real iterations for OS event delivery |
| 08-15 | `NodeRunnerTests.IdleTimeout…` | B | UNADDRESSED — 15s wall-clock bound, observed 20.6s under full-suite load; **worst placement: `Speed=Fast`, gates every PR** |
| 08-19 scheduled | `ReversedRanking_FailsTheFloors` | C | FIXED — #389 relational survival ratio |
| 08-19 branch | prune `Post_CommitsAnOpenRequestRow` | B | FIXED — folded into #389 |
| 08-19 branch | mid-drain `caughtMidDrain` | B | UNADDRESSED — Lane A root-caused; fixed in this task |
| 08-08 ×2 | retrieval trio (SectionTargeted/SourceIdentity/SourceAffinity) | C | Contained to nightly by #388; relational rewrite only if they recur |
| 08-10 | OTLP sampler interference | D | FIXED — #256 |

Systemic pattern: a **rotating cast of Class B time-bounded real-I/O tests under full-suite
parallel load**, plus live-embedding numeric gates. The nightly is functioning as a signal
(6/10 green; failures triaged and fixed same-day); the residual problem is the ~40% red rate from
unaddressed Class B tests and the absence of any classification/notification machinery.

## Lane B — nightly pipeline design (adopted)

- **R1** `scripts/nightly-triage.py`: trx logger on the full run; on failure re-run exactly the
  failed FQNs once; classify each: rerun-pass → flake candidate, rerun-fail → regression,
  in-ledger → known flake; mass-failure guard (>50); no-trx → unclassifiable. Exit 0 iff every
  failure is ledgered (the repo's record-and-tolerate policy, machine-decidable).
- **R2** artifacts on failure: trx + serve logs + crash dumps (build-slow's pattern).
- **R3** `known-flakes.json` ledger: exact-FQN containment; enter via owner-approved PR with
  evidence; quarantine rule (2nd red in 10 runs → must fix); 30-day stale warning.
- **R4** red-nightly notification: native `gh` CLI, auto-file/comment a `nightly`-labelled issue
  (dedupe by open issue search); `issues: write` only.
- **R6** timeout 30→45 (rerun headroom); split trigger documented (killed by timeout or ≥35 min).
- **R7** one-line run summary to `$GITHUB_STEP_SUMMARY` + issue body.
- **R5 rejected**: Speed-trait value parity gate — the unfiltered nightly is the designated typo
  backstop; a value gate would be a redundant catcher (verified by Lane B against the existing
  `SpeedGateCoverageTests`).

## Decisions

1. **WP1 — mid-drain test redesign (test-only, Lane A Option 1):** two capped phases (relay
   started via `model_migration.lease_owner`, then partial-drain catch as today), serve
   stdout/stderr captured to a diag dir, failure message carries the real evidence (serve-log
   tails, `maintenance_jobs` ledger, `model_migration` row, entries-by-state). Real kill + real
   ONNX preserved. The 15s poll-interval seam and any production change are **rejected** for now
   (ADR-0076 ruled the surface out; batch-1 latency is the unbounded term, not poll latency).
2. **WP2 — the two unaddressed Class B tests:** `IdleTimeout` bound 15s → 30s (keeps
   `Speed=Fast` PR coverage; observed worst 20.6s; a fully broken idle timeout still hangs the
   test either way); watch `EnsureSearchable/NotSearchable` fake budget 2s → 6s (60 real
   iterations for OS event delivery, still bounded by the 30s real hang-stop).
3. **WP3 — the pipeline (R1-R4, R6, R7):** one script + one ledger file + nightly.yml edits.
   Proved locally with synthetic trx per R1's acceptance paths; the workflow itself proved by a
   dispatched branch nightly on the PR before merge.
4. **Not doing:** Speed-value parity gate (R5); relational rewrite of the 08-08 trio (contained;
   revisit if they recur); poll-interval settings seam; version bump (no production code changes;
   precedent: #277 was tests-only).

## Acceptance gates

- WP1: (a) nonexistent-model-path injection fails the test with the new evidence message showing
  EventId 526/525 + ledger rows (prove-the-check); (b) 3× isolated runs green locally.
- WP2: both tests green in targeted runs; the nightly's Class B sources closed.
- WP3: every R1 verdict path witnessed against synthetic trx (flake candidate / regression /
  ledgered / mass failure / unclassifiable); branch-nightly dispatch on the PR shows the summary
  line, uploads nothing on green, and files an issue on a forced red.
- Whole: full local suite green; PR CI (build-fast/bdd/slow) green; branch nightly green.
