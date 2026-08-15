# Lane report — data access & SQLite

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: sonnet · read-only on `src/`, scratch SQLite databases used for every semantics claim.
Lane verified the base SHA.

> **Orchestrator follow-up (added after the lane finished).** The lane's "Still open" asked for the
> live bank's partition cardinality to confirm F2's mechanism. Measured, read-only:
>
> - `vec_entries_chunks` holds **36 chunk rows** for **16,150** vectors — at the default
>   `chunk_size` of 1024 that is 36,864 allocated slots, a **2.28× allocation ratio**, matching the
>   observed 49 MB against 24.8 MB of real vector bytes.
> - `vec_structure_chunks` holds **13 chunk rows** for **5,835** vectors — 13,312 slots, the same
>   2.28×, matching 19 MB against ~8 MB.
> - Partition cardinality is **20 distinct `ctx` values**, of which **14 hold fewer than 10 rows**
>   (`workspace:…` ×4, `custom:…` ×8, `project:qa-noise-project` 1, `project:manual-sweep` 26).
>   Each of those gets a full chunk.
> - Roughly **43 MB of the 159 MB bank is empty vec0 chunk padding** — 27% of the file.
> - **Orphans are not the explanation**: `vec_entries_rowids` = 16,150 against 16,146 `entries`
>   rows, and `vec_structure_rowids` = 5,835 against 5,834 non-NULL `structure_embedding` rows.
>   Four and one stray rows respectively — noise, not 31 MB.
>
> **F2 is confirmed with live numbers and the orphan hypothesis is disconfirmed.**

---

### F1 — No SQL statement in the Infrastructure layer silently drops a parameter [READ]
**Severity:** LOW (confirms the class of defect is closed, not present)
**Evidence:** swept every SQL constant in `MemorySql.cs` (57), `PromotionQueueSql.cs` (14),
`NoiseEntrySql.cs` (4), plus inline SQL in `SqliteWorkspaceStore.cs` (2),
`SqliteSearchQualityService.cs` (5), `SqliteMemorySourceStore.cs` (2), and
`NoiseShadowObserver.cs`/`BankMaintenanceHostedService.cs` (1 each) — **86 parameterised statements
total**. For each, matched every `@placeholder` against the calling site's anonymous-object /
`DynamicParameters` properties (Dapper matches case-insensitively; confirmed by
`PromotionQueueSql`'s `@ProjectId`/`@Hash` being satisfied by lower-camel anonymous objects).
**Zero mismatches in either direction.** `MemorySql.InsertEntry` (`MemorySql.cs:11-17`) — the
statement the prior review's `ttl_days` finding was about — carries no `ttl_days` placeholder at
all; `ttl_days` is intentionally NULL-by-default at insert and set separately via `UpdateEntryTtl`
(`MemorySql.cs:438-446`, called from `SetEntryTtlAsync`).

**Why it matters:** confirms the prior fix generalised rather than being a one-column patch, and
gives a clean baseline count for the next sweep.

---

### F2 — vec0's default `chunk_size` pre-allocates a full chunk per partition-key value, and nothing in the schema sets it [MEASURED]
**Severity:** HIGH
**Evidence:** `MemorySchema.cs:122,126` and `RebuildVecTableAsync` (`MemorySchema.cs:1191-1195`)
declare `vec_entries`/`vec_structure` as `USING vec0(ctx TEXT partition key, embedding float[384] …)`
with no `chunk_size` option — `grep -rn chunk_size src/AiRaccoon.Infrastructure` returns nothing, so
sqlite-vec's default (1024) applies. Measured on a scratch vec0 table (`hiraokahypertools.sqlite-vec`
0.1.9, the package the project ships):

```
5000 rows / 1 partition:      raw=7.68MB  stored=8.00MB    (x1.04)
5000 rows / 200 partitions:   raw=7.68MB  stored=316.81MB  (x41.25)
5000 rows / 1000 partitions:  raw=7.68MB  stored=1583.70MB (x206.21)
```

`vec_t_vector_chunks00` alone accounts for ~314.99 MB and ~1574.92 MB respectively — SQLite
allocates a full 1024-row chunk block per distinct `ctx` value regardless of how many rows live in
it. `chunk_size` is a real, tunable knob:

```
default:        overhead x206.21 (1000 partitions, 5 rows each)
chunk_size=64:  overhead x12.95
chunk_size=16:  overhead x3.30
chunk_size=8:   overhead x1.69
```

`MemorySql.ContextKeyExpression` partitions by `project:<id>` / `custom:<id>:<label>` /
`workspace:<id>:<wsId>` — a single shared bank serving many projects has exactly the
partition-cardinality shape that triggers this.

