# 0095 — Telemetry never syncs

Date: 2026-09-03

Status: Accepted

## Context

`search_quality` records every `memory_search` with its query text, and
`metrics` records the phase timings that join back to it. Both tables live in
the same bank file as `entries`, and `SyncService` pushes a whole-bank
`VACUUM INTO` snapshot. Anything the strip leaves in place rides along.
`StripNonSyncableAsync` removed workspaces, settings, and the code corpus but
left both telemetry tables in every pushed snapshot. Telemetry left the
machine on each push.

Nothing reads it. `MergeRemoteAsync` names `remote.entries` and
`remote.sync_tombstones` only. It never opens `remote.search_quality` or
`remote.metrics`, and no other pull path touches them either. A pull never
merges remote telemetry into local reads, so per-machine telemetry was
already the reality. The sync copied bytes for no consumer. That is the
defect this record fixes.

ADR-0094 accepted the leak in exchange for the signal. It named the
principled repair and deferred it. This record is that repair. It ships
before more richness lands on the same tables. New session and kind values
must not sync in the interim.

## Decision

Telemetry never crosses the sync boundary. Push DROPs `search_quality` and
`metrics` from every snapshot before it leaves (`StripNonSyncableAsync`,
called from all three push paths: local, merged, retry-merged). Pull stays
as it is. It already ignores both tables.

DROP, not DELETE. Telemetry has no FTS or vec0 shadow tables and no
triggers. DELETE would leave two empty tables plus four indexes in every
pushed snapshot, and empty-but-present tables still claim something false
about what synced. DROP removes the tables and sheds their indexes
(`idx_sq_project_time`, the three `idx_metrics_*`) in one move, and the
existing `application_id = 0` reset makes a restored snapshot recreate both
tables through the ordinary digest-DDL path on its next `EnsureAsync`. The
gate is table absence, checked in `sqlite_master` including index remnants.
Row counts cannot tell DROP from DELETE, so the tests never count rows.

`IF EXISTS` on both DROPs. The strip opens the snapshot through
`openSnapshot`, which never runs `MemorySchema.EnsureAsync`. A bank that
predates telemetry produces a snapshot with neither table, and a bare DROP
would throw `no such table` and abort the push. Same shape as the code
corpus H6 rule.

## Consequences

- Each machine keeps its own search-quality grades and phase timings. A
  cross-machine dashboard cannot read them from the sync object. Anyone who
  needs that builds an explicit export. Sync will not carry it by accident.
- A snapshot restored as a working bank gets both tables back on open. The
  reset digest forces the DDL block to run, so the restore path needs no
  special case.
- No schema change ships here. No DDL edit, no version bump, no digest edit,
  no ladder step. The strip touches the snapshot only.
- Release order matters. This strip rides first or together with the session
  and kind richness work. Richness without the strip would sync the new
  values it just added.

## Evidence

`src/AiRaccoon.Infrastructure/Sync/SyncService.cs` (`StripNonSyncableAsync`,
called from the three push sites in `SyncCycleAsync` — local `:76`, merged
`:107`, retry-merged `:184`; telemetry DROPs beside the code-corpus DROPs,
`IF EXISTS`, `application_id = 0` kept).

Gated by `tests/AiRaccoon.Tests/Integration/Sync/SyncServiceCodeExclusionTests.cs`:
`Sync_LocalPush_DropsTelemetryTablesFromSnapshot`,
`Sync_MergedPush_DropsTelemetryTablesFromSnapshot`,
`Sync_RetryMergedPush_DropsTelemetryTablesFromSnapshot` (all three push
paths, populated tables, absence oracle incl. index remnants;
DROP-to-DELETE mutation fails),
`Sync_Pull_LeavesLocalTelemetryUntouched` (local content equality, not
counts),
`Sync_PreTelemetrySnapshot_StripsWithoutError` (genuinely old shape, tables
missing; bare DROP without `IF EXISTS` fails),
`Sync_LocalPush_EncryptedBank_WithPopulatedTelemetry_DropsTelemetryTablesFromSnapshot`
(encrypted twin, populated tables, post-strip absence). Call-removal
mutations fail: dropping the `:107` call breaks the merged test, dropping
the `:184` call breaks the retry test.
