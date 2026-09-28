# 0124 — The context key is total over a NULL project id

Date: 2026-09-28

Status: Accepted

## Context

The vec0 `ctx` column carries one context key per entry, built in two places that must agree: the
SQL fragment `MemorySql.ContextKeyExpression` (embedded in the vector-write triggers, the v9
rebuild and the chunk-column maintenance SQL) and its C# twin `MemorySql.ContextKeyFor`, which
search and the write-path bucket filters use. The key is length-prefixed rather than `:`-joined so
a project id containing `:` cannot collide with a label containing `:`.

The two never agreed for a missing project id. `ContextKeyFor` reads the project id from the
context string; with an empty id it builds `project:`, `workspace:0::W` or `custom:0::L`. The SQL
fragment concatenated `project_id` directly, so for `project_id IS NULL` every branch but `shared`
evaluated to SQL `NULL`:

```
'project:' || NULL                      -> NULL
'custom:' || length(NULL) || ':' || ... -> NULL
'workspace:' || length(NULL) || ':' ... -> NULL
```

Three separate failures follow, in order of how long they have been live:

- **Pre-v9 partition key (v2–v8).** The DDL was `vec0(ctx TEXT partition key, …)`, and a vec0
  partition key **accepted** NULL — probed against sqlite-vec 0.1.9
  (`CREATE VIRTUAL TABLE v USING vec0(ctx TEXT partition key, embedding float[4] …)`,
  `INSERT … VALUES (1, NULL, ?)` succeeds). A row with no project id landed in the vec index under
  a NULL key and was invisible to every `ctx = @ctx` KNN — nothing can equal NULL.
