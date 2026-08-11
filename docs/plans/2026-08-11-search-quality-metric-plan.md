# Plan: Integrated search-quality metric

**Date:** 2026-08-11
**Based on:** `docs/work/2026-08-11-auto-grading.md` research record + expert reviews (architect + engineer)
**Goal:** First iteration of a universal memory_search quality metric: follow-through rate + coverage + usefulness grade, with correlation-id plumbing for future LLM-as-judge auto-grading.

## Design principle: signals are collected together

The quality record is a single entity that accumulates multiple signals over its
lifetime: search (query + results) creates it, follow-through (file reads) and
grade (usefulness) update it. The correlation-id is the thread. Every signal
flows through `ISearchQualityService` — no signal is collected outside it.
The search tool generates the correlation-id and passes it to the agent in the
response envelope; the agent passes it back on follow-through and grade calls.
All three signals land in the same `search_quality` row.

## Background

The current grading system (`memory-quality.jsonl`) captures only 4.6% of searches (20 graded out of 438 ops). The grade log itself misses 62% of hermes searches (instrumentation gap). The 21 existing grades avg 4.29 with selection bias. **The JSONL hook was REMOVED 2026-08-11 (task mem-cleanup)** — the `memory-quality.jsonl`/`pending.json` writers and `AI_BADGER_MEMORY_GRADE` are gone from ai-badger and the ai-raccoon provider's `memory-operations.jsonl` is gone too; the pre-migration file (167+ lines, 20 graded) survives only as historical data on the machine. This plan replaces the (now absent) JSONL hook with a server-side quality table that captures 100% of searches and adds follow-through measurement (did the agent use the result?).

## Architecture decisions (from expert review)

1. **Correlation-id at tool layer, not in SearchQuery.** `SearchQuery` is a Core retrieval parameter; correlation-id is an observability concern. Generate a GUID in `MemoryTools.Search()`, pass to `ISearchQualityService.RecordSearchAsync`, surface in `ApiEnvelope.Meta.CorrelationId`.

2. **ISearchQualityService is a Core port** (same pattern as `IMemoryStore`, `IWorkspaceStore`, `IPromotionQueue`). Implementation in Infrastructure via Dapper. Does NOT grow `IMemoryStore` — called alongside search, not inside it.

3. **Additive DDL, no version bump.** New `search_quality` table in `MemorySchema.Ddl` (CREATE TABLE IF NOT EXISTS). Per ADR-0023/0025 precedent, new tables need no `MigrateAsync` or version bump.

4. **Quality tools in a new QualityTools.cs class**, not appended to MemoryTools. MemoryTools maps 1:1 to IMemoryStore; quality tools map to ISearchQualityService.

5. **Fire-and-forget for RecordSearchAsync.** Must not block or fail the search response. Try/catch at Warning level.

6. **Follow-through fallback.** Allow `memory_record_followthrough` to accept `(correlationId, filePath)` OR `(query, filePath)` with fuzzy match on recent searches by time window, for when the agent forgets to stash the correlation-id.

## Work packages

### Wave 1 (parallel — no dependencies between WP1 and WP2)

#### WP1: search_quality table (additive DDL)

**Effort:** S (0.5d) | **Risk:** Low

Create table in `MemorySchema.Ddl`:

```sql
CREATE TABLE IF NOT EXISTS search_quality (
    id INTEGER PRIMARY KEY,
    correlation_id TEXT NOT NULL UNIQUE,
    query TEXT NOT NULL,
    scope TEXT,
    project_id TEXT,
    session_id TEXT,
    result_count INTEGER,
    top_source_files TEXT,      -- JSON array of SourceFile paths from top results
    follow_through_count INTEGER DEFAULT 0,
    follow_through_files TEXT,  -- JSON array of files that were read after search
    usefulness_grade INTEGER CHECK(usefulness_grade BETWEEN 1 AND 5),
    grade_note TEXT,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sq_project_time ON search_quality(project_id, created_at);
-- correlation_id UNIQUE constraint creates implicit index; no explicit index needed
```

**TDD:**
- RED: test opening a fresh bank, `SELECT name FROM sqlite_master WHERE type='table' AND name='search_quality'` returns 1 row
- GREEN: add DDL string
- Existing-bank test: DROP TABLE IF EXISTS, reopen (EnsureAsync), verify recreated

