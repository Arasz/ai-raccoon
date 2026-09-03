# P1 tracing answers — air-merge-enforce-single-project-id

Authoritative inputs: `plan.md` + `research-record.md` in `.ai-badger/worktrees/air-merge-enforce-single-project-id/`.
All file:line references are against this worktree at lane `lane/air-merge-p1`.
Zero bank writes were performed: the census (`src/AiRaccoon.Infrastructure/Sqlite/ProjectIdCensus.cs`)
runs SELECT-only (proven by `Collect_RunsUnderQueryOnly_ProvingZeroBankWrites`, which sets
`PRAGMA query_only = ON` before collecting) against seeded `:memory:` banks only.

## (a) Do `projects` rows sync at all? — NO

`SyncService.MergeRemoteAsync` (`src/AiRaccoon.Infrastructure/Sync/SyncService.cs:215`) merges
exactly two remote tables and derives one local table:

- `remote.entries` → near-union `INSERT OR IGNORE` (`:294-318`, tombstone-checked);
- local `memory_source` populate + `entries.source_id` backfill, derived from local rows, never read from remote (`:321-356`);
- `remote.sync_tombstones` → union (`:362-370`), then apply (`:372-381`), then watermark GC (`:383-396`).

The only other `remote.*` reference in the file is the `PRAGMA remote.user_version` gate (`:283`).
There is no `remote.projects` read anywhere in the merge body — neither union nor
last-writer-win. The plan's suspicion is confirmed: the merge tail covers entries +
memory_source/source_id backfill + tombstone union and never touches `remote.projects`
(the settings exclusion is even called out explicitly at `:358-360`).

**Binding consequence (fleet rule):** each replica's `projects` table is local-only, so an
unmerged replica's pull can re-insert loser-id entries its own never-synced `projects` table
still legitimizes. The rule is therefore RUN REPAIR LOCALLY ON EVERY REPLICA BEFORE PUSH,
not just merge-before-push.

## (b) Full sync-inclusion trace

Push strips in `StripNonSyncableAsync` (`SyncService.cs:464-...`); pull merges in
`MergeRemoteAsync` (`:215-...`). A surface syncs only if it survives the strip AND is read
by the merge.

| Surface | Push (strip) | Pull (merge) | Net |
|---|---|---|---|
| `entries`, non-workspace | survives | union `:294-318` | SYNCED |
| `entries`, workspace (`workspace_id NOT NULL`) | DELETED `:471` | excluded (`WHERE r.workspace_id IS NULL`) | never syncs |
| `memory_source` / `source_id` | survives (untouched) | locally re-derived `:321-356`, never read from remote | local-only |
| `sync_tombstones` | survives (untouched) | union `:362-370` | SYNCED |
| `projects` | survives (untouched) | never read — answer (a) | NEVER syncs |
| `settings` | DELETED `:474` | never read (`:358-360`) | never syncs |
| `promotion_queue` | DROPPED `:480` (+ `workspaces`, `promotion_queue_prune_requests`) | never read | never syncs |
| `promotion_discards` | survives (NOT dropped — dead bytes in the snapshot) | never read | never syncs |
| `search_quality` | survives (untouched) | never read | never syncs |
| `code_entries` / `code_fts` / `vec_code` | DROPPED `:488-494` | never read | never syncs |
| `watches` / `watch_files` / `watch_digest_claims` | survive (untouched) | never read | never syncs |
| `metrics` / `noise_entries` / `sync_meta` | survive (untouched) | never read | never syncs |

Net: the ONLY id-keyed surfaces that cross the sync boundary are `entries` and
`sync_tombstones`. Quality/queue/code/settings/watches/projects all stay per-replica —
which is why the P1 census must enumerate them locally per replica, and why the P2 repair
(plus its tombstone-PK rewrite) must run on every replica: nothing but entries-union and
tombstone-union will propagate the fold.

## (c) Re-embed scope (narrowed)

Existence of vec invalidation is settled (schema triggers); what P1 narrows is the exact
row set P2 must invalidate and which resyncs it can skip:

