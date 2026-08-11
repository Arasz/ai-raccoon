# Memory Source Normalization — Implementation Plan

**Date:** 2026-08-11
**Schema version:** 4 → 5
**Spike:** `scripts/spike_memory_source.py` (passed — JOIN faster than CASE/WHEN)

---

## 1. Architecture Decision: FTS Strategy

### Decision: Keep `source_file`/`section` as denormalized FTS-backing columns on `entries`

**Why not remove them?**

The `entries_fts` virtual table is an FTS5 external-content index:

```sql
CREATE VIRTUAL TABLE entries_fts USING fts5(
    value, source_file, section,
    content='entries', content_rowid='id'
);
```

The triggers (`entries_fts_ai`, `entries_fts_ad`, `entries_fts_au`) directly reference `new.source_file` and `new.section` on the entries row. FTS5 external content tables rely on the content table holding the indexed columns — removing them would require:

1. Switching to a non-external-content FTS table (doubles storage, loses automatic delete/update sync)
2. Or manually maintaining FTS rows from application code (fragile, violates single-source-of-truth)

**The chosen approach:**

- `memory_source` is the **canonical** source identity table
- `entries.source_id` FK points to `memory_source`
- `entries.source_file` and `entries.section` remain as **denormalized FTS-backing columns**
- The write path populates both `source_id` AND the denormalized columns from the source record
- Read paths that need full source identity JOIN to `memory_source`
- The denormalized columns are **write-through mirrors** — never updated independently

**Risk assessment (what breaks if FTS is wrong):**

| Failure | Impact | Mitigation |
|---------|--------|------------|
| `source_file`/`section` on entries diverge from `memory_source` | FTS index returns stale results; SourcePathQuery misroutes | Write path always copies from source row; integrity check in MigrateToV5 |
| Trigger references wrong columns | Silent FTS corruption | Migration rebuilds FTS from scratch (same pattern as v1) |
| Missing `source_id` on entries | FK violation on insert | All write paths updated; migration backfills |
| Chunk recompute PARTITION BY breaks | Duplicate chunk_index values | RecomputeChunkColumns updated to join source for source_file |

---

## 2. Target Schema

### New table: `memory_source`

```sql
CREATE TABLE IF NOT EXISTS memory_source (
    id            INTEGER PRIMARY KEY,
    source_type   TEXT NOT NULL CHECK(source_type IN ('file','transcript','manual')),
    source_locator TEXT NOT NULL,  -- file path, session id, or manual tag
    section       TEXT NULL,       -- heading anchor within the source
    heading_path  TEXT NULL,       -- full heading hierarchy (e.g. "## Arch > ### DB")
);
-- UNIQUE constraint uses expression (COALESCE) which is illegal in CREATE TABLE.
-- Separate index handles NULL/'' dedup:
CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source_identity
    ON memory_source(source_type, source_locator, COALESCE(section, ''));
```

**Note (simpler alternative considered):** A `GENERATED ALWAYS AS (CASE WHEN ...)` column on `entries` for `source_type` would avoid the JOIN entirely for type lookups. Rejected because: (a) it duplicates the classification logic; (b) it doesn't give us the canonical source identity table the normalization aims for; (c) the spike already showed the JOIN is faster than CASE/WHEN. If the `memory_source` table proves unnecessary in practice, the generated column is the fallback.

### Modified table: `entries` (add FK column)

```sql
-- New column (nullable during migration, NOT NULL after backfill)
ALTER TABLE entries ADD COLUMN source_id INTEGER NULL
    REFERENCES memory_source(id) ON DELETE RESTRICT;
CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id);
```

### `source_file`/`section` on entries: **kept as-is** (denormalized FTS backing)

No column changes. The write path copies from the referenced `memory_source` row.

---

## 3. Work Packages

### Dependency graph

```
WP0 (foundation)
  └─► WP1 (schema + migration)
        ├─► WP2 (write path)
        │     ├─► WP4 (read path + queries)
        │     └─► WP5 (sync) ← depends on WP2's ResolveOrCreateAsync
        └─► WP3 (chunk recompute)
              └─► WP4
                    └─► WP6 (ingestion + tools)
                          └─► WP7 (integration + cleanup)
```

