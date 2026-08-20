# Nightly 2026-08-20 — MoE plan (reviewed by QA + 3 expert lanes)

Source evidence: docs/work/2026-08-20-nightly-failure-list.md (8 findings, QA-confirmed
deleg_ab42d337); CI log tmp/logs/logs_87634383161/0_full-suite.txt; run 32326674365.
MoE lanes (all ran in parallel on 2026-08-20, all deepseek-v4-flash — same-model MoE, note
the reduced model diversity): test-engineer (fix-the-tests), architect (remove-vs-MTP),
code-reviewer (triage infra). Each lane verified the code independently; lane reports are in
the delegation transcripts; this file is the synthesis and the decision.

## The six failures share three root causes

1. **F1-F5 (5 failures, one fixture race)**: every CLI command auto-starts a full server
   (ADR-0075 §5.1); the server's maintenance startup pass stamps `maintenance_jobs` for every
   job whose ledger row is missing (`IsDue` true on NULL `last_run_at`,
   MaintenanceJobRunner.cs:97-100). The fixture's warm-up does not guarantee the pass
   completes; on the starved runner it is cancelled at process exit mid-flight, leaving a
   PARTIAL ledger; each subsequent command's server then re-runs the un-stamped jobs inside
   the command's observed window. F5's twist: promotion-queue-prune (due by missing row)
   deleted the seeded orphan before the CLI's GET report, so the CLI found 0 orphans and
   returned 0 WITHOUT writing its outbox request — exactly why the changed set was
   [maintenance_jobs, promotion_queue] with no outbox table.
2. **F6 (1 failure)**: SyncServiceGateContentionTests `Patience = 15 s` wall-clock timeout
   under full-suite load (observed 17.9 s). Semantic assertions are event-driven; the timeout
   only bounds waiting (and doubles as deadlock detection).
3. **F7/F8 (infra)**: the triage rerun filter carries xunit theory display names (spaces) →
   MSB4177 at the MSBuild layer (reproduced locally) → rerun never ran → all 6 failures
   unclassifiable; and `gh issue create --label nightly` failed silently because **the
   `nightly` label does not exist in this repo** (422, rc swallowed by the script).

## Option A — remove dev-machine-only tests from the nightly: REJECTED

All three lanes agree: **none of the six failures qualifies as dev-machine-only**.
- CliBankWriteTests is a regression detector, not a snapshot: ADR-0075 calls the zero-CLI-
  writes gate "the check the whole single-writer claim rests on… the one that must be watched
  failing". It is `Speed=Nightly` — removing it from nightly means it runs NOWHERE
  automatically (the derive-or-delete trap); the WP7-T1 plan (docs/plans/2026-08-16-bank-open-
  cost-implementation.md, gate line 593) always intended it PR-reachable; the `Nightly`
  placement diverged from the plan and the load-sensitivity is a fixable fixture bug, not an
  argument for removal.
- SyncServiceGateContentionTests already runs in PR build-slow; the nightly failure is load,
  not a logic issue.
- The manual-checklist skill is explicitly NOT an honest home (its "When NOT to use": anything
  `dotnet test` already covers). An on-demand workflow is effectively a dead gate (repo
  history: a gate nobody is forced to run is not a gate). known-flakes ledgering would be
  dishonest for deterministically fixable bugs.

## Option B — migrate to MTP + retry extension: NOT as the fix

- MTP fixes exactly one mechanism (F7: the filter reaches the test host directly, MSB4177
  impossible) — and that one is a one-line fix in vstest today. It does NOT touch the ledger
  race (F1-F5) or the patience timeout (F6).
- `--retry-failed-tests` MASKS the other five: retry converts a deterministically fixable bug
  into "flake candidate → ledger → quarantine" — the wrong order of operations; it also
  conflicts with the triage script's record-and-tolerate design (MTP writes per-attempt trx;
  the first-attempt failure never reaches the ledger).
- Cost for THIS repo is materially higher than jsaa's: Reqnroll.xunit.v3 3.3.4 (no jsaa
  precedent) registers a custom TestFramework + runner hooks — exactly the xunit 4.0.0
  extensibility surface that broke; the BDD lane (required check, 138 scenarios) is at risk
  with no upstream fix available. Effort ~1.5-3 days including a mandatory Reqnroll spike.
- Verdict: adopt later, deliberately, WITHOUT the retry extension, gated on a Reqnroll spike
  passing. The jsaa how-to (job-search-ai-assistant/docs/how-to/migrate-to-microsoft-testing-
  platform.md) is the playbook; this failure list is not the reason.

## Option C — fix-now (RECOMMENDED): three root-cause fixes + two triage fixes, one PR

