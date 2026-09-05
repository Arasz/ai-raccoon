# ADR-0101: repair verdicts ignore telemetry; open workspaces block folds and retires

**Status:** accepted · **Date:** 2026-09-05 · **Task:** `air-run-once-repair-fully-converges`

## Context

`AttachmentCount` counted every non-entry surface, so two shapes could never
resolve: metrics-only ids (e.g. a 214-row `b0e32c16`) were neither foldable
(`OwnsMoveableContent` ignores metrics), droppable, nor retirable — they
surfaced as `need a human` forever; and a loser with committed rows *plus* open
workspaces folded its rows while stranding live workspace scratch under the
loser id — with the run-until-fixed loop pinning the workspace-only remainder
afterwards as if the block had held all along (the exact silent-dishonesty class
D2 exists to kill).

## Decision

- `metrics`/`noise` rows are **verdict-invisible**: excluded from
  `AttachmentCount` for retire/unresolved verdicts (regenerable derived data, cf.
  ADR-0098), re-keyed to the winner by a trivial `UPDATE` on fold (verified: no
  triggers, FTS, or vec shadows on either table). Unresolvable metrics-only ids
  pin `pinned-telemetry-only`, convergence-neutral.
- `workspaces`/`WorkspaceEntries` **never move across projects** (isolation
  invariant): the workspace check precedes the fold in both attribution
  branches, so an otherwise-foldable loser with open workspaces pins
  `pinned-open-workspaces` with a reason; retire never fires while they exist.
  The loop classifies open-workspace pins as *waiting* (writer-activity), not stuck.
- The census stays SELECT-only (query-only gate green).

## Consequences

**+** No id blocks convergence on telemetry alone; workspace scratch can never
  be stranded by a fold-then-pin sequence (mixed-shape test pins, never folds).
**+** Retire stays honest: registered metrics-only ids retire (registered-empty
  shape owns that verdict), unresolvable telemetry-only ids pin with reasons.
**−** Telemetry history follows the winner on fold (accepted: regenerable).
**Neutral:** workspace byte-identical post-fold proven at apply level.

## Alternatives considered

- Count telemetry toward verdicts (rejected: metrics-only ids block convergence
  with content the repair deliberately never moves).
- Move workspaces with the fold (rejected: cross-project scratch migration —
  asymmetric value-at-risk, live user state).
- Retire despite open workspaces (rejected: deletes the registry row live
  scratch still references).
