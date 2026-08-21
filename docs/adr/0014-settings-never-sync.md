# 0014 — Settings never cross the sync boundary

Date: 2026-08-08

Status: Accepted

## Context

The `settings` table holds machine-local secrets: cloud store credentials (the
S3 access/secret keys or the Azure connection string) and the embedding
endpoint/API key. `SyncService` snapshots the local bank with `VACUUM INTO`
and pushes that snapshot to a shared object store, so anything left in the
snapshot's `settings` table would leave the machine.

The push side was closed first. `fix(sync): strip settings table from pushed
snapshots` (#88) added a `DELETE FROM settings` to the snapshot strip that
already dropped workspace-scoped entries, but only on the first-push path.
`fix(sync): strip settings on the merge and retry push paths, not just the
first push` (#114) moved the strip into `StripNonSyncableAsync`
(`SyncService.cs:344-360`) and called it from all three snapshot-producing
paths (first push, merge-branch push, conflict-retry push), closing the gap
where a merged or retried snapshot could still carry settings.

The pull side stayed open. `MergeRemoteAsync` unconditionally merged
`remote.settings` into the local bank with an LWW upsert — issue #121: a
remote snapshot's credentials or embedding endpoint/key would silently
overwrite the local machine's own, on every sync that saw a settings row on
the far end. `fix(sync): stop settings from crossing the sync boundary, check
integrity on every pushed snapshot` (#129) deleted that merge statement
entirely (`SyncService.cs:260-262`).

## Decision

Settings are per-machine. They never cross the sync boundary in either
direction: push strips them from every snapshot before it leaves
(`StripNonSyncableAsync`, called from all three push paths), and pull never
reads `remote.settings` at all — `MergeRemoteAsync` merges `entries` and
`sync_tombstones` only.

Both directions are gated by tests in
`tests/AiRaccoon.Tests/Unit/sync/SyncServiceTests.cs`:

- Push: `MemorySync_SettingsRows_NotInSyncPayload`,
  `MemorySync_MergeBranchWithExistingRemote_StripsSettingsAndWorkspaceFromPushedPayload`,
  `MemorySync_ConflictRetryBranch_StripsSettingsAndWorkspaceFromPushedPayload`.
- Pull: `MemorySync_PullWithHostileRemoteSettings_DoesNotOverwriteLocalSettings`,
  `MemorySync_MergeWithEmptySettingsRemote_SucceedsAndPreservesLocalSettings`.

## Consequences

- No setting can be centrally distributed via sync — a project-wide default
  (e.g. a shared embedding endpoint) cannot ride along with the entries it
  applies to. A genuine need for that requires an explicit allowlist of
  syncable keys and a design for how they merge, not a re-enabled blanket
  merge.
- Every machine configures its own cloud credentials and embedding endpoint
  independently; sync only ever moves `entries` and `sync_tombstones`.

## Evidence

`src/AiRaccoon.Infrastructure/Sync/SyncService.cs:260-262` (pull-side
comment recording the rule at the point it's enforced), `:344-360`
(`StripNonSyncableAsync`, called from `SyncService.cs:62`, `:93`, `:149`);
commits #88 (`64771f4`), #114 (`cc4ed68`), #129 (`b438322`, issues #121 and
#115).

## Amendment (2026-08-21) — the code corpus never syncs either

`docs/work/2026-08-21-code-search-implementation-plan.md` §3.7 (as corrected
by §12.2 H6) added a second corpus — `code_entries`/`code_fts`/`vec_code` —
that must never leave the machine, for the same reason settings don't: a
pushed snapshot is a whole-bank `VACUUM INTO`, so anything not explicitly
excluded rides along.

The mechanism differs from the settings strip on purpose. Settings are
row-deleted (`DELETE FROM settings`) because the table itself is syncable
shape, just not its contents. The code corpus is **table-dropped**
(`DROP TABLE IF EXISTS code_entries/code_fts/vec_code`) because the gate is
table *absence*, not empty tables — row-deletion would leave three empty
tables (plus their FTS5/vec0 shadow tables and trigger families) in every
pushed snapshot, and empty-but-present tables still assert something false
about what synced. `IF EXISTS` matters here in a way it doesn't for
`settings`: `StripNonSyncableAsync` opens the snapshot through `openSnapshot`,
which never runs `MemorySchema.EnsureAsync` (H6) — a snapshot taken from a
bank that predates the code corpus feature has none of these tables, and a
bare `DROP TABLE` would throw and abort the push.

The decision statement now reads: **settings and the code corpus never sync**
— push strips/drops both from every snapshot on all three push paths (local,
merged, retry-merged); pull never reads `remote.settings` and never names the
code tables at all (merge only touches `entries`/`sync_tombstones`).

Gated by `tests/AiRaccoon.Tests/Integration/Sync/SyncServiceCodeExclusionTests.cs`:
`Sync_LocalPush_DropsCodeTablesFromSnapshot`,
`Sync_MergedPush_DropsCodeTablesFromSnapshot`,
`Sync_RetryMergedPush_DropsCodeTablesFromSnapshot` (all three push paths),
`Sync_Pull_LeavesLocalCodeCorpusUntouched`, and
`Sync_PreCodeCorpusSnapshot_StripsWithoutError` (the `IF EXISTS` case).

> **Evidence:** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs`
> (`StripNonSyncableAsync`, called from the three push sites at
> `SyncCycleAsync` — local snapshot, merged snapshot, retry-merged snapshot).
