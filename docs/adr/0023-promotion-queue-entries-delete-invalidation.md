# 0023 — Invalidate promotion_queue rows when their entry is deleted

Date: 2026-08-09

Status: Accepted.

## Context

`promotion_queue` rows (ADR-0007) reference `entries` rows by `(project_id, hash)`. Nothing
invalidated a queue row when the entry it points at was deleted or re-chunked. Confirmed live: 19
orphaned queue rows (17 ai-raccoon, 2 ai-badger), all pointing at watched ADR docs that were edited
and re-ingested — `SqliteMemoryStore.ReplaceFileAsync` deletes the old chunk rows and inserts new
ones under new hashes (content-derived, ADR native-memory FR-NM-7), and the queue kept the dead hash.

Downstream, `PromotionQueueService.PromoteAsync` dequeues a candidate before sharing it, then
`SqliteMemoryStore.ShareAsync` throws `UnknownHashException` on the dead hash — the candidate is
destroyed and the batch aborts. That failure mode is a separate work package's fix (making promote
resilient to a hash that is no longer live); this ADR is the cause, not the symptom: stop the queue
from holding dead references in the first place.

## Decision

A single `AFTER DELETE ON entries` trigger, `promotion_queue_entries_ad`, deletes the matching
`promotion_queue` row whenever an `entries` row is deleted:

```sql
CREATE TRIGGER IF NOT EXISTS promotion_queue_entries_ad AFTER DELETE ON entries BEGIN
    DELETE FROM promotion_queue
    WHERE project_id = OLD.project_id AND hash = OLD.hash
      AND NOT EXISTS (SELECT 1 FROM entries e
                      WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash);
END;
```

Plus a one-shot CLI verb, `ai-raccoon extract prune`, to clear rows that were already orphaned
before the trigger existed — the trigger only covers deletes from here on.

### A trigger, not per-call-site invalidation

`SweepService.SweepAsync`, `SqliteMemoryStore.DeleteAsync`, `DeleteContextAsync`,
`DeleteSourcePathAsync`, `ReplaceFileAsync`, and `SyncService`'s merge all delete `entries` rows,
and any future deleter would join that set. Invalidating the queue at each call site is a
hand-maintained list mirroring "every place that deletes an entry" — it drifts the moment a new
deleter is added and nothing notices ("derive the list, or delete it"). A trigger is enforced by
the database engine against the one thing that actually matters (a row left `entries`), so no call
site can forget it and no future call site needs to remember it.

### The `NOT EXISTS` guard is load-bearing

`uq_entries_committed_bucket` (`MemorySchema.cs`) permits the same `(path, hash)` under two
different `(project_id, scope, context_label)` buckets of one project — the same content committed
to two contexts is two `entries` rows sharing a hash. Without the guard, deleting one such sibling
would drop a queue candidate the surviving sibling still backs. The guard makes the trigger check
"is this hash still live for this project at all", not "did this exact row survive" —
`WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash` re-runs the same predicate the queue
row itself was upserted against.

Proven, not assumed: the guard was implemented in two steps. First the trigger without the
`NOT EXISTS` clause, confirmed to fail `DeletingOneOfTwoEntriesSharingAHash_KeepsTheQueuedCandidate`
(`tests/AiRaccoon.Tests/Unit/storage/PromotionQueueInvalidationTests.cs`) — deleting one sibling
dropped the queue row the other sibling still backed. Then the clause was added and the same test
went green.

### Shared-scope deletes are inert, by construction

`OLD.project_id` is `NULL` for shared-scope `entries` rows (`project_id TEXT NULL`), and SQL's
`project_id = NULL` never matches any row — not even another `NULL`. So deleting a shared-scope
entry never touches `promotion_queue`, which is correct: promotion dequeues its own row explicitly
(`PromotionQueueService.PromoteAsync` claims the row with a discard *before* it shares), and a queue row is
always project-scoped, never shared-scoped, so there is nothing for a shared delete to invalidate.
This is a consequence of the predicate, not a special case written for it.

### No `CurrentVersion` bump

`MemorySchema.Ddl` runs unconditionally on every bank open (`MemorySchema.EnsureAsync`), with
`CREATE TRIGGER IF NOT EXISTS`, before the version ladder is consulted. An additive, idempotent
trigger reaches every existing bank on its next open with no migration step and no ladder entry.

**Do not "fix" this by bumping `CurrentVersion`.** ADR-0019's forward-version write guard refuses a
read-write open when the stored version is ahead of the binary's `CurrentVersion` — bumping the
version here would make every older binary that opens a bank already touched by this build throw
`schema-version-unsupported`, including the shared bank multiple projects/agents open concurrently,
for a change that needed no migration at all. The version ladder exists for changes that require
one; this one does not.

## `extract prune`: catching up rows the trigger predates