WP2 and WP3 can run in parallel after WP1. WP5 depends on WP2 (needs `ResolveOrCreateAsync`), not just WP1.

---

### WP0: Domain Model + Interface

**Goal:** Define `MemorySource` record and the lookup/resolve abstraction.

**Files to create/change:**
- `src/AiRaccoon.Core/Memory/MemorySource.cs` — **NEW**
- `src/AiRaccoon.Core/Memory/MemorySourceId.cs` — **NEW** (strongly-typed ID)
- `src/AiRaccoon.Core/Memory/IMemorySourceStore.cs` — **NEW** (interface)
- `src/AiRaccoon.Core/Memory/MemoryWriteRequest.cs` — keep `SourceFile`/`Section` but add docs noting it maps to source

**TDD — RED tests first:**
- `tests/AiRaccoon.Tests/Unit/Memory/MemorySourceTests.cs` — **NEW**
  - `MemorySource_Equality_SameLocatorSameType_AreEqual`
  - `MemorySource_Equality_DifferentType_AreNotEqual`
  - `SourceLocator_Empty_Throws`
  - `SourceType_Invalid_Throws`

**Acceptance criteria:**
- [ ] `MemorySource` record compiles with `Id`, `SourceType`, `SourceLocator`, `Section`, `HeadingPath`
- [ ] `SourceType` enum: `File`, `Transcript`, `Manual`
- [ ] `IMemorySourceStore` has `ResolveOrCreateAsync(sourceType, locator, section, headingPath) → MemorySource`
- [ ] All unit tests GREEN

---

### WP1: Schema DDL + Migration (v4→v5)

**Goal:** Create `memory_source` table, add `source_id` FK to entries, backfill existing rows, bump schema version.

**Files to change:**
- `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`
  - Add `memory_source` table to `Ddl`
  - Add `source_id INTEGER NULL` column + index to entries Ddl
  - Bump `CurrentVersion` to 5
  - Add `MigrateToV5Async` method
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemorySourceStore.cs` — **NEW**
- `tests/AiRaccoon.Tests/Integration/MemorySchemaVersionTests.cs`
  - Add test: `EnsureAsync_FromV4_CreatesMemorySourceTable_AndBackfills`
  - Add test: `EnsureAsync_FromV4_EntriesHaveSourceId`
  - Add test: `EnsureAsync_FreshBank_HasMemorySourceTable`
  - Add test: `EnsureAsync_V5Bank_SkipsMigration`

**Migration logic (MigrateToV5Async):**

```sql
-- 0. Ensure FK enforcement during migration
PRAGMA foreign_keys = ON;

-- 1. Create memory_source table (no expression UNIQUE — separate index below)
CREATE TABLE IF NOT EXISTS memory_source (
    id            INTEGER PRIMARY KEY,
    source_type   TEXT NOT NULL CHECK(source_type IN ('file','transcript','manual')),
    source_locator TEXT NOT NULL,
    section       TEXT NULL,
    heading_path  TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source_identity
    ON memory_source(source_type, source_locator, COALESCE(section, ''));

-- 2. Populate from existing entries (deduplicated via CTE — single CASE expression)
INSERT OR IGNORE INTO memory_source (source_type, source_locator, section)
SELECT source_type, source_locator, section FROM (
    SELECT DISTINCT
        CASE
            WHEN source_file LIKE 'hermes/%' OR source_file LIKE '%/hermes/%' THEN 'transcript'
            WHEN source_file IS NULL OR source_file = '' THEN 'manual'
            ELSE 'file'
        END AS source_type,
        COALESCE(source_file, '') AS source_locator,
        section
    FROM entries
);

-- Also insert catch-all for NULL source_file/section
INSERT OR IGNORE INTO memory_source (source_type, source_locator, section)
VALUES ('manual', '', NULL);

-- 3. Add source_id column (NULLable during backfill)
ALTER TABLE entries ADD COLUMN source_id INTEGER NULL;
-- Note: REFERENCES clause is advisory in SQLite unless PRAGMA foreign_keys = ON.
-- We enforce FK in MigrateToV5Async by enabling the pragma.

-- 4. Backfill source_id (single UPDATE with CTE — no duplicate CASE)
UPDATE entries SET source_id = (
    SELECT ms.id FROM memory_source ms
    WHERE ms.source_locator = COALESCE(entries.source_file, '')
      AND (ms.section IS entries.section OR (ms.section IS NULL AND entries.section IS NULL))
      AND ms.source_type = CASE
          WHEN entries.source_file LIKE 'hermes/%' OR entries.source_file LIKE '%/hermes/%' THEN 'transcript'
          WHEN entries.source_file IS NULL OR entries.source_file = '' THEN 'manual'
          ELSE 'file'
      END
);

-- 5. Verify backfill: SELECT COUNT(*) FROM entries WHERE source_id IS NULL must be 0

-- 6. Create index on source_id
CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id);