- **The v9 rebuild is a rethrow.** `MigrateToV9Async` deliberately rethrows — an empty-shell
  vec_entries answers every query with silence — and the rebuild's `INSERT INTO vec_entries(rowid,
  ctx, embedding) SELECT …` recomputes `ctx`. On a bank holding one NULL-keyed row the metadata
  column (ADR-0068, `ctx TEXT` without `partition key`) rejects the NULL with
  `Expected text for TEXT metadata column ctx, received NULL`, so `EnsureAsync` throws and the bank
  cannot be opened at all. `PRAGMA user_version` stays 8.
- **Post-v9 writes.** `vec_entries_au` / `vec_structure_au` insert NULL into the metadata column on
  `MarkEmbedded` of a project-id-less row, so the embed transaction throws. ADR-0119's isolation
  then treats it as a row failure, probes the engine, and **charges the row an `embed_attempts`
  every drain pass**; at three attempts the row leaves the pending selection and is silently
  abandoned until a model switch resets attempts.

The live bank (`~/.ai-raccoon/memory.db`, read-only, 2026-09-28) is at `user_version` 17 and holds
**0** entries with `project_id IS NULL` and **0** NULL/empty `ctx` rows in either vector table — the
defects are reachable by construction, not by the owner's data. Its stored trigger bodies carry the
old expression, which proves the second half of the problem: `CREATE TRIGGER IF NOT EXISTS` can
only create, so a code-only change would never reach any existing bank.

## Decision

**`COALESCE(project_id, '')` in every concatenating branch of `ContextKeyExpression`, and an
every-open probe-first body replacement for the two triggers that embed it.**

```
'project:' || COALESCE(project_id, '')
'custom:'  || length(COALESCE(project_id, '')) || ':' || COALESCE(project_id, '') || ':' || COALESCE(context_label, '')
'workspace:' || length(COALESCE(project_id, '')) || ':' || COALESCE(project_id, '') || ':' || workspace_id
```

The key becomes total: `project:`, `custom:0::L`, `workspace:0::W` — exactly what `ContextKeyFor`
builds from an empty project id, and never NULL.

**No `CurrentVersion` bump and no ladder step.** Two mechanisms carry the change to every bank,
both precedented by ADR-0023's amendment (probe-first body replacement) and ADR-0075 (digest-gated
DDL):

- The fresh-bank `Ddl` block and every ladder rebuild that creates `vec_entries_au` /
  `vec_structure_au` now build them from two shared `static readonly` constants, so the stored body
  and the intended body cannot drift.
- `EnsureVecCtxTriggerBodiesAsync` runs in `RunEveryOpenStepsAsync` beside
  `EnsurePromotionQueueTriggerScopeGuardAsync`: one indexed `sqlite_master` read for both triggers,
  and only on mismatch a `DROP` + `CREATE` from those same constants. No write, no
  `PRAGMA schema_version` bump, no missing-trigger window on a bank already repaired — the same
  reasoning the promotion-queue guard records, which chose probing over an unconditional
  drop/recreate on every open.

**No data migration, deliberately.** There is nothing to migrate:

- A **pre-v9** bank's NULL-keyed rows are rebuilt from `entries` by the v9 ladder step, which now
  computes the total key — the rebuild was already the repair for those rows, and it is why the
  rethrow mattered.
- A **post-v9** bank cannot contain a NULL `ctx`: the metadata column rejects NULL on insert,
  update (probed: `UPDATE v2 SET ctx = NULL …` fails the same way) and on the v9 rebuild. The old
  triggers never wrote NULL there; they threw.
- A row could hold an existing `ctx` only under a key no search builds, so an in-place `UPDATE ctx`
  repair would buy nothing a selectable row lacks. sqlite-vec 0.1.9 does support the metadata
  UPDATE if a future bank ever needs one (probed: `UPDATE v2 SET ctx = 'custom:0::L' WHERE rowid =
  1` succeeds and reads back), which is recorded here as the escape hatch, not shipped as a job.

`ChunkPositionScanner.PartitionAsync`'s NULL-safe `IS` stays. The key is recomputed from each row
by the now-total expression, so the arm that matched a NULL key is unreachable and `IS` behaves
exactly like `=`; it stays as the partition rule's NULL-safe form — the shape that fixed the
empty-lookup crash — and costs nothing. `ChunkIndexRepairTests`' NULL-project case pins the
end-to-end repair of such a partition, not the NULL arm.

## Alternatives rejected

- **Only fix `ContextKeyExpression`, no trigger replacement.** The live bank's stored bodies would
  keep writing NULL until some other event recreated them, so every fix below the level of a full
  re-ladder would be invisible on existing banks. This is exactly the drift the guard exists to
  close.
- **Unconditional `DROP TRIGGER` + `CREATE TRIGGER` on every open.** Rejected in ADR-0023's
  amendment for the promotion-queue trigger and no less wrong here: `SqliteConnectionFactory` runs
  the every-open steps on every checkout, so a large bank's sweep would turn into thousands of
  schema writes, each bumping `PRAGMA schema_version` and opening a window where the trigger does
  not exist — a concurrent embed landing in that window would write no vector at all.
- **A `CurrentVersion` bump to v18 with a "recreate the vec triggers" ladder step.** The ladder is
  for guarded, one-time work; a body replacement that is safely re-runnable on every open belongs
  in `Ddl`/the every-open steps (the ADR-0023 amendment's own rule). A version bump would also make
  every older binary refuse the bank for a change with no new shape.
- **Normalize `project_id` to `''` on write so NULL never reaches the expression.** A write-path
  conversion leaves every existing row, every sync-merged row and every bank an older binary opens
  unconverted; it fixes the SQL by making the data pass its assumptions, where the expression's job
  is to be total over what the table holds. `project_id` is also part of the bucket unique indexes
  and the repair-tier identity — changing its stored value is a data migration with a much larger
  blast radius than one `COALESCE`.
- **A `ctx IS NULL` repair pass on every open.** The probe would scan two vec0 tables on every open
  to find a state that post-v9 writes cannot produce and pre-v9 banks do not retain after v9. The
  measured count is 0 on the live bank; the repair stays one `UPDATE` away if that ever changes.

## Consequences

- **NULL and `''` share one normalized key.** Sync's bucket identity uses raw `project_id`, so it
  can admit a NULL-id row and an empty-id row as separate entries while both vec rows key
  `project:` (or `custom:0::…`). Metadata `ctx` is not unique, and no live search builds that key,
  so nothing breaks; a bank-wide chunk recompute merges the two rows into one partition, which is
  the same partition they already share for every other purpose. Recorded, not defended against.
- **A project-id-less row now embeds instead of burning three attempts.** It stays invisible to
  `memory_search` because no real project id is empty, so the normalized key is foreign to every
  live search context — but it no longer configures the ADR-0119 abandonment path, and its chunk
  columns are maintained at a key the maintenance SQL can name.
- **The chunk-column maintenance SQL becomes total.** `RecomputeChunkColumnsForContext` and
  `CompactChunkColumnsAfterDelete` now reach a project-id-less group given its normalized key
  (`@ctx = 'project:'` is not reachable from a real search, but the maintenance caller that owns
  the group can name it), and `RecomputeChunkColumnsBankWide` numbers each project-id-less context
  separately instead of cross-numbering them under one NULL group.
- **One more every-open read.** `MemorySchemaDigestTests`' fast-path statement count moves 5 → 6:
  the guard probes both triggers in one `sqlite_master` read and writes nothing when they match.
- **ADR-0068 is unchanged.** `ctx` stays a metadata column; this fixes the expression that
  populates it, not the shape.
- **The v2–v8 hazard is recorded, not refuted.** Pre-v9 partition keys genuinely accepted NULL, and
  a pre-v9 bank could genuinely hold an index-invisible row; the v9 rebuild is what now converts it.
- **A non-BMP project id still diverges.** SQLite `length()` counts code points while C#'s
  `string.Length` counts UTF-16 units, so a project id outside the BMP keys differently on the two
  sides. Pre-existing, unchanged by `COALESCE` (the identity on a non-NULL id), and unreachable with
  the guidv7 ids the tool writes; recorded, not fixed here.

## Gates

Every gate went red first:

- `MemorySqlContextKeyTests.ContextKeyExpression_WhenProjectIdIsNull_*` executed the old expression
  against real SQLite and got NULL (`should not be null but was`) where `ContextKeyFor(_, "")`
  returns a key.
- `VecCtxNullProjectTests` watched `MarkEmbedded` throw
  `Expected text for TEXT metadata column ctx, received NULL` on a fresh bank and on a current bank
  seeded with the old trigger bodies; watched the v8 upgrade throw and leave `user_version` 8; and
  watched the three chunk-column constants change 0 rows where the normalized key must reach them.
- The trigger-body guard was disabled after implementation and
  `EnsureAsync_OnAnAlreadyCurrentBankWithOldTriggerBodies_ReplacesThemAndMarkEmbeddedThenSucceeds`
  failed with that same `SqliteException`; restored, it passed.
- `MemorySchemaDigestTests` caught the new probe as `statements.Count should be 5 but was 6` before
  the count was updated.