All tests-only (no version bump, #277 precedent) + one script. No production code changes.

**WP1 — fixture: complete maintenance ledger by direct pre-stamp (F1-F5)**
- In CliBankWriteTests.InitializeAsync, after OpenBankAsync: INSERT the 9 ledger rows
  (last_run_at = now, run_count = 1, ON CONFLICT DO NOTHING), names taken from the public
  `JobName` constants of the registered jobs (NOT hardcoded strings — correct names:
  chunk-backfill, vec0-reclaim, vacuum, metrics-retention, model-migration,
  repair-chunk-index, repair-reingest, promotion-queue-prune, pending-embed).
- Direct stamp (not poll-and-rerun): the poll variant is still scheduler-dependent — a
  starved pass can be cancelled even at 0 stamps; the stamp makes the race impossible by
  construction. Keep the warm-up + settle as cheap absorbers.
- Drift guard: new Fast unit test asserting the DI-registered job list equals the fixture's
  stamp-name set (pattern: ChunkIndexRepairDoesNotAutoStartTests.cs resolves the real
  container) — a job added later fails PR CI, not next nightly.
- Acceptance: RED = the 2026-08-20 nightly F1-F4 log rows (witnessed red); cross-check RED by
  temporarily inserting a bogus stamp name (then restore). GREEN = class ×3 isolated, full
  local suite, PR CI (new Fast test), dispatched branch nightly.

**WP2 — apply tests: composite assertion tolerant of in-window outbox consumption (F5 tail)**
- Replace `changed.ShouldBe([expectedOutboxTable])` with a pure, unit-tested predicate:
  1) the outbox request row EXISTS (the CLI's own write — F5's failure was "request never
  written"); 2) changed ⊆ {outbox, maintenance_jobs} ∪ target tables (prune → promotion_queue,
  repairs → entries); 3) IF a target domain table changed THEN maintenance_jobs changed (the
  mutation went through the maintenance loop, never a synchronous-only CLI write).
- Rogue-CLI proof test: seed orphan, DELETE from promotion_queue directly (no request) → the
  composite must FAIL. Truth-table unit tests for the predicate (5 shapes).
- Rationale (QA): the exact-match form is already unsound for ≥15 s commands (the poll may
  consume in-window by design); the composite keeps the gate's honest claim.
- Acceptance: TDD (predicate unit tests RED first), rogue-proof witnessed RED→GREEN, class +
  suite + branch nightly green.

**WP3 — sync gate patience (F6)**
- One line: Patience 15 s → 60 s (SyncServiceGateContentionTests.cs:22; covers barrier waits,
  cycle1Entered, WhenAll in both methods). No semantic loss; keep the deadlock-detection role
  noted. Precedent: WP2 of the 08-19 review (IdleTimeout 15→30 s at 20.6 s worst).
- Acceptance: class ×5 isolated, full suite, PR build-slow, branch nightly.

**WP4 — triage rerun: class-level filters + rerun-only guard (F7)**
- Derive class FQNs from trx testNames: `fqn.split("(", 1)[0].rsplit(".", 1)[0]`, deduped
  (design + edge-case table in the lane report; verified: class filter selects 30 tests with
  no MSB4177; `+`/backtick shapes MSB4177-safe; regex guard `[A-Za-z0-9_.+`]+` fails the rerun
  to red on any future odd shape).
- Rerun-only guard: a failure in the class rerun that was NOT in the first run's failed set
  (and not ledgered) ⇒ red/unclassifiable, named in the summary + classification.json —
  without it, class-level reruns could misclassify first-run failures as flakes.
- Extend scripts/tests/test_nightly_triage.py (synthetic trx; the 6 real testNames → 2
  classes; no-space invariant; rerun-only scenarios; gh rc surfacing).

**WP5 — triage gh: label, clean FQNs, error surfacing (F8)**
- Root cause: the `nightly` label does not exist → gh issue create 422, swallowed. Fix:
  idempotent `gh label create nightly --force` first (label-less fallback: title prefix
  `[nightly] `); dedupe search on the method FQN (`split("(")[0]` — no search operators, no
  256-char limit) with `in:title`; `gh()` returns the CompletedProcess; every nonzero rc is
  printed and folded into the summary line; file_or_comment_issue returns a note appended to
  the summary.

**Sequencing**: WP4+WP5 are independent of WP1-WP3 (script vs tests) — parallelizable.
One PR (tests + script + their unit tests); branch nightly dispatch to prove the workflow
end-to-end before merge; CI build-fast/bdd/slow + next scheduled nightly as the final gate.

## Decision

Do the fix-now package (Option C). Reject Option A (no honest home; kills the ADR-0075
gate). Defer Option B to a separate task, without the retry extension, gated on a Reqnroll
spike — the how-to is written, the failure list is not the reason to migrate.