-- 7. Rebuild FTS (same drop/recreate/populate pattern from MigrateToV1Async)
--    Drop triggers, drop FTS, recreate both, repopulate FTS from entries.
--    This ensures FTS is consistent with the (unchanged) source_file/section columns.
--    Wrap in BEGIN IMMEDIATE / COMMIT for atomicity.

-- 8. Bump version
PRAGMA user_version = 5;
```

**Acceptance criteria:**
- [ ] Fresh bank: `memory_source` table exists, entries has `source_id` column
- [ ] v4→v5 migration: all entries get a valid `source_id` FK
- [ ] v5 bank re-open: migration is a no-op (skipped)
- [ ] FTS still works after migration (source_file/section preserved on entries)
- [ ] `PRAGMA user_version` = 5 after migration

---

### WP2: Write Path — Insert/Update resolves source

**Goal:** Every write to `entries` resolves a `memory_source` row first and sets both `source_id` and the denormalized `source_file`/`section`.

**Files to change:**
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemorySourceStore.cs` — implement `ResolveOrCreateAsync`
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs`
  - `WriteAsync`: resolve source before InsertEntry
  - `AddContentAsync`: resolve source before InsertEntry
  - `ShareAsync`: resolve source during promote
  - `InsertEntry` SQL: add `source_id` column
- `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`
  - `InsertEntry`: add `source_id` to column list
- `src/AiRaccoon.Infrastructure/Sync/SyncService.cs`
  - Merge INSERT: resolve source for incoming sync rows

**TDD — RED tests first:**
- `tests/AiRaccoon.Tests/Unit/storage/SqliteMemorySourceStoreTests.cs` — **NEW**
  - `ResolveOrCreate_NewSource_InsertsAndReturns`
  - `ResolveOrCreate_ExistingSource_ReturnsSameId`
  - `ResolveOrCreate_SameLocatorDifferentSection_DifferentRow`
  - `ResolveOrCreate_FileVsTranscript_DifferentRows`
- `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreTests.cs`
  - `WriteAsync_SetsSourceId_OnEntry` (RED)
  - `WriteAsync_NullSourceFile_SetsManualSource` (RED)
  - `AddContentAsync_SetsSourceId_OnEntry` (RED)

**Acceptance criteria:**
- [ ] Every new entry has `source_id` populated
- [ ] `ResolveOrCreateAsync` is idempotent (same locator+type+section → same ID)
- [ ] NULL source_file maps to `source_type='manual'` with `source_locator=''`
- [ ] All existing tests still pass (source_file/section still populated)

---

### WP3: Chunk Recompute — PARTITION BY source

**Goal:** `RecomputeChunkColumnsForContext` and `RecomputeChunkColumnsBankWide` continue to work. Since `source_file` remains on entries as a denormalized column, no SQL change is needed — but add a validation test.

**Files to change:**
- No SQL changes needed (source_file stays on entries)
- `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreChunkColumnMaintenanceTests.cs`
  - Add test: `RecomputeChunkColumns_AfterSourceNormalization_PartitionsBySourceFile` (RED then GREEN)

**Acceptance criteria:**
- [ ] Chunk recomputation still groups by `(ctx, source_file)` as before
- [ ] No regression in chunk_index/total_chunks correctness

---

### WP4: Read Path — Queries that return SourceFile

**Goal:** Queries that return `source_file AS SourceFile` to C# continue to work. For full source identity (including `source_type`), queries JOIN to `memory_source`.

**Files to change:**
- `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`
  - `SelectSourceByHashAndProject`: JOIN `memory_source` to also return `source_type` and `heading_path`
  - `SelectExtractionCandidates`: JOIN `memory_source` for `source_type`
  - `SelectDeleteRecomputeContext`: unchanged (only needs `source_file` for chunk recompute)
  - `SearchByFilter`: unchanged (source_file from entries is sufficient for display)
  - `VectorSearchByFilter`: unchanged
  - `StructureVectorSearchByFilter`: unchanged
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs`
  - `SourceRow`: add `SourceType` field
  - `ShareAsync`: use `SourceType` for provenance
  - `SelectSourceByHashAndProject` result mapping