**Why it matters:** this is the mechanism behind the live bank's vec0 overhead, and it gets worse,
not better, as the install accumulates projects and context labels.

**Fix:** set an explicit, small `chunk_size` (16-64) on both vec0 tables in `MemorySchema.Ddl` and
`RebuildVecTableAsync`, as a **new v9 ladder step** (the ladder is append-only — add a step that
drops and rebuilds both vec0 tables with the new `chunk_size`, sourcing from
`entries.embedding`/`structure_embedding`, the pattern `MigrateToV2Async` already uses). Pick the
value from the install's real partition-size distribution.

---

### F3 — `entries.embedding`/`structure_embedding` are not vestige, but the tradeoff they buy is unquantified [READ]
**Severity:** LOW
**Evidence:** the only read site for either column is `RebuildVecTableAsync`
(`MemorySchema.cs:1196-1203`), invoked by `MigrateToV2Async` to repopulate `vec_entries`/`vec_structure`
after a schema-shape change without re-embedding through the ONNX/OpenAI pipeline.
`EntryEmbedder.EmbedAsync`'s re-embed path (`EntryEmbedder.cs:43-45`) reads `entries.value`, never the
stored vector. Keeping them costs ~31 MB of a 159 MB bank (~19.5%).

**Why it matters:** deleting the columns would make every future vec0 rebuild — a dimension change,
an F2 `chunk_size` migration, disaster recovery from a corrupted shadow table — require a full
re-embed instead of a SQL copy. A real cost, not just caution.

**Fix:** no code change; record the tradeoff as an ADR so the next space audit does not re-discover
it as a mystery. **This disconfirms the orchestrator's O3 hypothesis that the BLOBs were write-only
vestige.**

---

### F4 — `structure_embedding` NULL on 64% of rows is content shape, not a stalled backfill — but the fusion formula treats "no signal" as "hostile signal" [READ]
**Severity:** MEDIUM
**Evidence:** `EntryEmbedder.EmbedIfConfiguredAsync` (`EntryEmbedder.cs:63-71`) only computes a
`structureEmbedding` when `HeadingPathParser.Parse(value).Length > 0`; content with no markdown
heading gets `null` **by design**, and `HealStructureAsync` (`EntryEmbedder.cs:192-223`) only backfills
rows with `heading_path IS NULL`, leaving genuinely headless content NULL forever — matching
`MarkStructure`'s own comment (`MemorySql.cs:317-318`) that `heading_path = ''` is the terminal
"healthy, no heading" state.