**Files touched:**
- `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` — add to `Ddl` const
- `tests/AiRaccoon.Tests/Integration/SearchQualitySchemaTests.cs` — new file

#### WP2: ISearchQualityService interface + SearchQualityMetrics

**Effort:** S (0.5d) | **Risk:** Low

Interface in Core:

```csharp
namespace AiRaccoon.Core.SearchQuality;

public interface ISearchQualityService
{
    Task RecordSearchAsync(
        string correlationId, string query, string? scope, string? projectId,
        string? sessionId,
        int resultCount, IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default);

    Task RecordFollowThroughAsync(
        string correlationId, string filePath,
        CancellationToken ct = default);

    Task RecordGradeAsync(
        string projectId, string correlationId, int grade, string? note,
        CancellationToken ct = default);

    Task<SearchQualityMetrics> GetMetricsAsync(
        string? projectId, DateTimeOffset from,
        CancellationToken ct = default);
}

public sealed record SearchQualityMetrics(
    int TotalSearches,
    int FollowThroughSearches,
    int GradedSearches,
    double AverageGrade,
    double FollowThroughRate,
    double Coverage,
    int SearchesPerDay);
```

**TDD:** Compile-only — no behavior tests until WP3 provides the implementation.

**Files touched:**
- `src/AiRaccoon.Core/SearchQuality/ISearchQualityService.cs` — new file
- `src/AiRaccoon.Core/SearchQuality/SearchQualityMetrics.cs` — new file

### Wave 2 (depends on WP1 + WP2)

#### WP3: SqliteSearchQualityService + DI registration

**Effort:** M (1d) | **Risk:** Medium

Implementation in Infrastructure using Dapper over `search_quality` table. Register as singleton in `Dependencies.cs`:

```csharp
services.AddSingleton<ISearchQualityService>(sp =>
    new SqliteSearchQualityService(sp.GetRequiredService<SqliteConnectionFactory>()));
```

**TDD:**
- RED: tests calling `RecordSearchAsync` → `GetMetricsAsync` returns count=1
- GREEN: Dapper INSERT/SELECT implementation
- Follow-through: `RecordFollowThroughAsync` → updates row, increments count
- Grade: `RecordGradeAsync` → updates row with grade
- Metrics: `GetMetricsAsync` → aggregates over time window