- `src/AiRaccoon.Core/Memory/SharedExtraction.cs`
  - `ExtractionCandidateRow`: add `SourceType` field (optional)
- `src/AiRaccoon.Core/Memory/ProvenanceArchetype.cs`
  - `Classify`: optionally use `source_type` to shortcut (e.g., `source_type='transcript'` → `ProvenanceArchetype.Transcript`)

**TDD — RED tests first:**
- `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreTests.cs`
  - `SelectSourceByHashAndProject_ReturnsSourceType` (RED)
  - `SelectExtractionCandidates_IncludesSourceType` (RED)
- `tests/AiRaccoon.Tests/Integration/SourceIdentityTests.cs`
  - Update existing tests to verify `source_id` is populated alongside `source_file`

**Acceptance criteria:**
- [ ] All existing query consumers still get `SourceFile` (backwards compatible)
- [ ] `SelectSourceByHashAndProject` additionally returns source identity
- [ ] FTS search still returns correct results (source_file backed by denormalized column)
- [ ] SourcePathQuery still works (FTS column filter `{source_file section}` unchanged)

---

### WP5: Sync Path

**Goal:** Sync merge populates `source_id` on merged rows.

**Files to change:**
- `src/AiRaccoon.Infrastructure/Sync/SyncService.cs`
  - Merge INSERT: resolve `memory_source` for each incoming row's `source_file`/`section`
  - Reindex: unchanged (already works with entries columns)
- `tests/AiRaccoon.Tests/Unit/sync/SyncServiceTests.cs`
  - `MergeAsync_SetsSourceId_OnMergedEntries` (RED)
  - `MergeAsync_PreservesSourceFile_Section_ForFTS` (RED)

**Acceptance criteria:**
- [ ] Synced entries get `source_id` set
- [ ] FTS triggers still fire correctly (source_file/section populated from sync)
- [ ] Tombstone round-trip doesn't lose source identity

---

### WP6: Ingestion + Tools

**Goal:** FileIngestor, MemoryTools, PromotionTools pass through source info correctly.

**Files to change:**
- `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs`
  - `source_file` already set; ensure `source_id` resolves via WriteAsync
  - No direct change needed if WP2's WriteAsync handles resolution
- `src/AiRaccoon/Tools/MemoryTools.cs`
  - `topSourceFiles` in search quality: reads from `MemorySearchResult.SourceFile` (unchanged)
- `src/AiRaccoon/Tools/PromotionTools.cs`
  - Reads `SourceFile` from promotion queue row (unchanged — queue has own column)