The problem is downstream. `StructureFusion.Fused` (`StructureFusion.cs:23-28`) is
`alpha * contentSim + (1-alpha) * (structureSim ?? 0.0)`. A row with no structure vector never
appears in `vec_structure`'s KNN candidate list (the trigger only inserts
`WHEN NEW.structure_embedding IS NOT NULL`, `MemorySchema.cs:134-139`), so `structureSim` is genuinely
**absent**, not merely low — and `Rank` (`StructureFusion.cs:52-56`) defaults it to `0.0`, the worst
possible cosine score. At the default `alpha=0.5`, two equally-relevant rows with content-similarity
0.70 score **0.65 (has heading) vs 0.35 (no heading)**, purely because one document happens to use
markdown headers. The class comment in `SqliteMemoryStore` ("banks without structure vectors degrade
to content-only order") describes whole-bank absence; it does not hold per-row, which is the actual
common case — **64% of the live bank**.

**Why it matters:** this silently penalises the majority of the bank's rows in vector-modality
ranking, for a reason unrelated to relevance.

**Fix:** when `structureSim` is **absent** (not just low), fuse at `alpha=1.0` for that hash — i.e.
genuinely degrade to content-only per row, matching the documented intent, rather than penalising
headless content.

---

### F5 — `promotion_discards` has no reaper anywhere in the codebase [MEASURED + READ]
**Severity:** HIGH
**Evidence:** `grep -rn "DELETE FROM promotion_discards"` across `src/` returns nothing;
`PromotionQueueSql.cs` defines `RememberDiscard` (insert-only, `:24-27`) and two read-only
`NOT EXISTS`/`EXISTS` checks against it (`Upsert`, `PruneRejected`) but never a delete. Live bank:
`promotion_discards` = **965 rows** against `promotion_queue` = 19 and 138 `shared`-scope entries —
already the largest artefact the promotion feature has produced, in an install where the feature is
lightly used.

**Why it matters:** the same shape ADR-0029 already fixed for `noise_entries` (unbounded growth, no
TTL), not generalised to the discard table. `PruneRejected` (`:33-41`) prunes *queue* rows already
covered by a discard; nothing prunes the discard record itself.

**Fix:** add a bounded age-based reaper to `BankMaintenanceHostedService.RunPassAsync`, next to
`PurgeExpiredNoiseEntriesAsync` — same pattern, same file, same cadence.

---

### F6 — `search_quality` has no reaper either [READ]
**Severity:** MEDIUM
**Evidence:** written by `SqliteSearchQualityService.RecordSearchAsync` on every `memory_search`
call, with no TTL column and no corresponding delete anywhere in `src/`.
`idx_sq_project_time (project_id, created_at)` exists (`MemorySchema.cs:318`) — an index built for a
range-purge query that is never issued. Live bank: 424 rows.

**Fix:** add a retention policy in the same maintenance pass, or state explicitly that the metric is
kept forever — the index's `created_at` component suggests someone expected a purge to exist.

---

### F7 — `BumpAccess`'s `rating` column loses updates under concurrent search hits on the same hash [MEASURED]
**Severity:** MEDIUM
**Evidence:** `BumpAccessAsync` (`SqliteMemoryStore.cs:1023-1044`) does `SELECT created_at,
access_count` (`SelectRatingForBump`), computes `rating` in C# from `row.AccessCount + 1`, then
`UPDATE … SET access_count = access_count + 1, rating = @rating` (`MemorySql.cs:393-400`) as two
separate round-trips with no transaction between them. Reproduced by manually interleaving two SQLite
connections (WAL, production shape):

```
P1 reads AccessCount=5; P2 reads AccessCount=5 (before P1 commits)
P1 computes rating=f(6), writes, commits.
P2 computes rating=f(6) (stale — true count is now 6), writes access_count=access_count+1 (→7,
   correct), rating=f(6), commits.
Final row: access_count=7 (correct), rating=f(6) — should be f(7). One bump's rating is lost.
```

`access_count` itself is immune (relative SQL expression under SQLite's single-writer
serialisation) — only `rating`, computed client-side from a stale read, loses updates.

**Why it matters:** `rating` feeds ranking and sweep-eligibility. Under concurrent search traffic on a
popular hash — exactly what a shared multi-project bank produces — it under-counts relative to
`access_count`, permanently and silently. ADR-0037 scopes its guards to workspace uniqueness/close and
promotion-queue claims; this path was never covered.

**Fix:** recompute `rating` inside the `UPDATE` itself as a SQL expression against the live
`access_count`/`created_at`, or wrap the read-then-write in `BEGIN IMMEDIATE`.

---

### F8 — The migration ladder is contiguous and the forward-version guard is correct, but the version is stamped once at the end, not per step [READ]
**Severity:** MEDIUM
**Evidence:** `EnsureAsync` (`MemorySchema.cs:324-437`) reads `storedVersion` once, chains
`MigrateToV1Async`…`MigrateToV8Async` gated by `storedVersion < N`, and calls
`StampAsync(CurrentVersion)` exactly once, only `if (healthy)`, after every step (`:433-436`).
`UnsupportedSchemaVersionException` (`:333`) correctly runs first, before the DDL. The ladder is
contiguous (v1..v8, no gaps, `CurrentVersion = 8`).

Consequence: if a `storedVersion = 0` bank hits a hard step's exception at v5 after v1-v4 already
committed, `PRAGMA user_version` is never advanced — so the next open re-runs v1 through v4 in full
before retrying v5. This is safe **only because every individual step happens to be idempotent**
(`ALTER TABLE` guarded by `pragma_table_info`, `DROP TABLE IF EXISTS`/`CREATE VIRTUAL TABLE`
full-replace, bucket dedupe keyed by `IF NOT EXISTS` index probes). The version marker is not what
prevents double-application; per-step idempotency is. `v1` and `v7` are explicitly soft (catch,
return `false`, degraded-but-open, retried next open); `v2`-`v6`, `v8` rethrow.

Also verified: two connections opening the same legacy bank concurrently each independently read
`storedVersion` before either stamps, so both redo the entire ladder — safe but wasteful;
`BEGIN IMMEDIATE` inside each hard step serialises them via SQLite's write lock (backed by the 5 s
`busy_timeout`), not via any application-level guard.

**Why it matters:** correct by convention, not by mechanism. A step added without care for re-entry
would silently corrupt data on the next partial-failure-then-retry, with no test positioned to catch
it.

**Fix:** no change needed today. Add a one-line contract note above `CurrentVersion`: "every ladder
step must be safe to re-run from a state where it, or any earlier step, partially or fully
completed" — that is the actual invariant holding the retry behaviour together, and it is not
written down the way the append-only rule is.

---

## Still open

