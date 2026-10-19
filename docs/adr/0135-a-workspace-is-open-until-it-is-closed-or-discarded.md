# ADR-0135: A workspace is open until it is closed or discarded

**Status:** accepted · **Date:** 2026-10-19 · **Task:** `air-fix-repair-open-workspaces-census`

## Context

ADR-0101 decided that open workspaces block a fold: a loser that owns live workspace scratch has
to wait, because scratch never moves across projects, and folding the loser's committed rows
would strand that scratch under an id no read path resolves.

The census could not tell an open workspace from a closed one. It counted workspace ROWS
(`SELECT project_id, COUNT(*) FROM workspaces GROUP BY project_id`) and the plan tested
`row.Workspaces > 0`. On the owner's bank the alias id `job-search-ai-assistant` held 14
workspace rows, every one of them closed (11 `Closed`, 3 `Discarded`), so the repair pinned the id
with the reason "owns 14 open workspace(s)" and its 25 project-scope and 65 custom-scope rows
stayed under a spelling that `memory_get` folds away before it looks. The rows were in the bank
and unreachable: the read path resolves the caller's id to the canonical one, so a row keyed
under the alias matched nothing under either spelling.

## Decision

- A workspace is **open** unless its status is terminal. `Closed` and `Discarded` are terminal;
  everything else is open. The comparison ignores case, because the production writer stores
  `Active`/`Closed`/`Discarded` while older rows and test seeds carry other spellings (`open`,
  `active`). A status the census cannot read counts as open: state it cannot read is state it
  must not strand.
- The census reports the live view next to the complete one: `OpenWorkspaces` and
  `OpenWorkspaceEntries` (the scratch rows living in an open workspace) join the existing totals,
  which stay because `AttachmentCount` must keep counting every attachment a row owns.
- The fold plan blocks on the open counts, never the totals. The pin reason names the open
  workspaces and the live scratch rows; a pin for an id that owns only closed workspaces says
  that instead of claiming live scratch.

## Consequences

**+** A loser whose workspaces have all closed folds. Its committed rows move to the canonical id
on the next repair run and are reachable again.
**+** The pin reason is true. It counts live scratch, not rows, so an operator reading it learns
  what actually blocks the fold.
**−** The census runs one more SELECT: the open workspaces joined to their scratch rows through
`idx_entries_workspace`.
**Neutral:** An open workspace blocks the fold exactly as before, and workspace rows still never
move across projects.

## Alternatives considered

- Count exactly `Active` (rejected: banks carry `open` and `active` spellings, and a status the
  census cannot read must not be assumed terminal).
- Delete closed workspace rows during the repair (rejected: the repair never moves or deletes
  workspace rows, and those rows are the evidence of what the id owned).
- Leave the census alone and let the operator edit the alias map (rejected: the pin was never
  about attribution — the id was attributed all along, and the block was a count).