**Files touched:**
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs` — new file (~100 lines Dapper)
- `src/AiRaccoon/Setup/Dependencies.cs` — DI registration
- `tests/AiRaccoon.Tests/Integration/SearchQualityServiceTests.cs` — new file (6-8 tests)

### Wave 3 (depends on WP3; WP4 + WP5 + WP6 can run in parallel)

#### WP4: Wire correlation-id into search tool + RecordSearchAsync

**Effort:** S (0.5d) | **Risk:** Low

In `MemoryTools.Search()`:
1. Generate `correlationId = Guid.NewGuid().ToString("N")`
2. After `store.SearchAsync(query)`, call `await quality.RecordSearchAsync(correlationId, query, scope, projectId, results.Count, topSourceFiles, ct)` (fire-and-forget with try/catch at Warning)
3. Add `CorrelationId` as optional property on `PromotionMeta` (init-only, null for non-search tools)

**TDD:**
- RED: test asserting `envelope.Meta.CorrelationId` is non-null after search
- GREEN: generate GUID, set on meta, call RecordSearchAsync

**Pitfalls:**
- MemoryTools constructor gains ISearchQualityService parameter — ~3 test files need null! or mock
- RecordSearchAsync must be fire-and-forget (never block/fail the search response)

**Files touched:**
- `src/AiRaccoon/Tools/MemoryTools.cs` — inject service, add call in Search()
- `src/AiRaccoon.Core/Memory/MemoryEntryResult.cs` or `ApiEnvelope.cs` — add CorrelationId to PromotionMeta
- `tests/AiRaccoon.Tests/Unit/MemoryToolsTests.cs` — update constructor + add test

#### WP5: QualityTools.cs with memory_record_followthrough

**Effort:** M (1d) | **Risk:** Medium

New tool class `QualityTools.cs` with:
- `memory_record_followthrough(projectId, correlationId, filePath)` — records follow-through event
- Fallback: accept `(projectId, query, filePath)` and fuzzy-match recent searches by time window

**Tool inventory update:**
- Current count: 23. New: 24.
- `ToolInventoryTests`: update count, add `tools.ShouldContain("memory_record_followthrough")`
- TN_* const: `TN_RecordFollowThrough`
- Access: `AccessRequirement.Write` (data write, non-destructive)
- `ToolExecutionActivity` wrapper (mandatory)
- `.WithTools<QualityTools>()` in `McpServerSetup.cs`

**TDD:**
- RED: behavior test — call tool, verify row updated with follow-through
- Inventory test — count 24

**Files touched:**
- `src/AiRaccoon/Tools/QualityTools.cs` — new file
- `src/AiRaccoon/Setup/McpServerSetup.cs` — register tools (BOTH host creation methods)
- `src/AiRaccoon/README.md` — update Tools heading count
- `tests/AiRaccoon.Tests/Unit/QualityToolsTests.cs` — new file
- `tests/AiRaccoon.Tests/Unit/ToolInventoryTests.cs` — count + name update
- `tests/AiRaccoon.Tests/Unit/McpServerSetupHostTests.cs` — tool count update
- `tests/AiRaccoon.Tests/Unit/ToolTelemetryCoverageTests.cs` — new tool coverage

#### WP6: Wire RecordGradeAsync into grade flow

**Effort:** S-M (0.5–1d) | **Risk:** Low

Option A (recommended): New `memory_record_grade` MCP tool that takes `(projectId, correlationId, grade, note?)`
  - TN_* const: `TN_RecordGrade`
  - Access: `AccessRequirement.Write` and calls `ISearchQualityService.RecordGradeAsync`. The ai-badger plugin's `memory_grade.py` calls this tool after grading. The correlation-id travels in the search response envelope, so the hook captures it automatically.

Option B: Direct Python → SQLite write. Simpler for the hook but couples the Python plugin to the C# schema.

**TDD:**
- RED: call tool with correlationId + grade, verify row updated
- GREEN: tool + service wiring

**Files touched:**
- `src/AiRaccoon/Tools/QualityTools.cs` — add grade tool
- `~/.hermes/plugins/ai-badger/memory_grade.py` — add MCP call after grading
- `tests/AiRaccoon.Tests/Unit/QualityToolsTests.cs` — add grade tests

## Acceptance criteria

1. Every `memory_search` call creates a row in `search_quality` with correlation-id, query, scope, project_id, result_count, top_source_files, and created_at
2. `memory_record_followthrough` tool updates the row with follow-through count and files
3. `memory_record_grade` tool updates the row with usefulness grade
4. `GetMetricsAsync` returns: total searches, follow-through searches, graded searches, avg grade, follow-through rate, coverage, searches-per-day
5. Search response envelope includes `CorrelationId` in Meta
6. IMemoryStore is NOT modified — zero fake-store ripple
7. All existing tests pass (ToolInventoryTests count updated to 25 for Option A: 23 baseline + memory_record_followthrough + memory_record_grade)
8. `src/AiRaccoon/README.md` Tools heading updated to match new count
9. `.WithTools<QualityTools>()` registered in BOTH `CreateAppHost` and `CreateWebHost` in McpServerSetup.cs
10. `McpServerSetupHostTests` and `ToolTelemetryCoverageTests` updated for new tools

## Deferred (Phase 2)

- **Agent-side follow-through hook**: ai-badger plugin hooks `read_file` on hermes, auto-detects follow-through (60s window, no explicit tool call)
- **LLM-as-judge auto-grading**: ARES/PPI calibration on human grades, needs a generative model
- **nDCG on live queries**: needs per-query relevance sets, which the quality table + human grades will provide
- **Backfill**: correlate existing `memory-quality.jsonl` grades with quality table via query+timestamp match — possible only from the pre-removal file if it is still on the machine (the writer was removed 2026-08-11)

## Effort summary

| WP | Effort | Risk | Wave |
|---|---|---|---|
| 1. search_quality table | S (0.5d) | Low | 1 |
| 2. ISearchQualityService interface | S (0.5d) | Low | 1 |
| 3. SqliteSearchQualityService | M (1d) | Medium | 2 |
| 4. Wire correlation-id + RecordSearch | S (0.5d) | Low | 3 |
| 5. QualityTools (followthrough) | M (1d) | Medium | 3 |
| 6. Grade wiring | S-M (0.5–1d) | Low | 3 |
| **Total** | **4–5d** | | |
