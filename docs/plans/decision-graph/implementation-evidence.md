# Native decision graph implementation evidence

Implementation PR: #923. Runtime plan `air-native-project-decision-graph-implementation`.

## Model

- Legal transition no-op: 4 failed / 4, then 4 passed / 4.
- Terminal amendment no-op: 3 failed / 28, then legal/terminal suite green.
- Invalid-selector stub: 3 failed / 3.
- Dependency-cycle validation no-op: 1 failed / 1.
- Confidence validation no-op: 4 failed / 4.
- Independent reviewer reproduced 56 passed / 56 and removed edge-kind filtering: 1 failed / 56.
- Reviewer found source-count and exposed-collection test gaps. Added targeted tests; removing source-count guard and returning a mutable copied array produced 2 failed / 13. Restored production yielded 58 passed / 58.
- Core has no HTTP or persistence dependencies. Static helper classes contain only pure validation/graph functions.

## Storage in progress

Fresh-bank table test: 1 failed / 1 before DDL; 1 passed / 1 after schema v23 tables. Storage suite now passes 9/9, covering reopen, cross-project FK refusal, read snapshot consistency, write rollback, removal/import metadata, v22-to-v23 migration, encryption and rekey. A NULL change-reason constraint test failed against SQLite's three-valued CHECK and then passed after explicit IS NOT NULL validation.

Legacy schema and sync consumers passed 27/27 after linking the worktree's missing model data asset from the existing main checkout. The encrypted rekey fixture performs its missing-key probe after rekey so a separate wrong-key connection pool does not hold the file during exclusive rekey.

Cloud exclusion is checked independently by the semantic condition that no table starting with decision survives any push snapshot. Removing graph exclusions caused 6 failures/15 sync cases; restoring them yielded 15/15 passing. Independent storage review completed at 6fb628bd with zero blockers/major findings. Project lookup, read snapshot, removal task and rollback mutants were killed; the restored storage/sync run passed 24/24.

## PR review corrections in progress

Review 5654842632 identifies stale status docs, occupied project repair and stale revision error classification. Status docs and graph snapshot now reflect actual milestones. Retirement and revision-conflict checks were observed red before correction and passed afterward in an 18-test focused run. HTTP 409/400 parity remains an adapter gate. The populated FK-enforced snapshot test failed when table reversal was removed (1/1) and passed after restoration (1/1). CI also exposed missing retry attributes on new integration tests and a cold-open DDL statement budget changed by twelve new schema statements; both consumer contracts are being updated with focused verification.

## Repair concurrency correction

Independent review reproduced a real graph-owner retirement race without production mutations: a graph task committed at the clock seam after preflight, then repair deleted its registry row (1/1 probe failed). Five new checks first failed: two nested-transaction cases, graph registration refusal, queued writer registration recheck, and SQLite-traced transactional graph preflight.

Repair now runs preflight plus all rewrites in one BEGIN IMMEDIATE transaction, existing nested bank writes use savepoints, and graph writers recheck registration after acquiring their write lock. The combined transaction/decision/project-fold/populated-snapshot run passed 40/40. Owner fixture setup now registers real project rows. A follow-up independent review is pending.

Service registration/record/replay/read passed its first behavior test; amendment/stale replay/concurrency passed 3/3. Four missing mutation-operation cases then went red, and implementing lifecycle transitions, links/removals and supersession produced 7/7 passing service tests, including an injected SQLite failure after edge insertion that proves all-or-none supersession and retry receipts.

## Service acceptance

The eight `service-contract` checks live in `tests/AiRaccoon.Tests/Integration/Decisions/DecisionServiceTests.Acceptance.cs`. Four overlapping view tests were folded into them. Each mutation came from the `implementation.md` table and was applied to production code. After each one the whole solution was rebuilt and `DecisionServiceTests` was run (21 tests). The named check went red every time:

| Mutation | Failed / 21 | Named check red |
|---|---|---|
| Tasks sorted by latest edit revision, not `registered_order` | 5 | LastUsesRegistrationOrder, EmptyNewestTaskRemains |
| Tasks selected only through an origin join to decisions | 5 | EmptyNewestTaskRemains |
| Version-touch membership dropped | 1 | RevisionAndEdgeTouchCount |
| Edge-touch membership dropped | 2 | RevisionAndEdgeTouchCount |
| Boundary stubs dropped | 1 | BoundaryClosureAndHistory |
| History filtered to selected-task versions | 2 | BoundaryClosureAndHistory |
| Read transaction removed (writer interleaved after the revision read) | 1 | ConcurrentWriterSingleSnapshot |
| Omission counts suppressed | 3 | CountsAndUnicodeBudget, ManyEmptyTasks |
| Excerpt sliced by UTF-16 unit | 2 | CountsAndUnicodeBudget |
| `removal_task_id` persisted as NULL | 3 | TaskProvenanceSurvivesRestart |
| `removal_task_id` dropped on read | 2 | TaskProvenanceSurvivesRestart |
| Task headers emitted without the budget check | 1 | ManyEmptyTasks |

With production restored, `DecisionServiceTests` passed 21/21 in four separate runs and `RetrySurfaceGateTests` passed 2/2. No production defect was found. Traversal termination is still covered by `Graph_preserves_causal_direction_and_explicit_depth_and_node_bounds`.

## Review fixes

An adversarial review of the pull request confirmed five findings. Each fix below was seen red before the change and green after it.

| Finding | Check | Red | Green |
|---|---|---|---|
| Decision-only project planned for retirement, so the repair request threw on every maintenance pass | `DecisionProjectRepairTests.Census_pins_a_registered_project_that_owns_only_decisions_instead_of_retiring_it`, `DecisionProjectRepairJobTests.Repair_request_folds_unrelated_ids_and_finishes_when_a_project_owns_only_decisions` | 2/6 | 6/6 |
| Required text measured untrimmed; identifiers accepted U+202E, U+200B, U+2028, U+2029, U+00AD and padding | `DecisionTextTests` | 8/14 | 14/14 |
| Cyclic or self link with a stale revision returned a revision conflict | `DecisionServiceTests.Invalid_links_are_refused_as_invalid_even_with_a_stale_revision` | 1/22 | 22/22 |
| v23 ladder step re-ran decision DDL the digest-gated Ddl had already run | `DecisionBankLifecycleTests.Version_twenty_two_bank_migrates_without_losing_existing_settings` now checks every decision table; removing the decision DDL from the Ddl turned it red | 2/2 | 2/2 |
| `AmendAsync` read the clock directly | refactor only, covered by the existing service tests | n/a | 115/115 |

The census now counts decision graph rows per id as an attachment, so a graph owner is never retire-eligible. The repair still refuses to fold or drop a graph owner. After all five fixes, the project-id repair classes, every decision class and the repo-wide source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `RetrySurfaceGateTests`, `LoggerMessageEventIdTests`, `ProjectIdAliasDefaultCollectionGateTests`) passed 492/492.

The `INSERT ... SELECT` backfill and `decision_fts` rebuild remain in `DecisionSchema.Ddl`. They run once per schema digest change and no longer run a second time from the ladder.
