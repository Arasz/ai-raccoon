# Promotion Propose-Tier: persisted waiting queue + fair-share capacity + response envelope

> **Status:** DRAFT (2026-08-06, design pinned with the owner). Implements the `e:` extension to the
> memory-first-gate task: (1) the propose tier becomes a real persisted queue with fair-share
> capacity, (2) every MCP response carries a `meta` envelope telling the agent what is waiting.

**Goal:** `memory_share_extract propose` persists ranked candidates into a per-project
**propose tier** (the waiting-for-promotion queue). `promote`/`autoPromote` consume **from that
queue**, never from a fresh re-extraction. The queue is capacity-managed: a total cap is split
into per-project reservations (cap ÷ project count); projects may borrow each other's unused
space; when the queue is at cap, the lowest-scored item of the project with the greatest item
count is evicted. The shared tier stays exactly as it is — curated, sweep-exempt, never swept
(owner-confirmed `f:`: capacity/eviction applies only to the waiting queue). Every MCP tool
response is enveloped in a common schema whose `meta` carries `waitingPromotionsCount` and
`promotionsWaitTime` so the agent sees that something is waiting for its review.

**Owner-pinned decisions (2026-08-06):**
1. The waiting tier **is** the propose tier: `propose` persists candidates; `promote` promotes
   FROM the queue (agent reviews what's waiting, then promotes).
2. Shared tier semantics unchanged — never swept, no capacity on it.
3. Eviction rule (uniform): at total cap, evict the **lowest-scored item** of the project with
   the **greatest item count** (if that project is the inserter, so be it). Tie-break: oldest
   `created_at` first.

---

## Phase 1 — Propose-tier persistence: schema + store (TDD)

### Task 1.1: Failing schema tests
**Files:** `tests/AiRaccoon.Tests/Unit/Infrastructure/...` (follow existing store-test layout).

Assert, against a fresh in-memory bank:
- `promotion_queue` exists after open with columns `id, project_id, hash, path, value,
  source_file, score, reasons, created_at, updated_at`.
- UNIQUE(project_id, hash) enforced; index on (project_id), (score).
- An existing bank (migrated, entries present) opens clean with the new table (idempotent DDL).
- Queue rows are **not** searchable via `memory_search` and do not appear in `memory_stats`
  (separate table — stats/contexts unchanged).

Run: `dotnet test --filter "FullyQualifiedName~ProposeTier"` → FAIL (table missing).

### Task 1.2: Schema + store methods
**Files:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` (DDL, `CREATE TABLE IF NOT EXISTS
promotion_queue` + indexes — the established idempotent pattern), `IMemoryStore.cs`,
`SqliteMemoryStore.cs`, new records in `AiRaccoon.Core/Memory/`.

Store surface (thin, SQL-shaped, mirrors the existing style):
- `UpsertQueueCandidatesAsync(projectId, rows)` — upsert by (project_id, hash): insert keeps
  first `created_at`, refresh `score/reasons/value/path/source_file/updated_at` on re-propose.
- `ListQueueCandidatesAsync(projectId?)` — all rows, or one project's, ordered by score DESC,
  created_at ASC (stable review order).
- `DiscardQueueCandidatesAsync(projectId, hash?)` — one row or the whole project's queue.
- `GetQueueStatsAsync()` — `{totalCount, avgWaitSeconds, perProject: {project: count}}`
  (avg wait = avg(now − created_at) over queued rows; the meta source).
- `EvictQueueVictimAsync()` — the eviction SQL: lowest `score`, oldest `created_at`, of the
  project with the greatest item count. Returns the removed row (for logging/response).

Run store tests → PASS. Commit: `feat(propose-tier): queue table + store surface`.

### Task 1.3: Failing capacity-policy tests (pure logic)
**Files:** new `src/AiRaccoon.Core/Memory/PromotionCapacityPolicy.cs` + tests.

Pure functions (no IO; the store feeds counts, the policy decides):
- `ReservationFor(totalCap, projectCount)` → `totalCap / projectCount` (floor; ≥1 when
  projectCount ≤ totalCap, else 0 → no reservation, cap rules).
- `NeedsEviction(totalCount, totalCap)` → totalCount > totalCap.
- `EvictionTarget(perProjectCounts)` → the project id with the greatest count (tie: any — the
  victim row query breaks ties by created_at). Deterministic, documented.
- `CapacityInfo(totalCap, projectCount, perProjectCounts)` → per-project `{reserved, used,
  borrowing}` for responses/meta (used − reserved > 0 ⇒ borrowing).

Run → FAIL (module missing), implement, GREEN. Commit.

---

## Phase 2 — Extraction integration: propose persists, promote consumes (TDD)

### Task 2.1: Failing service tests
**Files:** extend the extraction service tests (`SharedExtractionService` tests + new
`PromotionQueueService` tests).

Assert (integration-level, real store, in-memory bank):
- `propose` upserts candidates into the queue; re-propose refreshes score and does **not**
  duplicate; first `created_at` survives re-propose.
- At cap: inserting a new fact evicts the lowest-scored item of the greatest-count project;
  eviction happens **after** upsert, looping until total ≤ cap.
- A project below its reservation that had space borrowed: eviction never touches its rows
  while another project is over its reservation and holds the max count (the uniform rule's
  observable consequence — the borrower loses first because it holds the max count).
- `promote` reads the top-N queue rows for the requested project ids (limit applies), calls
  `ShareAsync` for each, discards them from the queue, returns the promoted hashes + remaining
  queue counts. Promoting a hash that is already in the shared tier (dedup) skips + drains.
- `discard` removes a row / a project's rows.
- Queue is untouched by `SweepService` (sweep targets the committed context only; no shared
  context, no queue context — pinned by a regression assertion in the sweep tests).

Run → FAIL. Implement:
- `PromotionQueueService` (Core): orchestrates upsert → capacity check → eviction loop →
  promote-from-queue → discard; pure policy in `PromotionCapacityPolicy`, store calls thin.
- `SharedExtractionService.Run` unchanged (still the scorer); propose mode now **persists** its
  scored candidates through the service instead of returning ephemeral ones.
- `MemoryTools.ShareExtract` rewires: `propose` → persist + return queued candidates;
  `promote`/`autoPromote` → promote from queue.
- `ExtractionHostedService` propose mode → persists to the queue (the tier fills by itself on
  the schedule; the meta then tells the agent).

Run → GREEN. Commit: `feat(propose-tier): propose persists, promote consumes from the queue`.

### Task 2.2: Queue management tools + config (TDD)
- New MCP tools: `memory_promotion_list` (projectId?, limit?) → waiting queue rows (score,
  reasons, proposed_at — the agent's review surface) and `memory_promotion_discard`
  (projectId, hash?) → removed count. Same access rules as share_extract (read/write).
- Config key: `extract.queue-capacity.global` (default 1000, guarded int-parse ≥ 1) —
  `ExtractionConfigKeys` + CLI `config` surface per the existing pattern.
- Tests: tool-level (list/discard shapes, access gating) + config parse tests.

Run → GREEN. Commit.

---

## Phase 3 — Response envelope with waiting meta (TDD)

### Task 3.1: Failing envelope tests
**Files:** new `ApiEnvelope` record tests + per-tool response-shape tests (the mcp tool-surface
test suite asserts result shapes — extend it).

Assert: every memory/watch tool returns `{ data: <previous shape>, meta: { waitingPromotionsCount,
promotionsWaitTimeSeconds, waitingByProject? } }`; meta reflects a seeded queue (count + avg
wait); zero queue ⇒ `0` / `null`, still present (0 is informative, never absent).

### Task 3.2: Envelope implementation
- `AiRaccoon.Core/ApiEnvelope.cs`: `ApiEnvelope<TData>(TData? Data, ResponseMeta Meta,
  OperationStatus Result)` — record (the MCP output schema derives from it).
  `ResponseMeta(int WaitingPromotionsCount, double? PromotionsWaitTimeSeconds,
  `IReadOnlyDictionary<string, int>? WaitingByProject)`; `OperationStatus(int Code)` — HTTP
  status code — with optional `Message` as an init property and
  `public static readonly OperationStatus Ok = new(200) { Message = "ok" }` (success
  sentinel — every success response carries it; the envelope never omits Result).
  Domain-outcome mapping: 200 ok, 404 not-found, 409 already-shared/duplicate, 422
  cap-reached/eviction side-effect, 400 bad request shape. Convention: required members
  positional, optional members properties.
- **Two-tier errors (owner pin):** `Result` carries domain outcomes in-band (not-found,
  already-shared, cap-reached, eviction side-effects) — `OperationStatus.Ok` = success.
  Protocol errors (invalid params, access denied, confirm-gates) stay `McpException` — the
  SDK's JSON-RPC channel, already consumed as tool errors.
- Every tool in `MemoryTools.cs` + `WatchTools.cs` returns `ApiEnvelope<...>`; meta computed
  from `GetQueueStatsAsync` (one cheap indexed query per call).
- **Breaking change** for clients: version bump 1.0.11 → 1.1.0 + changelog entry (releases are
  traceable); the manual fresh-install gate is the finish test.

Run → GREEN. Commit: `feat(api): envelope all responses with waiting-promotion meta`.

---

## Phase 4 — Release + verification

- Full gates: `dotnet test` (no skips beyond the 4 pre-existing spec skips), `dotnet build` 0
  warnings, pytest scripts suite, version bump + changelog, manual fresh-install test
  (`scripts/manual-fresh-install-test.py`).
- One PR in Arasz/ai-raccoon (one task = one PR; never push to main).
- Live verification evidence:
  - propose → queue rows persist; `memory_promotion_list` shows them; meta shows
    `waitingPromotionsCount` / `promotionsWaitTime` on an unrelated call (e.g. memory_search).
  - promote → drains the queue into the shared tier; shared tier untouched by eviction.
  - Eviction: seed a bank near cap, propose from a borrower, observe the victim row (greatest
    count project's lowest score) removed.
  - docs/work outcome note appended (the `e:` extension record).

## Files likely to change (summary)

- `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` — queue table + indexes (idempotent DDL)
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` + `IMemoryStore.cs` — 5 store methods
- `src/AiRaccoon.Core/Memory/PromotionCapacityPolicy.cs` (new, pure) +
  `PromotionQueueService.cs` (new, orchestration) + queue records
- `src/AiRaccoon.Core/Memory/ExtractionConfigKeys.cs` — `extract.queue-capacity.global`
- `src/AiRaccoon/Tools/MemoryTools.cs` — ShareExtract rewiring + 2 new tools + envelope
- `src/AiRaccoon/Tools/WatchTools.cs` — envelope
- `src/AiRaccoon.Core/ApiEnvelope.cs` (new)
- `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` — persist on propose
- Tests: store tests, capacity-policy tests, queue-service tests, tool-surface tests, sweep
  regression (queue untouched), config tests
- `VERSION`/changelog/docs (1.1.0), docs/work outcome note

## Risks / open questions

1. **Reservation basis** = `GetProjectIdsAsync` (projects with committed rows — the extraction
   universe). A project with no committed rows gets no reservation (nothing to propose anyway).
   Config override (`extract.queue-projects`) is out of scope unless it bites.
2. **Eviction fairness** — the uniform rule can evict the inserter itself (it is the max-count
   project). Owner confirmed the rule; the 3-strike-style tuning knob is the capacity key only.
3. **`promotionsWaitTime`** = average age of queued rows (seconds). Per-project breakdown in
   `waitingByProject` stays optional in the meta (cheap, one grouped query).
4. **Queue rows are ephemeral by design** — no TTL on them yet (a stale-queue sweep is a
   follow-up if the wait-time grows unbounded; the capacity cap bounds it anyway).
5. **Envelope breakage** — every client reading raw result fields breaks; that is the point
   (schema-first). Bump + changelog + fresh-install gate cover it.
6. **Extraction hosted service with promote mode** — promote-from-queue changes the background
   pass from "share top fresh candidates" to "share top queued candidates"; autoPromote/confirm
   semantics unchanged.

---

## Architecture (software MoE, 2026-08-06 — architect + engineer reports, read-only)

Two experts verified the design against the code. Their findings correct the draft in four places:

1. **The queue gets its own port** `IPromotionQueueStore`/`SqlitePromotionQueueStore` (Infrastructure), NOT 5 new `IMemoryStore` methods: `IMemoryStore` has two implementations (`SqliteMemoryStore` + `MemoryExtensionHost` decorator in Core) and 15 test fakes — growing it breaks 16 types at compile time. `IWorkspaceStore` is the exact precedent.
2. **`PromotionQueueService` lives in Infrastructure** (not Core): Core has no `ILogger` dependency (csproj = FluentValidation/JetBrains.Annotations/CommunityToolkit.Diagnostics) and the service must log evictions. Core keeps only the `IPromotionQueue` port. `SweepService` is the established shape.
3. **`EvictVictimAsync(projectId)`** — the policy picks the victim project, the store executes "lowest score, oldest created_at" within it.
4. **Metrics need a Core port** `IPromotionQueueMetrics`: the metrics classes live in the server project (`Observability/`), which Core/Infrastructure cannot reference; the port keeps the service testable with a recording fake.

### Components

| Component | Layer | Deps | Role |
|---|---|---|---|
| `IPromotionQueue` | Core port | — | Propose/Promote/Discard/List/GetMeta |
| `IPromotionQueueStore` | Core port | — | Upsert/List/Discard/GetStats/EvictVictim(projectId) |
| `IEvictionPolicy` | Core port | — | `EvictionTarget(perProjectCounts)` |
| `UniformCountEvictionPolicy` | Core pure | none | greatest-count project; tie → ordinal-smallest id |
| `PromotionCapacityPolicy` | Core static pure | none | ReservationFor/NeedsEviction/CapacityInfo |
| `IPromotionQueueMetrics` | Core port | — | RecordQueued/RecordEviction/RecordPromoted/RecordDiscarded/RecordUtilization |
| Queue records | Core | — | PromotionQueueRow, QueueCandidate, PromotionQueueStats, ProposeOutcome, PromoteOutcome, QueueMeta, EvictedRow, PromotionCapacityInfo |
| `ApiEnvelope<TData>`/`ResponseMeta`/`OperationStatus` | Core records | — | SDK schema source |
| `SqlitePromotionQueueStore` | Infrastructure/Sqlite | SqliteConnectionFactory, TimeProvider | repository; SQL in new internal `PromotionQueueSql` |
| `PromotionQueueService` | Infrastructure/Promotion | IPromotionQueueStore, IMemoryStore (ShareAsync+dedup), IEvictionPolicy, IPromotionQueueMetrics, ILogger, TimeProvider | orchestrator; nested static partial `Log` (EventIds 600+) |
| `PromotionQueueMetrics` | Server/Observability | Meter "AiRaccoon.PromotionQueue" | UpDownCounter queue_queued; Counter evictions_total/promoted_total/discarded_total; Histogram evicted_score/wait_seconds; Gauge capacity_utilization |

### Refactor (before/with the feature)

- **MemoryTools.cs (715 lines) → six domain files** (WatchTools precedent — one domain per file, own Tn* consts, own result records): `MemoryTools` (9 core tools), `ShareTools` (share/share_extract), `WorkspaceTools` (4), `SweepTools`, `SyncTools`, new `PromotionTools` (list/discard). Each < 400 lines. `ToolInventoryTests` becomes an assembly-wide scan over the Tools namespace asserting the full 22-tool set (the split's safety net). Two `WithTools<...>` sites in `McpServerSetup.cs`.
- **SqliteMemoryStore (1214 lines) stays bounded** — the queue's methods go in the new store component, never `IMemoryStore`.

### Envelope

Aggregation at the tool boundary (decorator rejected — the MCP SDK discovers `[McpServerTool]` reflectively on concrete types; a decorator would redeclare all 22 signatures): each tool does `var meta = await queue.GetMetaAsync(ct); return new ApiEnvelope<WriteResult>(result, meta, OperationStatus.Ok);`.

### Explicitly NOT over-engineered

No event bus (metrics port + Log suffice); no TTL/stale-queue sweep (the cap bounds growth); no per-project capacity config; no transaction abstraction around upsert+evict (a crash may leave the queue one over cap — the next propose loop self-heals; documented); no Envelope helper class.

### Implementation map (TDD, each step green before the next)

1. **Tools split** (mechanical refactor commit — green suite, inventory test updated)
2. **Queue persistence**: schema tests (fresh + migrated bank; UNIQUE(project_id, hash); not in search/stats) → DDL → store + records + SQL
3. **Pure policies**: capacity + eviction unit tests (deterministic tie)
4. **Queue service**: integration tests (real store, recording fake metrics, fake TimeProvider) — upsert-no-duplicate/first-created_at survives; evict-after-upsert loop; borrower-loses-first; promote top-N with shared dedup skip + drain; discard; sweep regression (queue untouched)
5. **Wiring**: config key + parse; ShareTools.ShareExtract rewired; ExtractionHostedService proposes persist; PromotionTools with access-gating tests; DI
6. **Envelope**: record tests; per-tool shape assertions in `McpServerToolSurfaceE2ETests` (catches closed-generic `ApiEnvelope<T>` schema-derivation risk immediately; fallback = concrete envelope records per tool file); version bump 1.1.0 + changelog

### Interface signatures (from the architect report)

```csharp
public interface IPromotionQueue
{
    Task<ProposeOutcome> ProposeAsync(string projectId, IReadOnlyList<QueueCandidate> candidates,
        CancellationToken cancellationToken = default);
    Task<PromoteOutcome> PromoteAsync(IReadOnlyList<string> projectIds, int limit,
        CancellationToken cancellationToken = default);
    Task<int> DiscardAsync(string projectId, string? hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default);
    Task<QueueMeta> GetMetaAsync(CancellationToken cancellationToken = default);
}

public interface IPromotionQueueStore
{
    Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, CancellationToken cancellationToken = default);
    Task<int> DiscardAsync(string projectId, string? hash, CancellationToken cancellationToken = default);
    Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<PromotionQueueRow?> EvictVictimAsync(string projectId, CancellationToken cancellationToken = default);
}

public interface IEvictionPolicy
{
    string? EvictionTarget(IReadOnlyDictionary<string, int> perProjectCounts);
}

public interface IPromotionQueueMetrics
{
    void RecordQueued(string projectId, int delta);
    void RecordEviction(string projectId, double victimScore, string reason);
    void RecordPromoted(string projectId, double waitSeconds);
    void RecordDiscarded(string projectId, double waitSeconds);
    void RecordUtilization(double ratio);
}
```

(Records: QueueCandidate, PromotionQueueRow, PromotionQueueStats, EvictedRow, ProposeOutcome, PromoteOutcome, QueueMeta, PromotionCapacityInfo — shapes per the architect report; envelope per Task 3.2 pins: `ApiEnvelope<TData>(TData? Data, ResponseMeta Meta, OperationStatus Result)` with `OperationStatus(int Code)` + optional `Message` property + `OperationStatus.Ok = new(200) { Message = "ok" }`.)
