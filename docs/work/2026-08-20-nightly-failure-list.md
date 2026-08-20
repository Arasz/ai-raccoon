# Nightly failure list — 2026-08-20 (run 32326674365, commit dc5451b7)

Run: `nightly` workflow, full-suite job, ubuntu-24.04, 03:00 UTC. Suite: 3617 passed /
6 failed / 10 skipped. Triage rerun crashed with MSB4177, so all six failures were reported
unclassifiable; no nightly issue was filed despite the script attempting it.

## F1–F4 — CliBankWriteTests.ReadCommand_CommitsNothingToTheBank (4 rows)

Failing tests (all one theory):
- `(label: "settings noise show")`
- `(label: "settings extract list")`
- `(label: "repair reingest")`
- `(label: "noise entries")`

Failure: `'…' committed a write to the bank; tables changed: maintenance_jobs`
(observer `PRAGMA data_version` also moved).

Code touched:
- tests/AiRaccoon.Tests/Integration/Setup/CliBankWriteTests.cs (Speed=Nightly — runs ONLY in
  the nightly; first nightly after PR #391; never ran in PR CI)
- src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs (startup pass +
  15 s on-demand poll)
- src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs (stamps maintenance_jobs)
- src/AiRaccoon/Setup/AppRegistrations.cs:154-179 (the 9 registered jobs)

Probable reason (root cause, established):
- Every settings/read command auto-starts a full server (ADR-0075 §5.1). The server's
  maintenance startup pass runs all jobs whose `maintenance_jobs` ledger row is missing
  (`IsDue` returns true when `last_run_at` is NULL — MaintenanceJobRunner.cs:97-100) and stamps
  the ledger on each run.
- The fixture's warm-up (`settings sweep show`) starts one server, then waits only for the bank
  to settle (two stable snapshots). On the starved runner the warm-up server's pass is CANCELLED
  mid-flight by shutdown (StopAsync cancels the pass token between jobs), leaving a partial
  ledger; the settle sees a stable-but-incomplete ledger and proceeds.
- Every subsequent command's server then re-runs whichever jobs have no ledger row, inside the
  command's observed window → `maintenance_jobs` digest changes → FAIL. Once the ledger is
  complete, commands stop failing (CI log: last read failure 03:08:29, none after).
- Locally the warm-up pass completes (verified: all 9 rows stamped, run_count=1) → 24/24 green,
  multiple consecutive unloaded runs. (QA correction: the class has 24 test rows, not 25; the
  pass is cancelled at process exit — `base.StopAsync` — so `WaitForBankToSettleAsync` is
  vestigial for this failure mode: it starts polling only after the process is gone and can
  never observe the cancelled pass; it also silently gives up after 30×100 ms with no failure,
  a latent trap for fixture changes.)

## F5 — CliBankWriteTests.ApplyCommand_OnlyCommitsAnOutboxRequest_NeverTheDomainTableDirectly

Failing test: `(label: "extract prune --apply", argv: [extract, prune, --apply], expectedOutboxTable: promotion_queue_prune_requests)`

Failure: `'extract prune --apply' must commit only its outbox request (promotion_queue_prune_requests); changed instead: maintenance_jobs, promotion_queue`

Code touched: same as F1-F4 + src/AiRaccoon.Infrastructure/Maintenance/PromotionQueuePruneJob.cs
+ src/AiRaccoon/Settings/PromotionQueuePruneEndpoint.cs + ExtractCommands.PruneAsync.

Probable reason (same root cause, one twist):
- The test seeds an orphaned promotion_queue row, then runs the command. On CI the ledger was
  still partial, so the command's server startup pass ran promotion-queue-prune (due by missing
  ledger row, no request needed): `DeleteOrphans` removed the seeded orphan, the ledger got
  stamped — all inside the command window.
- The CLI's own flow (`extract prune --apply` = GET report → if 0 orphans, return 0 WITHOUT
  writing the request; else POST the outbox request) then found the orphan already gone and
  returned exit 0 having written NOTHING → `promotion_queue_prune_requests` never changed,
  which is exactly why it is absent from the changed set. The outbox discipline held; the
  maintenance loop pre-empted the test's precondition.
- Verified the normal path manually: with a complete ledger the request is written, the orphan
  stays, the job has not run by command exit (request row `finished_at IS NULL`).

## F6 — SyncServiceGateContentionTests.ConcurrentMemorySync_RepeatedRuns_BothCallersAlwaysSucceed

Failing test: `(run: 2)` — `System.TimeoutException: The operation has timed out` at line 241.

Code touched: tests/AiRaccoon.Tests/Integration/Sync/SyncServiceGateContentionTests.cs
(Speed=Slow — runs in nightly AND PR build-slow); src/.../Sync/SyncService.cs (the gate under test).