- **Whether `vec_structure`'s size includes true orphans** rather than pure chunk padding — the lane's scratch orphan probe showed zero orphans for the straightforward delete case; the sync-reindex-clears-then-crashes interleaving that `SyncService.cs:376-382` guards against was not reproduced. *(Orchestrator: settled against the live bank — 4 stray rowids in `vec_entries`, 1 in `vec_structure`. Not the explanation.)*
- **`promotion_discards`/`search_quality` retention is a product decision, not just a code fix** — what age threshold, and whether `search_quality` should ever expire given it presumably feeds a quality view. Needs an owner call before F5/F6 become tickets.
- **F7's actual production frequency** — the anomaly is proved and its shape quantified, but not how often it fires in the live bank; `search_quality` does not capture concurrent-hit telemetry for entries.

## Grade mix

MEASURED 3 (F2, F5 in part, F7) · READ 5 (F1, F3, F4, F6, F8) · INFERRED 0 · UNVERIFIED 0.
Every SQLite semantics claim was executed against a scratch database with the statement and output
recorded.

## Owner questions

1. Is the ~31 MB `entries.embedding`/`structure_embedding` duplication (F3) an accepted rebuild-insurance tradeoff, or should it be dropped now that a `chunk_size` fix (F2) is the more likely reason to ever rebuild vec0?
2. Should `promotion_discards` and `search_quality` get the same maintenance-pass reaper as `noise_entries`, and on what retention window?
3. Is F4's per-row structure-fusion penalty — headless content scoring lower than headed content at equal relevance — a known accepted tradeoff of ADR-0004, or an oversight?
4. Does F7's `rating` drift matter given `rating`'s consumers (ranking, sweep eligibility), or is it noise relative to the `RatingPolicy` decay curve's own imprecision?

## Healthy

- **Parameter/placeholder hygiene across all 86 swept statements** (F1) — clean.
- **UNIQUE-index NULL semantics** on the three bucket indexes behave exactly as SQLite's NULL-distinctness implies (verified: two `shared` rows with identical hash and NULL `path` both insert; a genuine non-NULL `(path,hash)` duplicate correctly rejects) — and `path` is never actually NULL on any real write path, so the theoretical gap is unreachable.
- **Bare `ON CONFLICT DO NOTHING` (no conflict target) on `InsertEntry` correctly does *not* swallow `CHECK` violations** — verified a scope-CHECK-violating insert still raises `IntegrityError` through the exact `InsertEntry` shape, while a genuine UNIQUE conflict is silently dropped as intended.
- **Trigger fire-time / orphan cleanup on delete:** `entries_fts_ad`, `vec_entries_ad`, `promotion_queue_entries_ad` all fire correctly and leave zero orphans in the single-row-delete case.
- **`last_insert_rowid()` is never used anywhere in `src/AiRaccoon.Infrastructure`** — every insert path re-`SELECT`s by natural key, sidestepping the staleness trap rather than needing to get it right.
- **ADR-0037's two atomicity claims hold:** `TryCloseAsync` (`UPDATE … WHERE status = 'Active'`, affected-row check) and `ClaimAsync` (`UPDATE … WHERE claimed_at IS NULL RETURNING`) are both genuine single-statement compare-and-swaps.
- **WAL mode + `busy_timeout=5000` on every opened connection** (`SqliteConnectionFactory.cs:293-299`); the maintenance connection deliberately drops to 250 ms for its own checkpoint/vacuum so a contended checkpoint defers instead of blocking, and explicitly restores 5000 before returning to the pool (`BankMaintenanceHostedService.cs:302-308`) — correct and easy to get wrong.
- **`noise_entries` has a working reaper** (`PurgeExpiredNoiseEntriesAsync`, every maintenance pass) — ADR-0029's fix is real, unlike F5/F6's siblings.
- **`EXPLAIN QUERY PLAN` on a populated, `ANALYZE`d scratch bank shows every hot lookup using an index** — `CountProjectEntries`/`PendingCount` via covering indexes, hash-keyed lookups via `idx_entries_hash`, and vector KNN correctly partition-pruned by `ctx` before the `MATCH`.

## Disconfirmed

- **"`count(*)` on a vec0 table is misleading."** Disconfirmed for this case: `vec_entries count(*)` matched the source row count exactly (5125/5125) in the scratch bank, and stayed exact after deleting half the rows. The real trap here is not `count(*)` miscounting rows — it is the *storage* that `count(*)` does not show you (F2).
- **"Bare `ON CONFLICT DO NOTHING` silently swallows `CHECK` violations."** Disconfirmed by direct test.
- **"`last_insert_rowid()` goes stale across the AFTER INSERT triggers."** Moot — the codebase never reads it.
- **"The `entries.embedding` BLOBs are write-only vestige"** (the orchestrator's O3). Disconfirmed — they are the source for `RebuildVecTableAsync` (F3).