- `src/AiRaccoon.Infrastructure/Sqlite/SqlitePromotionQueueStore.cs`
  - `source_file` on `promotion_queue` stays as-is (independent table)
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs`
  - `top_source_files` stays as-is (JSON array of file paths, independent)
- `src/AiRaccoon/Setup/Cli/CliCommandTree.cs`
  - `extract.exclude.prefixes` matches on `source_file` from ExtractionCandidateRow (unchanged)

**TDD — RED tests first:**
- `tests/AiRaccoon.Tests/Integration/WatchIntegrationTests.cs`
  - `FileIngest_CreatesSourceId_ForIngestedChunks` (RED)

**Acceptance criteria:**
- [ ] FileIngestor writes produce entries with valid `source_id`
- [ ] CLI exclude prefix filtering still works against `source_file`
- [ ] Promotion queue is unaffected (separate table)

---

### WP7: Integration Tests + Cleanup

**Goal:** End-to-end validation, remove any temporary compatibility shims.

**Files to change:**
- `tests/AiRaccoon.Tests/Integration/SqliteMemoryStoreIntegrationTests.cs`
  - `WriteSearchRoundtrip_IncludesSourceIdentity` (RED)
  - `ShareExtract_PreservesSourceIdentity` (RED)
  - `SyncMerge_PreservesSourceIdentity` (RED)
- `tests/AiRaccoon.Tests/Integration/RetrievalBaselineTests.cs`
  - Verify FTS bm25 still weights source_file/section correctly
  - Verify VectorSearch still returns SourceFile
- `tests/AiRaccoon.Tests/Integration/SyncReindexStructureClearTests.cs`
  - Verify reindex preserves source_id
- `tests/AiRaccoon.Tests/BDD/FileWatcherSteps.cs`
  - Update BDD steps if they assert on source_file
- `tests/AiRaccoon.Tests/TestData.cs`
  - Update test fixtures to include source_id expectations

**Acceptance criteria:**
- [ ] Full test suite passes (all unit + integration + BDD)
- [ ] No test relies on `source_id` being NULL
- [ ] FTS search quality is unchanged (regression baseline)

---

## 4. Parallelism Map

```
         ┌─────────────┐
         │    WP0      │  Domain model + interface
         │  (serial)   │
         └──────┬──────┘
                │
         ┌──────▼──────┐
         │    WP1      │  Schema + migration
         │  (serial)   │
         └──────┬──────┘
                │
         ┌──────┼──────┐
         │             │
    ┌────▼───┐    ┌───▼────┐
    │  WP2   │    │  WP3   │  Write path / Chunk recompute
    │ (par.) │    │ (par.) │  ← can run in parallel
    └────┬───┘    └───┬────┘
         │            │
    ┌────▼───┐        │
    │  WP5   │        │  Sync (depends on WP2)
    │ (ser.) │        │
    └────┬───┘        │
         │            │
         └─────┬──────┘
               │
        ┌──────▼──────┐
        │    WP4      │  Read path + queries
        │  (serial)   │
        └──────┬──────┘
               │
        ┌──────▼──────┐
        │    WP6      │  Ingestion + tools
        │  (serial)   │
        └──────┬──────┘
               │
        ┌──────▼──────┐
        │    WP7      │  Integration + cleanup
        │  (serial)   │
        └─────────────┘
