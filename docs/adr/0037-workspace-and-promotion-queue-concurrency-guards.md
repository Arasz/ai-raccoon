# 0037. Workspace and Promotion-Queue Concurrency Guards

Date: 2026-08-14

## Status
Accepted

## Context
WP5b of the code-quality review named three data-integrity defects, all shaped by concurrency or a
missing uniqueness guard.

**DA-F1 (HIGH), workspace writes have no uniqueness guard.** `MemorySchema.BucketIndexDdl` declares
`uq_entries_shared_bucket … WHERE scope = 'shared'` and `uq_entries_committed_bucket … WHERE scope
IN ('project','custom')`. The `entries` table's own CHECK constraint is `(workspace_id IS NULL AND
scope IN (...)) OR (workspace_id IS NOT NULL AND scope IS NULL)` — a workspace row always carries
`scope IS NULL`, which neither partial index can ever match. `MemorySql.InsertEntry`'s bare `ON
CONFLICT DO NOTHING` therefore has nothing to conflict against for a workspace write, and a retried
`memory_write` into a workspace silently created a duplicate row — both got embedded (paying
inference twice) and both appeared in that workspace's search results. One pre-existing test
(`SqliteMemoryStoreSchemaTests.OpenBank_MigrationLeavesWorkspaceRowsUntouched`) had encoded this gap
as intentional design, asserting workspace duplicates survive a migration with the comment
"workspace scope is unconstrained by design."

**A-F7 (MEDIUM), `CloseAsync` was an unguarded UPDATE with a TOCTOU window.**
`SqliteWorkspaceStore.CloseAsync` ran `UPDATE workspaces SET status = @status WHERE id = @id AND
project_id = @projectId` — no `AND status = 'Active'`, no affected-row check — and accepted any
`WorkspaceStatus` including `Active`. `WorkspaceService`'s `RequireActiveAsync` and `CloseAsync` each
opened their own connection and ran in sequence with the workspace's outbox operations
(`ListContextAsync`/`AddContentAsync`/`DeleteContextAsync`) in between: a concurrent consolidate and
discard on the same workspace both passed the active-check, both did their outbox work, and the last
`CloseAsync` won — the outbox could be both promoted and discarded, or discarded twice.

**A-F11 (MEDIUM), the promotion queue claimed by delete.** `PromotionQueueService.PromoteAsync`
reused `IPromotionQueueStore.DiscardAsync` (a `DELETE … RETURNING`) as its claim mechanism before
calling `IMemoryStore.ShareAsync`. Its own catch block admitted that any failure other than
`UnknownHashException` dropped the candidate permanently rather than re-queueing it — a locked
database or a disk-full write mid-`ShareAsync` destroyed a promotion candidate with no retry.

A fourth, smaller defect rode along in the same files: `SqlitePromotionQueueStore.RememberDiscardsAsync`
looped per-hash with no transaction, unlike its sibling `UpsertAsync` in the same file, which already
wrapped its loop correctly.

## Decision

**DA-F1.** Add `uq_entries_workspace_bucket ON entries(path, hash, workspace_id) WHERE workspace_id
IS NOT NULL` to the fresh-bank DDL, plus a v7 ladder step (`MigrateToV1Async` through `MigrateToV6Async`
already occupy 1–6) that dedupes existing workspace-scope duplicates first — survivor = earliest row,
mirroring `MigrateToV1Async`'s own bucket dedupe exactly — before creating the index, so creation can
never fail on a real bank. No change was needed to `SqliteMemoryStore.WriteAsync` or `MemorySql`: the
insert's bare `ON CONFLICT DO NOTHING` and its post-insert re-read (`SelectEntryByPathInBucket`,
filtered on `workspace_id`) already generalize to any bucket a partial unique index recognizes — the
gap was purely in the schema, not the write path. `CurrentVersion` moves to 8 in the same change (see
below), since both ladder steps landed in this wave.

**A-F7.** `Workspace` (in `AiRaccoon.Core.Isolation`) gains two real transitions, `Consolidate()` and
`Discard()`, each returning a new record in the corresponding terminal status and throwing
`InvalidOperationException` from any non-Active source — the state-machine invariant applied at the
domain level. `IWorkspaceStore` gains `TryCloseAsync`, an atomic compare-and-swap (`UPDATE … WHERE
status = 'Active'`, reporting whether it actually affected a row) added as a **default interface
method** rather than a breaking signature change: `IWorkspaceStore` has fakes in test files outside
this task's ownership (`MemoryToolsTests`, `MemoryToolsAccessModeTests`,
`MemoryToolsInstrumentationTests`), and this task must not edit files owned by the concurrent lane
that owns `SqliteMemoryStore.cs`/`MemoryTools.cs`. The default implementation forwards to the existing
`CloseAsync` and reports success unconditionally, so those fakes keep compiling and behaving exactly
as before; `SqliteWorkspaceStore` overrides `TryCloseAsync` with the real guard, and `CloseAsync`
itself now forwards to `TryCloseAsync` (ignoring the bool), so `CloseAsync(..., WorkspaceStatus.Active,
...)` throws `ArgumentOutOfRangeException` too. `WorkspaceService.ConsolidateAsync`/`DiscardAsync` now
call `TryCloseAsync` — the atomic claim — **before** any outbox operation, not after: the loser throws
`UnknownWorkspaceException` before `ListContextAsync`/`AddContentAsync`/`DeleteContextAsync` ever run,
so the outbox is consumed by exactly one winner rather than partially by both.

*Known limitation, not fixed here:* this closes the race on the workspace's own status row, but the
claim and the outbox work still run as two separate operations against two separately-opened
connections (`IWorkspaceStore` and `IMemoryStore` are different stores with no shared transaction
scope). A crash between a successful claim and the outbox work completing leaves the workspace
permanently terminal with an undrained outbox and no self-healing pass to finish it — unlike, for
example, `PromotionQueueService`'s propose loop, which self-heals a crash between upsert and eviction
on its next run. Making the whole sequence transactional would require `IMemoryStore` to accept an
externally supplied connection/transaction, which is out of this task's scope (`SqliteMemoryStore.cs`
is owned by a concurrent lane). Flagged for a future pass.

**A-F11.** `IPromotionQueueStore` gains `ClaimAsync` (`UPDATE promotion_queue SET claimed_at = ?
WHERE project_id = ? AND hash = ? AND claimed_at IS NULL`, atomic, same exclusivity the old
delete-based claim gave) and `ReclaimStaleClaimsAsync` (releases claims older than a threshold),
again both **default interface methods** — `IPromotionQueueStore` also has fakes outside this task's
ownership (`ExtractionMetricsTests`, `TestData.cs`). `ClaimAsync`'s default forwards to the existing
`DiscardAsync`; `ReclaimStaleClaimsAsync`'s default is a no-op. `PromotionQueueService.PromoteAsync`
now claims with `ClaimAsync` instead of `DiscardAsync`, only removes the row once the outcome is
resolved (promoted, absorbed, duplicate-skipped, or a genuinely dead `UnknownHashException`), and
sweeps stale claims (`ReclaimStaleClaimsAsync(TimeSpan.FromMinutes(5))`) once at the start of every
`PromoteAsync` pass — a caller that claimed a row and then died or hung mid-`ShareAsync` never gets to
unclaim it itself. A new v8 ladder step adds `promotion_queue.claimed_at INTEGER NULL`; existing rows
backfill to `NULL` (unclaimed) via SQLite's own `ADD COLUMN` default.

*Considered and rejected:* the review also named a simpler alternative — inside `PromoteAsync`'s catch
block, re-insert the row when the exception is not `UnknownHashException` (~4 lines, no schema
change). Rejected because it does not survive the two failure modes the finding itself names as
motivating: a process crash or unhandled abort between the claim and the catch block still loses the
row exactly as before, and a genuinely disk-full `ShareAsync` failure is just as likely to fail the
re-insert `INSERT` as it was to fail the share — the "fix" and the failure share a root cause. The
claim-by-update row never leaves the table at all, so neither failure mode can lose it.

**Fourth defect.** `SqlitePromotionQueueStore.RememberDiscardsAsync`'s per-hash loop now runs inside
one transaction, exactly matching `UpsertAsync` in the same file.

## Consequences
- **Positive:** A retried `memory_write` into a workspace collapses to one row, matching the shared
  and committed buckets' existing behavior.
- **Positive:** A concurrent consolidate and discard on the same workspace now has exactly one
  winner; the loser never touches the outbox.
- **Positive:** A transient `ShareAsync` failure (locked database, disk full) leaves a promotion
  candidate reclaimable instead of destroying it.
- **Positive:** Both new store interfaces stayed non-breaking for concurrent lanes' test fakes, via
  default interface methods rather than signature changes.
- **Negative:** `SqliteMemoryStoreSchemaTests.OpenBank_MigrationLeavesWorkspaceRowsUntouched` and
  `UniqueIndex_AllowsSamePathDifferentHash_AndWorkspaceDuplicates` (owned by the lane that owns
  `SqliteMemoryStore.cs`, not edited by this task) now fail: both assert the pre-fix behavior
  (workspace duplicates surviving a migration / a duplicate workspace insert succeeding) as
  intentional. They need updating to reflect DA-F1's corrected invariant.
- **Negative, not fixed here:** the claim-then-outbox sequence for workspace close is not fully
  transactional across `IWorkspaceStore` and `IMemoryStore` — see the "known limitation" note above.
- **Negative:** `CurrentVersion` moves from 6 to 8 in one wave (two ladder steps); a bank opened by an
  older binary after this ships will correctly refuse to write per ADR-0019's forward-version guard.
