# Search latency: persisted chunk columns, vec0 native KNN, and the dead structure modality

Issue [#178](https://github.com/Arasz/ai-raccoon/issues/178) items 1+2, plus the `vec_structure`
dead-schema fix folded in. Branch `task/perf-impr-part2-search-knn`. Target: 1-2 days.

Blueprint only — no production code is written by this document. Implementation goes to
`dotnet-engineer`, test design to `test-engineer`.

**Status: executed.** WP1-WP3 shipped in #180; WP4 (benchmark) and WP5 (structure
writer) shipped in #197 (both merged into `main`). Measured end-to-end: hybrid search
p50 57 ms → ~8-9 ms (issue #178 baseline vs `SearchLatencyBenchmark`, PR #197). WP6's
deliberate re-pin proved unnecessary — the golden files (`reference-topk.json`,
`manifest.json`) stayed byte-identical, so the structure modality coming alive stayed
within ADR-0015's tolerance on the fixture corpus. WP7 (deferred FTS `snippet()`) was
not needed to hit the target and is filed as
[#198](https://github.com/Arasz/ai-raccoon/issues/198).

---

## 1. Evidence

Everything below was measured this session against a copy of the live bank
(`~/.ai-raccoon/memory.db`, 4,423 entries / 4,423 embedded rows, largest context
`project:ai-raccoon` = 2,065 rows) using the shipped `vec0` extension
(`HiraokaHyperTools.sqlite-vec` 0.1.9, `vec_version() = v0.1.9`). Probe scripts are in the
session scratchpad; they are reproducible from the numbers and shapes given here.

### 1.1 Where the time goes (measured, 20 reps, warm, median)

| Statement | Today | Rewritten | Note |
|---|---:|---:|---|
| Vector batch, `project:ai-raccoon`, k=300 | 32.20 ms | **3.05 ms** | KNN + persisted chunk columns, full production column list |
| — same, windows removed only | 18.79 ms | | isolates the window-function cost (42%) |
| FTS batch, same context, limit 300 | 41.23 ms | **9.60 ms** | direct join, persisted chunk columns, CTE deleted |
| Shared context, vector | — | **0.28 ms** | 86 rows |

Issue #178 reported 37.9 ms / 18.3 ms for the vector query and estimated the rewritten FTS at
~0.1 ms. The vector figures reproduce. **The FTS estimate does not**: the rewritten statement is
9.60 ms, not 0.1 ms. Attribution of what remains:

| FTS variant | Median |
|---|---:|
| full row (`e.value` + `snippet()`) | 9.60 ms |
| without `snippet()` | 3.32 ms |
| without `e.value` and without `snippet()` | 1.83 ms |
| `e.hash` only | 1.47 ms |

`snippet()` over 300 candidate rows costs ~6.3 ms; hauling `e.value` for 300 rows costs ~1.5 ms.
Both are payload costs on the candidate window, not matching costs. This is the same defect PR
#176 fixed for the vector modality (defer snippet computation to ranking survivors) and it is now
the largest single remaining item. See §5 WP7.

### 1.2 vec0 v0.1.9 capability probe

Verified directly against the shipped `vec0.dylib`:

- `distance_metric=cosine` is accepted in the `vec0(...)` declaration. **The current
  `vec_entries`/`vec_structure` declarations omit it, so they are L2 tables** — switching to
  `MATCH` without redeclaring would silently change the metric.
- vec0 KNN cosine distance is **bit-identical** to `vec_distance_cosine` on the same pair
  (delta 0.000e+00), so `StructureFusion.SimFromDistance` needs no change.
- A single `TEXT partition key` column works; both the partition value and `k` bind as
  parameters.
- `k` larger than the partition returns the whole partition rather than erroring.
- `JOIN entries … ORDER BY v.distance, e.path` on top of a KNN works — the existing tie-break
  survives.
- A trigger can `INSERT INTO vec0(rowid, ctx, embedding)` with a computed partition key.
- **`UPDATE` on a partition-key column is rejected** (`UPDATE on partition key columns are not
  supported yet`). Partition-key changes must be delete-then-insert.
- Maximum 4 partition-key columns; `NULL` partition values insert and query (`ctx IS NULL`) fine.

### 1.3 Partition-scheme decision (measured, and decisive)

Two candidate schemes were built and benchmarked on the same data:

| Scheme | Build (4,423 rows) | Query (k=300, project context) | Shared context |
|---|---:|---:|---:|
| **A — one `ctx TEXT partition key`** | **245 ms** | **1.50 ms** | **0.28 ms** |
| B — 4 partition keys mirroring `scope`/`project_id`/`context_label`/`workspace_id` | 71,100 ms | 423.26 ms | 19.03 ms |

Scheme B is 290× slower to build and 280× slower to query than A, and **13× slower than the
statement it was meant to replace**. It is rejected on measurement. Scheme A is the design.

Scheme A top-100 and top-300 hash lists are **IDENTICAL** to the current statement's output, with
identical distances to 9 decimal places.

### 1.4 Chunk-column semantics (the subtle one)

The current window functions run *inside* `WHERE {filter}` — numbering is per `source_file`
**within one search context**, not globally. A globally-computed persisted column would therefore
be wrong wherever one `source_file` appears under two contexts.

That case is real, not hypothetical: `SqliteMemoryStore.ShareAsync`
(`src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:225-248`) passes `source.SourceFile`
through to `AddContentAsync`, so promoting a chunk creates a `scope='shared'` row carrying the
same `source_file`. The live bank already contains **one such file**
(`docs/work/reviews/2026-08-07-api-deploy-guard-failure.md`, spanning 2 contexts).

Computing the columns per `(ctx, source_file)` — where `ctx` is the same partition key as §1.3 —
reproduces the current per-query values **exactly**: 0 mismatches across all four context shapes
(shared 86 rows, project:ai-raccoon 2,065, project:ai-badger 1,189, custom:jsaa 3). Backfill of
the whole 4,423-row bank takes **56 ms**.

### 1.5 Write-path audit

No code path anywhere in `src/` ever `UPDATE`s `scope`, `project_id`, `context_label`,
`workspace_id` or `source_file`. Group membership changes only by INSERT or DELETE. Consequently
the vec0 partition-key-immutability limitation from §1.2 **cannot be hit today**, and no
partition-key-repair trigger is needed. (State this in the PR; if a future change introduces such
an UPDATE it must add the delete-then-insert trigger.)

Paths that can change a `(ctx, source_file)` group:

| Path | File | Effect |
|---|---|---|
| `FileIngestor.InsertChunksAsync` | `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:71-151` | inserts chunks one at a time; **skips already-present chunks with a bare `continue` (`:102-105`)** |
| `SqliteMemoryStore.WriteAsync` | `.../SqliteMemoryStore.cs:40-90` | `memory_write` accepts a `sourceFile`, so a plain write can join a group |
| `SqliteMemoryStore.AddContentAsync` ← `ShareAsync` | `.../SqliteMemoryStore.cs:476-536`, `:225-248` | creates the cross-context `shared` group of §1.4 |
| `SqliteMemoryStore.DeleteAsync` | `.../SqliteMemoryStore.cs:296-323` | removes one row from a group (also reached by `SweepService`) |
| `SqliteMemoryStore.DeleteSourcePathAsync` | `.../SqliteMemoryStore.cs:351-384` | removes whole groups (+ directory cascade) — nothing left to renumber |
| `SqliteMemoryStore.DeleteContextAsync` | `.../SqliteMemoryStore.cs:325-343` | removes a whole `ctx` — nothing left to renumber |
| `SyncService.MergeRemoteAsync` | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:190-352` | tombstone DELETE at `:284-293` can remove group members |
| `MemorySchema` legacy dedup | `.../MemorySchema.cs:475-491` | one-shot, during migration |
| `WorkspaceService.ConsolidateAsync` | `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:58-61` | **does not pass `sourceFile`** → promoted row has `source_file = NULL` → no group |

Two findings that contradict assumptions in issue #178:

1. **The ingest loop does not know the chunk ordinal.** `foreach (var chunk in chunks)`
   (`FileIngestor.cs:87`) has no index, and existing chunks are skipped with `continue`, so loop
   position ≠ row position. `chunks.Count` is known but is *not* `total_chunks` either, for the
   same reason. Computing the columns in C# at write time would be wrong. The correct maintenance
   is a SQL recompute over the group after the mutation (§3.3).
2. **`SyncService.MergeRemoteAsync` omits `source_file` and `section` from its INSERT column list**
   (`SyncService.cs:251-267`), so every synced-in row lands with `source_file = NULL` and can never
   join a group. That is a pre-existing bug, **out of scope here** — note it in the PR and file it
   separately. It does mean sync's INSERT needs no chunk maintenance; only its tombstone DELETE does.

Consumers of the values — `SourceAffinityRanker`
(`src/AiRaccoon.Infrastructure/Sqlite/SourceAffinityRanker.cs:55,77,113`) — rely on 0-based,
contiguous numbering and test adjacency as `Math.Abs(a.ChunkIndex - b.ChunkIndex) == 1`. Any
recompute must preserve contiguity or consolidation silently stops merging siblings.

### 1.6 The structure modality is dead schema

- `vec_structure` has only a DELETE trigger (`MemorySchema.cs:74-76`); there is no insert/update
  trigger analogous to `vec_entries_au` (`:99-104`).
- `MemorySql.MarkEmbedded` (`MemorySql.cs:271-272`) sets `embed_state` and `embedding` only.
- `FileIngestor.InsertChunksAsync` writes `section = (string?)null` explicitly
  (`FileIngestor.cs:115`) and never writes `heading_path` or `structure_embedding`.
- History: `ce6b476` added the read side; `eac755e` added the writer as a standalone backfill tool;
  `ae75e74` deleted the writer entirely (537 deletions across `HeadingPathParser`,
  `StructureBackfillService`, `tools/AiRaccoon.StructureBackfill` and 230 lines of parser tests).
  The comment at `MemorySchema.cs:70-71` admits it.
- Live bank: 0 non-null `heading_path`, 0 non-null `structure_embedding`, `vec_structure` empty.

Effect today: `StructureFusion.Rank` supplies `structureSim = null` for every row, so
`Fused = 0.5 * contentSim + 0.5 * 0` — a strictly monotonic transform. **Ordering is unchanged**;
the modality is pure overhead, not a correctness bug. That matters for sequencing (§4).

**A simpler shape was checked and does not exist.** The `section` column is written as literal
`null` at ingest, so there is no already-populated structural signal to embed instead. Restoring
`HeadingPathParser` from `ae75e74^` (96 lines + 230 lines of tests, previously reviewed and
shipped) is cheaper and lower-risk than writing a new parser.

The deleted `StructureBackfillService` parsed the heading path from **the chunk's own text**
(`HeadingPathParser.Parse(row.Value)`) and left it NULL when the chunk contained no heading. That
is the semantics ADR-0004 ratified, and it means the writer can live in the embed transition,
which only sees `value`. It also means **the structure modality will be sparse** — only chunks
that themselves contain a heading get a vector. See §4 for why that makes a metrics gate
mandatory rather than optional.

---

## 2. Correcting one detail in the issue

Issue #178 says "all 6 search statements". There are **3** statements carrying window functions —
`SearchByFilter`, `VectorSearchByFilter`, `StructureVectorSearchByFilter` — each with **2** window
expressions, giving the 6 line numbers cited (`MemorySql.cs:115,117,141,143,160,162`). Derive the
list, do not trust the count:

```
grep -n "OVER (PARTITION" src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs
```

The acceptance criterion for WP2 is that this grep returns **nothing**, not that "6 sites were
edited".

---

## 3. Design

### 3.1 The context key (`ctx`) — one concept, three uses

A single SQL expression maps an `entries` row to the search context that can retrieve it. It is
the vec0 partition key, the chunk-numbering partition, and (implicitly) what `FilterFor` already
selects.

```
CASE
  WHEN <p>workspace_id IS NOT NULL
       THEN 'workspace:' || length(<p>project_id) || ':' || <p>project_id || ':' || <p>workspace_id
  WHEN <p>scope = 'shared'  THEN 'shared'
  WHEN <p>scope = 'project' THEN 'project:' || <p>project_id
  ELSE 'custom:' || length(<p>project_id) || ':' || <p>project_id || ':' || COALESCE(<p>context_label, '')
END
```

`<p>` is a column prefix (`""`, `"e."`, `"p."`, `"NEW."`, `"OLD."`). Expose it as a pure static
function `MemorySql.ContextKeyExpression(string prefix)` — a pure function, which the
static-classes invariant permits — used by the migration backfill, the vec0 trigger, and the chunk
recompute. The C# query side gets a matching `ContextKeyFor(context, projectId)`.

**Why the length prefix.** The naive `'custom:' || project_id || ':' || label` encoding collides:
`(project_id='a:b', label='c')` and `(project_id='a', label='b:c')` both produce `'custom:a:b:c'`
— **verified**. Length-prefixing yields `'custom:3:a:b:c'` vs `'custom:1:a:b:c'`. The existing
`FilterFor` label parser (`SqliteMemoryStore.cs:822-830`) already assumes project ids contain no
`:`; do not inherit that assumption into a storage key.

This encoding exists in two languages (the SQL fragment and the C# builder), which is exactly the
hand-maintained-list hazard the project invariants warn about. Required check: for every context
shape, insert a row, let the trigger compute `ctx`, and assert it equals the C# builder's output
(WP1 acceptance).

**Exactness.** `ctx` partitions `entries` precisely into the row sets the four `FilterFor` shapes
select — verified per shape in §1.4. A `scope='custom'` row with a NULL `context_label` maps to
`'custom:N:P:'` and is unreachable by any query, which is exactly its status today.

**One asymmetry to know about.** `ctx` for a `scope='project'` row ignores `context_label`, because
the project filter does. A project-scoped row carrying a non-null `context_label` therefore groups
with the unlabelled ones — matching the query. Grouping by the four raw columns instead would
split them and diverge. This was verified with a dedicated case; it is why the trigger and
recompute must use the `ctx` expression, not a four-column comparison.

### 3.2 Schema v2 — one version bump covering everything

`MemorySchema.CurrentVersion: 1 → 2`, one ladder step, per ADR-0011. Contents:

1. `ALTER TABLE entries ADD COLUMN chunk_index INTEGER NOT NULL DEFAULT 0` (guarded by the
   existing `pragma_table_info` pattern).
2. `ALTER TABLE entries ADD COLUMN total_chunks INTEGER NOT NULL DEFAULT 0`.
3. Bank-wide backfill of both columns (§3.3, bank-wide form). Measured: **56 ms / 4,423 rows**.
4. Rebuild `vec_entries` as
   `vec0(ctx TEXT partition key, embedding float[384] distance_metric=cosine)`, repopulating from
   `entries.embedding` where `embed_state='embedded'`. Measured: **245 ms / 4,423 rows**.
5. Rebuild `vec_structure` with the same shape. It is empty everywhere, so this is free.
6. Replace `vec_entries_au` / `vec_entries_pending` / `vec_entries_ad` so the insert trigger
   supplies `ctx`.
7. Add `vec_structure_au`, mirroring `vec_entries_au` but gated on
   `NEW.structure_embedding IS NOT NULL`. **A no-op until WP5 lands a writer** — this is what lets
   the whole task be one version bump.

Follow the existing FTS-rebuild precedent (`MemorySchema.cs:382-442`): one `BEGIN IMMEDIATE`
transaction, rethrow on failure, so a crash mid-rebuild cannot leave a bank with a half-built vec
table. Do **not** copy the soft/`return false` pattern used for the bucket indexes — a bank whose
`vec_entries` is an empty shell answers every vector query with silence, which is worse than
failing to open.

The `entries.embedding` blobs are the source of truth for the rebuild, so no re-embedding is
needed and the migration is offline-safe.

Dimension: the DDL hardcodes 384 today (`MemorySchema.cs:67`) with a comment noting the embedder
owns the dimension if the model is not all-MiniLM. Read the existing table's declared dimension
out of `sqlite_master` and preserve it in the rebuild rather than re-hardcoding 384, or a bank
using a different model loses its vectors.

### 3.3 Chunk-column maintenance — recompute at the mutation boundary

The recompute, for one `(ctx, source_file)` group:

```sql
WITH numbered AS (
  SELECT id,
         ROW_NUMBER() OVER (PARTITION BY <ctx>, source_file ORDER BY id) - 1 AS ci,
         COUNT(*)     OVER (PARTITION BY <ctx>, source_file)              AS tc
  FROM entries
  WHERE source_file IS NOT NULL AND (<ctx>) = @ctx AND source_file = @sourceFile)
UPDATE entries
   SET chunk_index  = (SELECT ci FROM numbered n WHERE n.id = entries.id),
       total_chunks = (SELECT tc FROM numbered n WHERE n.id = entries.id)
 WHERE entries.id IN (SELECT id FROM numbered);
```

Bank-wide form: drop the two `WHERE` predicates on `ctx`/`source_file` from the CTE and widen the
UPDATE accordingly. Rows with `source_file IS NULL` keep the column defaults `0 / 0`, matching the
current `CASE WHEN e.source_file IS NULL THEN 0` branches.

Verified: 0 mismatches against the current per-query window formula across every context shape,
including the cross-context share case and the labelled-project-row case. One-shot cost for a
200-chunk group: **1.0 ms**.

**Triggers were prototyped and rejected — with numbers.** An `AFTER INSERT`/`AFTER DELETE` pair
would be unforgettable-by-construction, which is the shape the invariants prefer, but it fires
once per row and each firing rescans the group: a 200-chunk file ingest measured **838 ms**
(and its delete 904 ms) versus **1.0 ms** for one recompute after the loop — ~800×. At watch
re-ingest frequency that is not affordable. Explicit recompute at the mutation boundary wins;
the cost is that the call sites below must not be forgotten, which is what the WP3 tests are for.

**A scoping trap, found the hard way.** The first trigger prototype compared
`(<ctx> over p.*) = (<ctx> over unqualified columns)` inside a correlated subquery. SQLite resolves
the unqualified columns to the *innermost* scope (`p`), so the predicate silently became
`x = x` — always true — and the numbering degenerated to grouping by `source_file` alone. **It
looked perfect on the happy path** (a single-context 5-chunk ingest produced 0..4 / 5) and only
diverged once a second context appeared. Always qualify the outer row as `entries.*`, and make
the cross-context share case a red test before trusting any version of this SQL.

Call sites (from §1.5):

| Site | When | Form |
|---|---|---|
| `FileIngestor.InsertChunksAsync` | once after the chunk loop | scoped `(bucket ctx, path)` |
| `SqliteMemoryStore.WriteAsync` | when `request.SourceFile` non-null | scoped |
| `SqliteMemoryStore.AddContentAsync` | when `sourceFile` non-null (covers `ShareAsync`, promotion) | scoped, on the **target** ctx |
| `SqliteMemoryStore.DeleteAsync` | when the deleted row had a `source_file` — read it alongside the existing pre-delete `SelectScopeByHashAndProject` | scoped, using the pre-delete values |
| `SyncService.MergeRemoteAsync` | once, after the merge transaction | bank-wide (56 ms, and sync is rare) |
| `MemorySchema` v2 ladder | once | bank-wide |

`DeleteSourcePathAsync`, `DeleteContextAsync` and `WorkspaceService.ConsolidateAsync` need
**nothing**: the first two remove entire groups or entire contexts, and the third produces rows
with `source_file = NULL`. Assert that in tests rather than adding calls "for safety".

### 3.4 The three SQL rewrites

**`SearchByFilter`** — delete the `MATERIALIZED` CTE entirely. The comment at
`MemorySql.cs:106-109` justifies the CTE by FTS5's `bm25()` not sharing a SELECT with a window
function and by O(n²) inlined-window re-execution; with no window functions, neither constraint
exists. New shape:

```sql
SELECT e.hash AS Hash, 0 AS Seq, bm25(entries_fts, 1.0, 8.0, 16.0) AS Ranking,
       e.path AS Path, snippet(entries_fts, 0, '', '', '…', 12) AS Snippet,
       e.value AS Value, e.source_file AS SourceFile,
       e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks
FROM entries_fts
JOIN entries e ON e.id = entries_fts.rowid
WHERE entries_fts MATCH @query AND {filter}
ORDER BY bm25(entries_fts, 1.0, 8.0, 16.0)
LIMIT @limit
```

Verified: hashes, rankings and chunk columns all **IDENTICAL** to the current statement.
`{filter}` keeps the `e.` alias, so `FilterFor(context, projectId, "e.")` is unchanged.

**`VectorSearchByFilter`** and **`StructureVectorSearchByFilter`** — identical rewrite against
`vec_entries` / `vec_structure`:

```sql
SELECT e.hash AS Hash, 0 AS Seq, e.path AS Path, e.value AS Value,
       v.distance AS Distance, e.source_file AS SourceFile,
       e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks
FROM vec_entries v
JOIN entries e ON e.id = v.rowid
WHERE v.ctx = @ctx AND v.embedding MATCH @queryVector AND k = @limit
ORDER BY v.distance, e.path
```

Note the filter substitution disappears from these two statements — the partition key replaces it,
which is why the scheme must be exact (§3.1). `SqliteMemoryStore.SearchAsync` adds `ctx` to
`SearchParameters` beside `limit` and `queryVector`; `FilterFor` stays for the FTS statement.

**`ORDER BY v.distance, e.path` is retained** and verified to work on top of a KNN, so the
existing tie-break among returned rows is preserved exactly.

### 3.5 Candidate window (`k`) and result identity

`k = @limit` where `limit = CandidateWindowFor(query.Limit, query.CandidateWindow)` — unchanged,
`max(limit*3, 100)` by default (`SqliteMemoryStore.cs:638-641`). Because the partition is exact,
`k` needs **no over-fetch and no residual filtering**: the KNN searches precisely the rows the old
`WHERE {filter}` selected. Verified identical at k=300 for both top-100 and top-300.

One bounded non-determinism: the old statement ordered by `(distance, path)` *then* took `LIMIT k`,
so a distance tie straddling the k-th position was broken by path. vec0 picks its k rows by
distance alone, then the outer `ORDER BY` sorts within them — so at an exact distance tie on the
boundary the two can select different rows. Measured on this bank: the 400 nearest rows have
**400 distinct distances** (zero duplicates), and rank 300 (0.610662758) ≠ rank 301
(0.610692978). The risk needs exactly-equal float32 cosine distances at the boundary.

This is precisely the case ADR-0015's boundary rule was written for: a hash on only one side is
not a difference when its ranking is within tolerance of the k-th ranking. Do not add a
work-around; note the property in the SQL comment and let the gate absorb it.

### 3.6 Structure writer (WP5)

1. Restore `src/AiRaccoon.Core/Chunking/HeadingPathParser.cs` and
   `tests/AiRaccoon.Tests/Unit/Chunking/HeadingPathParserTests.cs` from `ae75e74^` unmodified.
   Restore the parser and its tests only — **not** `StructureBackfillService` or
   `tools/AiRaccoon.StructureBackfill`, which were standalone tooling the schema trigger now
   replaces.
2. In `EntryEmbedder`, derive `HeadingPathParser.Parse(value)` per row, collect the distinct
   non-empty paths across the batch, embed those once, and write `heading_path` +
   `structure_embedding` alongside `embedding`. Extend `MemorySql.MarkEmbedded` to set all four
   columns; a row whose heading path is empty writes NULL to both, so the `vec_structure_au`
   trigger's `NEW.structure_embedding IS NOT NULL` guard leaves it alone.
   Embed cost: one extra ONNX inference per **distinct** heading path per batch (batch size 32,
   `EntryEmbedder.cs:16`), not per row. Heading paths repeat heavily within a document, so the
   dedupe is what keeps this affordable — measure it, do not assume it.
3. Healing existing banks: `EntryEmbedder.ConfigureAsync` already re-embeds the whole bank when
   the engine fingerprint changes (`EntryEmbedder.cs:42-44`), but nothing changes the fingerprint
   here, so **that path will not fire on its own**. Do not rely on it. Add an explicit
   `structure_embedding IS NULL AND embed_state='embedded'` backfill pass driven from the v2
   migration or from the existing pending-embed loop, and make "the live bank actually has
   `vec_structure` rows" an acceptance criterion with a counted gate — not an assumption.
   `EngineFingerprint` is not extended; the structure vector uses the same engine as the content
   vector by construction.

---

## 4. Sequencing, and what happens to the golden files

The retrieval gates are the correctness net, so the order is chosen to keep them meaningful.

**WP1-WP3 (chunk columns + KNN) are provably identity-preserving** — measured identical hashes,
rankings and chunk columns. They must land **before** any deliberate baseline change, so that the
golden files staying green *is the evidence* that the SQL rewrite preserved behaviour. That
evidence is unrecoverable if the baseline moves first.

**WP5 (structure) is deliberately behaviour-changing.** Today `Fused = 0.5 * contentSim` is a
strictly monotonic transform of the content similarity, so ordering is unchanged (§1.6). The
moment structure vectors exist, `alpha`-fusion comes alive and the vector modality reorders — which
changes RRF rank positions and therefore the merged output. **ADR-0015's 5e-3 ranking tolerance
will not absorb this**, and it is not meant to: the tolerance exists for cross-platform SIMD
spread (1e-4..3e-3), not for a modality switching on. A deliberate golden re-pin is required.

So: exactly one re-pin, at WP6, attributable to exactly one cause.

**The re-pin is gated on quality, not on convenience.** Because heading paths are parsed per chunk,
only chunks that themselves contain a heading get a vector (§1.6), so rows without one score
`alpha * contentSim` against neighbours scoring `alpha * contentSim + (1-alpha) * structureSim`.
If the populated fraction is low, `alpha = 0.5` is a large uniform penalty on the majority and
retrieval can get *worse*. `BaselineMetricsTests` (nDCG@5 / MRR / recall@5) is the arbiter:

- Metrics ≥ pinned baseline → re-pin the golden files, recording the measured deltas and the
  populated-row fraction in the commit message.
- Metrics regress → **do not re-pin**. Ship the structure writer with the fusion effectively off
  (bank-scoped `retrieval.structureAlpha = 1.0`, the existing setting at
  `StructureFusion.AlphaSettingKey`) so the schema and writer are correct and dormant, and open a
  follow-up to tune `alpha` or to widen heading-path derivation to non-heading chunks.

Either outcome is a shippable result. Decide it with the measurement, not in advance.

---

## 5. Work packages

Parallelism is limited by two shared files: `MemorySql.cs` (WP1, WP2, WP5) and `SqliteMemoryStore.cs`
(WP2, WP3). Lanes that share a file **serialize**.

```
WP0 ──▶ WP1 ──┬──▶ WP2 ──▶ WP3 ──▶ WP4 ──┐
              │   (lane A: MemorySql.cs, SqliteMemoryStore.cs, FileIngestor.cs)
              └──▶ WP5 ────────────────────┴──▶ WP6 ──▶ (WP7 optional)
                  (lane B: Core/Chunking, Embedding/ — MemorySql.MarkEmbedded only)
```

### WP0 — Pin the baseline

- **Scope**: capture current-state evidence before any edit.
- **Files**: none (artifacts to the scratchpad).
- **Do**: run the three suites on the untouched branch; record the golden file hash; run the WP4
  harness shape against the live-bank copy to capture the before-numbers on *this* machine.
- **Acceptance**: three green runs recorded; a before-latency number exists.
- **Gate**: `dotnet test --filter "Speed=Fast"` · `dotnet test --filter "Speed=Slow"` ·
  `dotnet test --filter "Category=Retrieval"`.

### WP1 — Schema v2

- **Scope**: §3.2 in full, including the dormant `vec_structure_au` trigger and the
  `ContextKeyExpression` fragment (§3.1).
- **Files**: `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`,
  `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` (fragment + recompute constants only).
- **Tests** (`tests/AiRaccoon.Tests/Integration/MemorySchemaVersionTests.cs`, whose `OpenAsync`
  helper at `:99-108` already opens an in-memory bank with `LoadVector()`):
  - a v1 bank with rows migrates to v2, `PRAGMA user_version = 2`, columns present and backfilled;
  - `vec_entries` after migration is declared with `ctx TEXT partition key` **and**
    `distance_metric=cosine`, and holds one row per embedded entry;
  - a bank whose `vec_entries` declares a non-384 dimension keeps that dimension;
  - **encoding agreement**: for each of the four context shapes, the trigger-computed `ctx` equals
    the C# `ContextKeyFor` output;
  - **collision red test**: `(project_id='a:b', label='c')` and `(project_id='a', label='b:c')`
    must land in different partitions. Write it against the naive `':'` encoding first, watch it
    fail, then switch to the length-prefixed form — this is the check that must be seen failing.
  - migration is idempotent (second `EnsureAsync` is a no-op) and re-entrant after a simulated
    mid-rebuild crash.
- **Acceptance**: all of the above; migration of a 4,423-row bank completes in well under a second
  (measured components: 56 ms + 245 ms).
- **Gate**: `dotnet test --filter "Speed=Slow"`.

### WP2 — The three SQL rewrites

- **Scope**: §3.4 and §3.5. Delete the CTE, delete all window functions, switch both vector
  statements to partitioned KNN, thread `ctx` through `SearchParameters`.
- **Files**: `MemorySql.cs`, `SqliteMemoryStore.cs` (`SearchAsync` parameter plumbing,
  `ContextKeyFor`).
- **Depends on**: WP1 (needs the partition key and the columns).
- **Tests**: a search-identity test that runs a fixed query set against a seeded bank and asserts
  the result list is unchanged; explicit coverage for all four context shapes; a test that the
  shared context returns shared rows only (the partition-leak case); a test that a `k` exceeding
  the partition size returns the whole partition rather than erroring.
- **Acceptance**: `grep -n "OVER (PARTITION" src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`
  returns nothing; `grep -n "MATERIALIZED" …` returns nothing; **the golden files are unchanged
  and green**.
- **Gate**: `dotnet test --filter "Category=Retrieval"` and `dotnet test --filter "Speed=Slow"`,
  both green **without touching any golden file**.

### WP3 — Chunk-column maintenance

- **Scope**: §3.3 recompute at the five call sites plus the bank-wide sync call.
- **Files**: `SqliteMemoryStore.cs`, `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs`,
  `src/AiRaccoon.Infrastructure/Sync/SyncService.cs`, `MemorySql.cs` (recompute constants).
- **Depends on**: WP1; shares files with WP2, so it follows WP2 in lane A.
- **Tests** — one per write path, all currently missing (§1.5 gap):
  - ingest a 5-chunk file → `(0..4, 5)`;
  - re-ingest the same file unchanged (every chunk hits the `continue` skip) → numbering unchanged
    — this is the test that catches a C#-loop-index implementation;
  - `memory_write` with an existing `source_file` → the new row appends and `total_chunks` grows;
  - **`ShareAsync` of one chunk → the shared row is `(0, 1)` in its own context and the project
    rows are untouched** — the cross-context case, and the one that caught the scoping bug in
    §3.3. Make this the first red test.
  - `DeleteAsync` of a middle chunk → survivors renumber contiguously (`SourceAffinityRanker`'s
    adjacency test depends on it);
  - `DeleteSourcePathAsync` and `DeleteContextAsync` → group/context gone, no stale rows, and
    **no recompute call is needed** (assert the absence rather than adding one);
  - `WorkspaceService.ConsolidateAsync` → promoted row has `source_file = NULL` and `(0, 0)`;
  - sync tombstone delete → bank-wide recompute leaves every group contiguous.
- **Acceptance**: for a seeded multi-context bank, the persisted columns equal the old per-query
  window formula for every context — assert it as a property over the whole bank, not per case.
- **Gate**: `dotnet test --filter "Speed=Slow"` and `dotnet test --filter "Category=Retrieval"`,
  golden files still untouched.

### WP4 — Measurement

- **Scope**: before/after end-to-end search latency, mirroring issue #178's method.
- **Files**: `benchmarks/AiRaccoon.Benchmarks/` (new benchmark) — note there is **no** existing
  search-latency harness; `EmbeddingLatencyBenchmark` measures the embedder's own brute-force
  search over an in-memory corpus, not `SqliteMemoryStore`/vec0.
- **Depends on**: WP2 + WP3.
- **Do**: a BenchmarkDotNet benchmark over `SqliteMemoryStore.SearchAsync` against a fixture bank
  of realistic size, reporting p50/p90 for `scope=all, limit=10`; compare against WP0's numbers on
  the same machine. Report per-statement medians too, so a miss can be attributed.
- **Acceptance**: p50 measurably improved and the components consistent with §1.1. Publish the
  number, whatever it is.
- **Target, and what it rests on**: issue #178 estimates ~10 ms. From §1.1 the rewritten
  statements sum to roughly 9.6 (FTS) + 3.1 (vector) + ~3 (structure, *once WP5 populates it*)
  ≈ 16 ms for the largest context, plus the shared context and fixed overhead. **~15-20 ms is the
  honest expectation for WP1-WP3+WP5; ~10 ms needs WP7.** Record the actual number rather than
  restating the estimate.
- **Gate**: `dotnet run --project benchmarks/AiRaccoon.Benchmarks --bench --filter '*Search*' --job short`.

### WP5 — Structure writer (parallel lane B)

- **Scope**: §3.6.
- **Files**: `src/AiRaccoon.Core/Chunking/HeadingPathParser.cs` (restored),
  `tests/AiRaccoon.Tests/Unit/Chunking/HeadingPathParserTests.cs` (restored),
  `src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs`, `MemorySql.cs` (`MarkEmbedded` only —
  coordinate with lane A).
- **Depends on**: WP1 (the `vec_structure_au` trigger). Runs parallel to WP2/WP3.
- **Red test**: cherry-pick `StructurePopulationTests.Ingest_MarkdownWithHeadings_PopulatesTheStructureVectorTable`
  from commit `1fa99c1` (the diagnosis branch). It currently passes on `vec_entries > 0` and fails
  on `structure_embedding` non-null — confirm it fails **for that reason** before fixing.
- **Additional tests**: distinct heading paths are embedded once per batch (assert the generator
  call count, not the timing); a chunk with no heading writes NULL/NULL and produces no
  `vec_structure` row; the existing-bank backfill pass populates a bank embedded before this
  change.
- **Acceptance**: after ingest, `vec_structure` row count > 0 and equals the count of non-null
  `structure_embedding`; the live bank heals to a **counted, non-zero** `vec_structure`; the
  populated fraction is recorded (WP6 needs it).
- **Gate**: `dotnet test --filter "Speed=Slow"`.

### WP6 — The deliberate re-pin

- **Scope**: §4. Re-pin `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` (and
  `manifest.json`) via `scripts/regenerate-retrieval-golden.py`.
- **Depends on**: WP2, WP3, WP5 all green.
- **Acceptance**: `BaselineMetricsTests` nDCG@5 / MRR / recall@5 **≥** the WP0 baseline. If they
  regress, do not re-pin — take the `alpha = 1.0` branch of §4 instead.
- **Gate**: `dotnet test --filter "Category=Retrieval"`, with the commit message recording the
  metric deltas, the populated-row fraction, and the reason the re-pin is legitimate.
- Add an ADR (or extend ADR-0004) recording that the structure modality now has a writer and that
  the baseline moved because of it. Per the documentation instructions, a behaviour change of this
  size gets its decision recorded, not just a commit message.

### WP7 — Deferred FTS snippets (optional; decided by WP4)

- **Scope**: apply PR #176's pattern to the FTS modality — return the FTS candidate window without
  computing `snippet()` for all 300 rows, and resolve snippets for ranking survivors only.
- **Measured prize**: 9.60 ms → 3.32 ms per FTS batch (§1.1).
- **Trigger**: run it only if WP4 lands materially above the ~10 ms target and the schedule allows.
  Otherwise file it, with the §1.1 table as its evidence.

#### Why the resolution filters by rowid, not hash

Recorded here 2026-08-09, moved out of a code comment in `MemorySql.FtsSnippetsForSurvivors` that
was the only place it existed. Measured against a live-bank copy at K=20 survivors:

| survivor filter | SQLite plan | per-query |
|---|---|---|
| `e.hash IN (…)` | `SCAN entries_fts VIRTUAL TABLE INDEX 0:M3` | **7.9–17.7 ms** |
| `entries_fts.rowid IN (…)` alongside `MATCH` | `INDEX 0:=M3` | **2.9–3.2 ms** |

`hash` is not a column FTS5 can index, so filtering on it forces a full `MATCH` scan of the term
across the whole corpus; `entries_fts.rowid` (= `entries.id`) uses FTS5's rowid lookup. Roughly
5–6× on this corpus, and the reason the resolution statement carries row ids rather than hashes.

---

## 6. Risks

**Encrypted banks.** The v2 ladder rebuilds two virtual tables on a bank opened through
`SqliteEncryptionInit`. `SqliteConnectionFactoryEncryptionTests:251,275` already asserts the
current-key probe path does **not** call `EnsureAsync`, so the probe stays cheap — but confirm the
rebuild runs inside the keyed connection and add an encrypted-bank migration test. A migration that
half-runs on an encrypted bank is the worst failure mode here.

**Sync schema interop.** `SyncService` attaches a remote bank as `remote.` and reads its `entries`.
A v1 remote has no `chunk_index`/`total_chunks`, and its `vec_entries` has no partition key. The
merge SELECT is an explicit column list (`SyncService.cs:251-267`) that names neither, so it keeps
working — **verify this rather than assuming it**, and add a test merging a v1 snapshot into a v2
bank. The bank-wide recompute after the merge (§3.3) then fixes numbering for whatever arrived.

**Old-bank backfill cost.** 56 ms + 245 ms at 4,423 rows, both linear-ish. A 10× bank is ~3 s of
one-time open cost. Acceptable, but log it at information level so a slow first open after upgrade
is explicable rather than mysterious.

**vec0 rebuild for large banks.** The rebuild holds a write transaction for its duration. On a very
large bank that blocks concurrent openers. Several Claude sessions share this repo's live bank, so
the first post-upgrade open should be a deliberate act, not a surprise inside a search.

**The encoding lives in two languages.** Mitigated by the WP1 agreement test, but it is the most
likely place for a future silent divergence. Keep the SQL fragment as the single source and derive
the C# side from the same shape.

**Sparse structure vectors.** §4 covers it; the metrics gate is the control.

**Pre-existing, out of scope, worth filing**: `SyncService.MergeRemoteAsync` drops `source_file`
and `section` on merge (§1.5); `memory_delete` with a null hash returns a generic invocation error
instead of a validation error (issue #178, "Also observed").

---

## 7. Revert conditions

Partitioned by work package, because these fail independently.

- **WP1-WP3 cannot hold result identity** (golden files move without WP5 having landed): the
  partition scheme or the chunk semantics are wrong. Revert WP2's vector KNN first — it is the
  only part whose semantics could differ — keeping the chunk columns and the FTS rewrite, which
  are independently verified identical and still deliver 41.2 → 9.6 ms. Re-derive the partition
  scheme against the failing context before retrying.
- **WP4 shows no material end-to-end improvement** despite green per-statement numbers: the
  bottleneck moved (likely to the payload costs in §1.1). Keep everything — nothing regressed —
  and pivot to WP7 rather than reverting.
- **WP5's metrics regress**: do not revert the schema or the writer. Ship with
  `retrieval.structureAlpha = 1.0` per §4 and skip WP6. The dead schema is fixed either way, which
  was the point.
- **The v2 migration fails on a real bank** (encrypted, large, or concurrent): this is the only
  hard revert. The whole task ships together or not at all, because WP2 depends on the partition
  key existing. Roll back to v1 and re-approach the rebuild as an explicit maintenance command
  (ADR-0010 territory) rather than an on-open migration.

Minimum shippable subset if time runs out: **WP1 + WP2 + WP3** — measured 32.2 → 3.1 ms and
41.2 → 9.6 ms, golden files unmoved, no baseline re-pin, no ADR needed.