```

**WP2 and WP3** can run in parallel after WP1. **WP5 depends on WP2** (needs `ResolveOrCreateAsync` for sync merge), so it serializes after WP2.

---

## 5. Risk Assessment

### Critical risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| 1 | **FTS triggers break if source_file/section removed** | N/A (not removing) | Catastrophic | Decision: keep denormalized columns. Triggers untouched. |
| 2 | **Migration corrupts FTS index** | Low | High | MigrateToV5 rebuilds FTS in transaction (same pattern as v1). Row count check after rebuild. |
| 3 | **FK constraint blocks insert** | Medium | Medium | source_id is NULLable during transition. WriteAsync always resolves before insert. |
| 4 | **Sync merge hits FK violation** | Low | Medium | SyncService resolves source for each merged row. OR IGNORE catches conflicts. |
| 5 | **Chunk recompute partitions wrong** | Low | Medium | source_file stays on entries, PARTITION BY unchanged. Validation test in WP3. |
| 6 | **Performance regression from JOIN** | Very Low | Low | Spike already showed JOIN is faster than CASE/WHEN. source_id indexed. |
| 7 | **Migration performance on large banks** | Low | Medium | Backfill is one UPDATE with correlated subquery. On 14k rows: ~63ms. FTS rebuild is the expensive part but is one-time. Wrap in BEGIN IMMEDIATE / COMMIT. |

### Non-critical risks

| # | Risk | Mitigation |
|---|------|------------|
| 7 | `ProvenanceArchetypeClassifier` still takes `sourceFile` string | Works unchanged; can optionally short-circuit on `source_type` later |
| 8 | `promotion_queue.source_file` stays independent | Correct — queue is a separate concern. No normalization needed there. |
| 9 | `search_quality.top_source_files` stays as JSON array | Correct — it's a metric log, not a relational reference |

---

## 6. File Change Summary

| File | WP | Change Type |
|------|----|-------------|
| `src/AiRaccoon.Core/Memory/MemorySource.cs` | 0 | NEW |
| `src/AiRaccoon.Core/Memory/MemorySourceId.cs` | 0 | NEW |
| `src/AiRaccoon.Core/Memory/IMemorySourceStore.cs` | 0 | NEW |
| `src/AiRaccoon.Core/Memory/MemoryWriteRequest.cs` | 0 | MODIFY (docs) |
| `src/AiRaccoon.Core/Memory/SharedExtraction.cs` | 4 | MODIFY |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` | 1 | MODIFY |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` | 2, 4 | MODIFY |
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemorySourceStore.cs` | 1, 2 | NEW |
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` | 2, 4 | MODIFY |
| `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` | 5 | MODIFY |
| `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs` | 6 | VERIFY |
| `src/AiRaccoon/Tools/MemoryTools.cs` | 6 | VERIFY |
| `src/AiRaccoon/Tools/PromotionTools.cs` | 6 | VERIFY |
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs` | 6 | VERIFY |
| `src/AiRaccoon.Infrastructure/Sqlite/SqlitePromotionQueueStore.cs` | 6 | VERIFY |
| `src/AiRaccoon.Core/Memory/ProvenanceArchetype.cs` | 4 | VERIFY |
| `tests/AiRaccoon.Tests/Unit/Memory/MemorySourceTests.cs` | 0 | NEW |
| `tests/AiRaccoon.Tests/Unit/storage/SqliteMemorySourceStoreTests.cs` | 2 | NEW |
| `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreTests.cs` | 2, 4 | MODIFY |
| `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreChunkColumnMaintenanceTests.cs` | 3 | MODIFY |
| `tests/AiRaccoon.Tests/Unit/sync/SyncServiceTests.cs` | 5 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/MemorySchemaVersionTests.cs` | 1 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/SqliteMemoryStoreIntegrationTests.cs` | 7 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/RetrievalBaselineTests.cs` | 7 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/SourceIdentityTests.cs` | 4 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/SyncReindexStructureClearTests.cs` | 7 | MODIFY |
| `tests/AiRaccoon.Tests/Integration/WatchIntegrationTests.cs` | 6 | MODIFY |
| `tests/AiRaccoon.Tests/BDD/FileWatcherSteps.cs` | 7 | MODIFY |
| `tests/AiRaccoon.Tests/TestData.cs` | 7 | MODIFY |

**Total: 7 NEW files, 22 MODIFY/VERIFY files**

---

## 7. TDD Sequence per Work Package

Each work package follows the RED → GREEN → REFACTOR cycle:

1. **Write the failing test** (RED) — describes the expected behavior
2. **Implement the minimum code** to make it pass (GREEN)
3. **Refactor** — clean up while keeping tests green

For WP1 (migration), the test sequence is:
1. RED: `EnsureAsync_FromV4_CreatesMemorySourceTable` — assert table exists after migration
2. GREEN: Add `memory_source` DDL + `MigrateToV5Async` stub
3. RED: `EnsureAsync_FromV4_EntriesHaveSourceId` — assert source_id populated
4. GREEN: Implement backfill logic
5. RED: `EnsureAsync_V5Bank_SkipsMigration` — assert no-op on v5
6. GREEN: Version gate logic

---

## 8. Acceptance Gate (WP7 completion)

- [ ] `dotnet test` passes 100%
- [ ] `PRAGMA user_version = 5` on fresh bank
- [ ] Migration from v4 bank produces valid v5 with all entries having source_id
- [ ] FTS search returns identical results before/after normalization
- [ ] Chunk recompute produces identical chunk_index/total_chunks
- [ ] Sync merge preserves source identity
- [ ] No NULL source_id on any committed entry after migration

---

## 9. Pre-merge Extension (e: from owner)

After WP7 and before merging:

1. **Version bump (local-only):** 1.6.6 — do NOT commit the version bump. CI has 1.7.0 queued. Use `dotnet build -p:Version=1.6.6` or equivalent for local testing only.
2. **Manual tool sweep:** test all MCP tools against a live `ai-raccoon serve` on the normalized schema
3. **New tool verification:** test any memory_source-related tool that was added
4. **Prometheus grading integration:** verify that `source_type='transcript'` filtering in the grading script correctly handles hermes transcripts (grade from memory bank content, not disk files)