- Drain default is already bank-wide and targeted-by-state, not by project:
  `PendingEmbedJob.HasWorkAsync` polls bank-wide `MemorySql.HasPendingEmbed`
  (`MemorySql.cs:358-359`); `EntryEmbedder.EmbedPendingBatchAsync` drains bank-wide
  `SelectAllPendingForEmbed` (`EntryEmbedder.cs:299-305`, `MemorySql.cs:394-396`); code side
  mirrors via `HasPendingCodeEmbed`/`SelectAllPendingCodeForEmbed` (`MemorySql.cs:412,425)
  into `CodeEmbedder.EmbedPendingBatchAsync` (`CodeEmbedder.cs:51-...`), paced by
  `maintenance.embed-rows-per-run.global` (default 128, `EmbedDrainService.DrainOnceAsync`).
  No drain change is needed: P2 only has to mark the right rows `pending` and the existing
  jobs drain them.
- Narrowed invalidation set — renamed-ids-only, embedded-state-only, both corpora:
  `UPDATE entries SET embed_state = 'pending' [, embedding = NULL, structure_embedding = NULL,
  heading_path = NULL] WHERE embed_state = 'embedded' AND project_id IN (<renamed losers>)`
  plus the same predicate on `code_entries`. Firing `vec_entries_pending` / `vec_structure_pending`
  (`MemorySchema.cs:163,196`) and `vec_code_pending` (`MemorySchema.cs:512`) drops the stale
  `ctx`-tagged vec rows the instant the UPDATE commits; the `*_au` triggers
  (`MemorySchema.cs:189,505`) re-insert them under the winner `ctx` on re-embed.
  `ctx` embeds the id by construction (`MemorySql.ContextKeyExpression`, `MemorySql.cs:785-794`;
  code uses the raw project id, `MemorySchema.cs` `vec_code_au`), so a plain id UPDATE without
  this step leaves renamed rows invisible under the new id.
- Explicitly NOT required: FTS resync. `entries_fts_au` fires only `AFTER UPDATE OF
  value, source_file, section` (`MemorySchema.cs:179`) and `code_fts_au` only on
  `value, source_file` (`MemorySchema.cs:494`) — a `project_id`/`context_label` rewrite is a
  trigger no-op, and FTS rows are keyed by rowid, not by id. The plan's FTS-resync budget is
  deleted.
- Still required as a P2 job step (not resync): chunk renumber. Chunk groups partition by the
  ctx expression + `source_file`, so renamed rows change groups — reuse `ChunkIndexRepair` as
  an ordered job step after the rewrite (sync's own post-merge precedent re-derives bank-wide
  from id order, `SyncService.cs:428-...` via `MemorySql.RecomputeChunkColumnsBankWideFromIdOrder`).
- Shape precedent for the invalidation UPDATE is sync's merge-reindex
  (`SyncService.cs:407-427`: sets `pending` + NULLs content/structure columns, with the comment
  explaining why the structure columns must be nulled alongside). Never cite
  `VecDimensionReconciler` as the mechanism (dimension-reconcile-only, never repopulates).

## P1 persistence decision (alias map)

The alias map ships as the first-class pure type `ProjectIdAliasMap`
(`src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs`, `Default` = the plan's canonical-wins
table, JSON round-trip for durable hand-off) with NO bank table and NO settings keys in P1:
settings rows never cross sync (trace (b)), so a settings-backed map would diverge per replica,
and a new table would move `SchemaDigest` for zero P1 benefit. P2 chooses the repair-time
vehicle; the map's `TryResolve` (loser→winner, canonical→self, dropped/typo→false) is the
contract pull-time fold and the ToolGate fold program against. The production guid standing in
for `01a062f4` is intentionally NOT hardcoded (full value unavailable without reading the live
bank, which P1 forbids): `ProjectIdAliasMap` constructs from arbitrary entries, proven by
`CustomMap_ResolvesAGuidLoser_ByExactEntry`, so P2 diagnose fills guid losers from the bank it
repairs.

## Census reproduction note

`ProjectIdCensusTests` seeds a `:memory:` bank mirroring every research-record cluster in shape
(jsaa split queue + split code incl. pending-loser and embedded-winner vec legs, ai-badger
zero-entry guids, hermes-default quality ownership, ai-raccoon casing split with disjoint
settings keys, manual-sweep watch attachment, qa-noise-project isolation, NULL-scope workspace
row, NULL-ctx bulk rows, legacy `watch.scope.`/global-key attribution) and asserts the census
reports each leg. Live-bank volumes (± drift) are deliberately not asserted — the artifact proves
the mechanism reproduces the clusters, not a snapshot of a moving bank.
