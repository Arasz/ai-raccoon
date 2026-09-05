# ADR-0100: the project-ids repair folds every committed scope, including NULL-context rows

**Status:** accepted · **Date:** 2026-09-05 · **Task:** `air-run-once-repair-fully-converges`

## Context

`ProjectIdsFoldPlan.OwnsMoveableContent` scheduled a fold whenever a loser owned
committed rows (`EntryTotal > 0` = `project` + `custom` + `shared` scopes), but
`ProjectIdsRepair.FoldEntriesAsync` moved only `LabeledProjectScope` rows
(`scope = 'project'` with a non-NULL `context_label`, the d-426 keep predicate).
A repair `--apply` drained its request (261 s on the live bank) yet re-planned
identical folds forever: guid losers owning only NULL-context rows, and
`job-search-ai-assistant`/`aib`/`pbi-badger-integration` owning only
custom/shared rows, never moved a single row. The planner/applier predicate
mismatch made convergence unreachable no matter how often the repair ran
(air-merge P2, #603; not a recent regression).

## Decision

- The applier folds the **committed predicate**: `scope IN ('project', 'custom')`,
  any label including NULL-context bulk rows — via a new named
  `ProjectRows.CommittedScope` (single home; `ProjectRows.Scopes` untouched, still
  the search/visibility definition). Winner-dedup mirrors
  `uq_entries_committed_bucket` exactly
  (`path, hash, project_id, scope, COALESCE(context_label, '')`), no tombstone on
  dedup-collapse; vec invalidation extended to the same predicate;
  queue-before-entries ordering proven against `promotion_queue_entries_ad`.
- The d-426 NULL-keep is overturned: no consumer keys on
  `(project_id, NULL-label)` stability, and the dedup subquery is NULL-safe.
- `shared` rows never fold: `uq_entries_shared_bucket` is `(path, hash)` global
  with no project key, so shared content is cross-project by design (H9 verified
  the write path keeps the writer's `project_id` on fresh schema — shared-keyed
  loser rows are genuine, not legacy). Shared-only losers pin with a reason.
  The sync pull and push arms share this fold domain (integration review #622:
  pull is project+custom like the applier, push re-attributes nothing shared) —
  one domain in plan, applier, pull, and push.
- `OwnsMoveableContent` is redefined as exactly the applier's executable
  predicate — a planned fold can never execute as zero moves (D2 honesty rule).

## Consequences

**+** The repair converges: every fold the plan schedules, the applier drains;
  second-run-no-op (`TotalChanges == 0`) holds.
**+** The BDD rename scenario asserts the new contract (mirrors fold
  byte-identical, winner key-set is the exact union incl. dedup absorption).
**−** One-time `project_id` churn on bulk rows at the first post-fix repair.
**−** The pre-D1 `LabeledProjectScope` helper is deleted (referenced nowhere).
**Neutral:** custom-scope rows keep their scope under the winner; shared rows
  stay loser-keyed and pinned, never silently dropped.

## Alternatives considered

- Narrow the planner to the old applier (rejected: converges the loop but leaves
  loser-owned committed content searchable under retired ids — redefinition, not
  repair — and contradicts P3).
- Force custom/shared residue onto the dropped list (rejected: destroys
  preservable content and mints tombstones against the create-only-for-dropped rule).
- Fold `shared` too (rejected: steals cross-project content; structurally suspect).