Probable reason:
- Wall-clock timeout, not a logic failure: `Patience = 15 s` bounds the whole `Task.WhenAll`
  of two concurrent sync cycles (each cycle = VACUUM + integrity check + pull + merge + push +
  watermark on a real SQLite file). 5 theory runs × 2 cycles = 10 cycles; on the starved
  runner run 2 exceeded 15 s. The timeout guards nothing time-sensitive — the assertions that
  matter (mutual exclusion, both succeed, FIFO) are semantic.
- Same `Patience` in the sibling test `ConcurrentMemorySync_SecondCallerWaitsForFirst…` (passed
  this night, same risk).

## F7 — Triage rerun crash: MSB4177 (infra bug, masked all classification)

Failure: `MSBUILD : error MSB4177: Invalid property. The name "settings extract list, argv: [settings, extract, list])|…" contains an invalid character " ".`

Code touched: scripts/nightly-triage.py (build_filter at line 109, run_dotnet_test at line 123).

Probable reason:
- `dotnet test --filter` values containing spaces (xunit v3 theory display names — the trx
  `testName` carries `(label: "settings noise show", argv: […])`) break at MSBuild argument
  parsing in the vstest path. Reproduced locally with the exact nightly command shape.
- Consequence: the rerun never executed → every failure classified "unclassifiable" → exit 1 →
  no flake/regression ledgering. The classification honesty held (unclassifiable, not flake),
  but the pipeline's core value was lost.

## F8 — Nightly issue auto-filing silently produced nothing

The script ran `gh issue create` (GH_TOKEN set, issues: write granted) after the unledgered
red; no open or closed issue with the `nightly` label exists. The script ignores gh's
returncode and stderr (`gh()` captures stdout only), so the failure mode is invisible.

## Regression candidates / context

- state.json already flagged: "CliBankWriteTests.ReadCommand_CommitsNothingToTheBank remains
  load-sensitive under concurrent local runs; worth filing." This nightly is the first to run
  the class (Speed=Nightly, added 2026-08-16, nightly triage only since 2026-08-19).
- known-flakes.json does NOT exist at repo root (QA verified) — `load_ledger` treats it as
  empty; nothing ledgered yet.
- QA review (2026-08-20, deleg_ab42d337): all 8 findings CONFIRMED; fixes vetted SOUND with
  notes: (a) the exact-match apply assertion is already unsound for ≥15 s commands by design
  (the 15 s poll may consume in-window) — the tolerated-consumption form is the honest fix;
  (b) the rerun-diff guard is essential, without it a rerun failing different rows would
  misclassify first-run failures as flakes; (c) MTP `--retry-failed-tests` has a gate-honesty
  flaw: a retried-then-passed genuine regression would disappear into a green run that triage
  never sees — retried attempts must still surface to triage if adopted; (d) when raising the
  sync Patience, keep the timeout's deadlock-detection role stated.

## Proposed fix directions (for review)

1. Fixture (F1-F5): make the warm-up guarantee a COMPLETE maintenance ledger — poll until all 9
   registered jobs have a ledger row (run_count ≥ 1) instead of only "bank settled"; or stamp
   the 9 rows directly in the fixture. Then no command's server pass has any job due, and read
   commands stop writing. Deterministic, no production change.
2. Apply tests (F5 tail): with a complete ledger the request is written and consumed only by
   the 15 s poll; on an extreme tail (command ≥15 s) the poll can consume it in-window. Tolerate
   the consumed state while keeping the gate honest: assert the request row EXISTS (the CLI's
   own write) AND any domain-table change is accompanied by a maintenance_jobs stamp (the
   mutation went through the maintenance loop, never synchronous-only).
3. Sync tests (F6): raise Patience 15 s → 60 s in both methods; no semantic loss.
4. Triage script (F7): build the rerun filter from the CLASS FQN (strip the display-name
   suffix) instead of the full testName; treat failures in the rerun that were NOT in the first
   run's failed set as red/unclassifiable; surface gh's returncode/stderr (F8).
5. Alternative (owner option): migrate the test stack to MTP (jsaa how-to exists:
   docs/how-to/migrate-to-microsoft-testing-platform.md in job-search-ai-assistant; ADR-0099)
   + Microsoft.Testing.Extensions.Retry (`--retry-failed-tests 2`) — the MSB4177 filter bug
   disappears under MTP (filter reaches the test host directly) and flakes get built-in retries;
   but it is a large change (xunit.v3 4.0.0 breaking, Reqnroll.xunit.v3/ArchUnitNET compat,
   workflow + triage rewrite) and does not by itself fix the ledger race (a retried
   CliBankWriteTests row can fail again under load).
