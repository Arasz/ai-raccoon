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