The trigger only fires on deletes from here on — the 19 rows already orphaned on the live bank
predate it and need a one-shot sweep. `ai-raccoon extract prune` reports per-project orphan counts
by default (dry run, matching `memory_sweep`'s dry-run-by-default posture) and removes them with
`--apply`. Both modes run the same `NOT EXISTS`-based query the trigger uses, so "orphan" means the
same thing in both places. Idempotent: a second `--apply` run finds nothing left to report, because
the first run already removed every row the query could find.

`PruneOrphansAsync` is a concrete method on `SqlitePromotionQueueStore`, not a member of
`IPromotionQueueStore`. That interface is implemented by test fakes owned by other in-flight work;
adding a member would break them for a one-shot maintenance verb no other caller needs.
`MaintenanceCommands(SqliteConnectionFactory)` is the existing precedent for a CLI command class
reaching a concrete infrastructure type directly instead of routing through a port.

## Consequences

- **Positive.** A queue row can no longer reference a dead entry. `PromoteAsync` cannot be handed a
  hash `ShareAsync` will reject for having vanished underneath it — the cause of the destroyed-batch
  failure mode is gone, independent of whatever WP2 does to harden the symptom.
- **Positive.** Every present and future deleter of `entries` rows is covered automatically —
  `SweepService`, the four `SqliteMemoryStore` delete paths, and `SyncService`'s merge — with no
  code at any of those call sites and nothing to keep in sync as new ones are added.
- **Negative — a replace is a delete, so the trigger over-fires on it.** `ReplaceFileAsync` deletes
  every chunk of a source path and re-ingests, inside one transaction. Chunk hashes are content-derived,
  so a chunk whose text did not change returns under the *same* hash — but at the instant of the delete
  nothing backs it, the `NOT EXISTS` guard passes, and its candidate is dropped. SQLite has no deferred
  triggers, so the guard cannot see the end of the transaction. `ReplaceFileAsync` therefore captures the
  affected queue rows into a temp table before the delete and re-inserts the ones whose hash is backed
  again after the re-ingest (`CaptureQueueRowsForSourcePath` / `RestoreQueueRowsStillBacked`). A chunk
  that genuinely changed does not come back and stays dropped, which is the intended behaviour.
  `DeleteSourcePathAsync` needs no such treatment — it deletes without re-ingesting.
- **Negative — per-row cost on large deletes.** The trigger is a per-row `AFTER DELETE` (SQLite has
  no statement-level triggers), so it runs once per row for a multi-row delete
  (`DeleteContextAsync`, `DeleteSourcePathAsync`'s subtree cascade, `SyncService`'s tombstone-driven
  merge delete). Each firing is an indexed point lookup on `promotion_queue`'s
  `(project_id, hash)` UNIQUE constraint plus the `NOT EXISTS` sub-select on `entries`, itself
  covered by `idx_entries_hash` — cheap per row, but it adds up on a delete spanning thousands of
  rows. No case has been observed where this mattered in practice; if a bulk-delete path is ever
  measured to be trigger-bound, the fix is a bulk equivalent of the same predicate run once after
  the delete, not removing the trigger.
- **Neutral — an unrequested but correct interaction with sync.** `SyncService`'s merge deletes
  local `entries` rows for remote tombstones (rows another machine deleted). The trigger fires there
  too and drops the matching queue row. This is correct, not incidental: a candidate whose backing
  entry was deleted on another machine cannot be promoted on this one, and the merge path gets that
  for free from the same mechanism that covers every other deleter — no sync-specific code needed.
- **Neutral.** `extract prune` is a one-shot catch-up, not a permanent maintenance verb the trigger
  depends on. Once every currently-orphaned row is cleared, the trigger alone keeps the queue clean;
  `prune` stays available for a bank restored from a pre-ADR-0023 backup or one that skipped an
  intermediate binary version.

## Alternatives considered

### Per-call-site invalidation

Rejected — see "A trigger, not per-call-site invalidation" above. Five current call sites, no
guarantee the fifth or sixth is remembered.

### Bump `CurrentVersion` and add a ladder step

Rejected. The change is additive and idempotent; a version bump exists to gate changes that need
ordered, one-time migration work, and forcing this one through the ladder would make ADR-0019's
write guard reject older binaries opening a bank this build already touched, for no migration this
change actually requires.

## Amendment (2026-08-09): a body *replacement* is a different case from an *addition*

The 1.6.0 integration review (H4) found the guard itself was wrong: `NOT EXISTS (SELECT 1 FROM
entries e WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash)` treats *any* surviving
sibling as "still live," but `ShareAsync` only ever resolves a candidate against a
`scope = 'project'` row. A queue row backed solely by a `custom`- or workspace-scoped sibling was
kept, unpromotable, and destroyed as `stale-hash` on the next promote pass — the fix adds
`AND e.scope = 'project'` to the guard.

That fix is not additive the way the trigger's original creation was: it *replaces* a trigger body
that may already be on disk. `CREATE TRIGGER IF NOT EXISTS` — the mechanism "No `CurrentVersion`
bump" above relies on — only ever creates; it never touches a definition that already exists. Left
alone, editing the DDL text in this file would ship the fix to fresh banks only, while every bank
already opened by a pre-fix binary kept the broken guard forever. The "Bump `CurrentVersion`"
rejection above still holds for the reason given there (ADR-0019's forward-version write guard), so
the fix does not use the ladder either.

Resolution: `DROP TRIGGER IF EXISTS promotion_queue_entries_ad` immediately before an *unguarded*
`CREATE TRIGGER` (no `IF NOT EXISTS`), both kept inside the same unconditional `Ddl` script that
already runs in full on every bank open (`MemorySchema.EnsureAsync` executes it before consulting
`storedVersion` at all). Dropping something absent is a no-op, so this is idempotent exactly like
the original `IF NOT EXISTS` form was, and it reaches every existing bank on its very next open —
no `CurrentVersion` bump, no ladder step, the same no-migration guarantee the original decision
made, just achieved with DROP+CREATE instead of CREATE-only because this change replaces rather
than adds.

**The general rule going forward:** a trigger body change that is safe to re-run unconditionally on
every open (no data transform, no dependency on prior state) belongs in the unconditional `Ddl`
path — via `CREATE ... IF NOT EXISTS` for a genuine addition, or `DROP IF EXISTS` + unguarded
`CREATE` for a replacement. The version ladder is reserved for changes that need guarded, ordered,
one-time work (a data backfill, a non-idempotent `ALTER TABLE`) — not for "does this touch a
trigger" as such.
