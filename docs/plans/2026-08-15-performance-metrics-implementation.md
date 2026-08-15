# Performance metrics — the implementation plan (phase one)

Date: 2026-08-15 · Branch `task/perf-metrics` · Base `e1f211b0` · Baseline `dotnet build` GREEN, 0 warnings

Spec of record: `docs/work/specs/PerformanceMetrics.feature` (18 Rules, 39 Scenarios, gate-ruled 4/4).
Manifest: `docs/work/specs/spec.json`. The earlier design
(`docs/plans/2026-08-15-performance-observability-design.md`) predates the elicitation; **the
.feature wins wherever they disagree**.

Everything stated as fact was re-read from the code in this worktree, or measured. Anything not
verified is labelled **HYPOTHESIS**.

---

## Owner rulings, 2026-08-15

Three rulings taken after the first draft of this plan. They define what is being built.

**RULING 1 — Build phase one; defer the rest as a named list.** Phase one is the four items from
section 6's "smallest thing": search phase timings, the `metrics` table with its reaper, a simple
asynchronous writer, and a project-scoped `memory_performance`. Seven deferred items are recorded in
**section 5** with the property that makes each safe to defer — not omitted, the same treatment
spec.json gives card S1.

**RULING 2 — G4 is reshaped.** spec.json states G4 as *"Count bank writes during the call; assert
zero."* **That can never pass.** `SqliteMemoryStore.SearchAsync` ends with `await BumpAccessAsync(…)`
at `:272` — an access-bump write on **every** search. And SQLite's `total_changes()` is
**per connection**, so a before/after read on a test-held connection reports 0 regardless of what the
search did — a gate that passes on every implementation, which is the exact ADR-0056 vacuity G2
exists to prevent, reintroduced inside G4.

The owner has accepted the narrowing from "bank writes" to "metrics-table writes":

> Count rows in the `metrics` table immediately before and immediately after `memory_search` returns,
> with the background writer **paused**; assert the count is unchanged.
> *Watch red:* write one measurement synchronously inside `SearchAsync` and watch the count go
> non-zero.

This proves exactly what the rule exists to forbid — spec.json non-functional #2, *"No synchronous
bank write on the search hot path to record a measurement"* — and nothing it does not. **spec.json
still states G4 the old way; this ruling supersedes it.** The reasoning above must travel with the
gate, because the next person to read spec.json will find the unimplementable form.

**RULING 3 — Release engineering splits into its own task.** Version single-sourcing, build stamping
and tag/release automation are **not in this plan's execution scope**. Section 7 is their handoff.

---

## 0. Findings that reshape the plan

Read these before the packages; they change what the packages are.

**A. No v10 ladder step is needed, and one would be provably dead code.**
`MemorySchema.EnsureAsync` executes the unconditional `Ddl` block at `:347-349` and only *then*
reaches `if (storedVersion >= CurrentVersion) { return; }` at `:373`. A `CREATE TABLE IF NOT EXISTS`
in `Ddl` therefore reaches **every** bank — fresh, legacy and current — on every open, before any
version logic runs. Ruled in 3.1. *(Independently verified by the coordinator.)*

**B. The adjacent trap: index creation follows two conventions, and only one reaches legacy banks.**
This is the same ruling's failure mode and it has **already bitten once in this file**.
- Indexes inside the `Ddl` string (`idx_entries_*` at `:281-287`, `idx_sq_project_time` at `:319`)
  run on **every** open — they reach legacy banks.
- Indexes in the `if (fresh)` branch (`:360-370`) run **only for brand-new banks**.

The proof it is a real trap: `idx_entries_source_id` appears **twice** — at `:368` in the fresh
branch and again at `:1177` inside `MigrateToV5Async` — and the comment at `:364-365` says why:
*"created here for fresh banks …, and in MigrateToV5Async for v4→v5 migration banks."* The fresh
branch could not reach migration banks, so the index had to be written a second time.

**Consequence for WP0:** the `metrics` table **and every index the report reads** go in the `Ddl`
string. If an index goes in the `fresh` branch, every existing developer bank — which is all of them,
including the owner's — silently gets an unindexed metrics table, and the defect surfaces only as a
slow `memory_performance` on the banks that matter most. Gated in WP0.

**C. The owner's envelope ruling reverses the correlation-id design.** With `SearchTimings` riding out
on the result, `MemoryTools` holds both the timings *and* the correlation id and tags them itself.
`SearchQuery` needs **no** `CorrelationId` member. Ruled in 3.4.

**D. Rulings 1 and 3 interact: the `IBuildStamp` seam has no consumer in phase one.** Its only
consumer was the checkpoint rollup, which ruling 1 defers. Shipping an interface plus a null
implementation that nothing calls is premature abstraction (ask-if-simpler), so **phase one defines
neither**. The unavailable-stamp contract is recorded in D3 and section 7 for the package that will
need it. *This is a deliberate deviation from a literal reading of ruling 3, flagged rather than
taken silently.*

---

## 1. Phase-one work packages

**Seven packages.** Each gets its own worktree and its own agent. **File ownership is exclusive** — a
file appears under exactly one package's "Owns" list, and no other package may edit it while that
package is in flight.

The numbering has gaps — **WP5, WP7 and WP9 are the deferred packages** (section 5). The gaps are
kept so every cross-reference in sections 3-6 stays valid and so the deferrals are visible in the
package list itself rather than only in prose.

### Dependency order

```
Wave 1   WP0  foundations (Core contracts, pure statistics, schema + indexes)
             │
Wave 2   ────┼──────────────┐
         WP1 envelope       WP3 writer
         (EXCLUSIVE LOCK)   │
             │              │
Wave 3   ────┼──────────────┼──────────────┐
         WP2 phase timings  WP4 reaper     WP6 report + tool
             │              │              │
Wave 4   ────┴──────────────┴──────────────┘
                     WP8 G1 coverage + telemetry hook
```

**Parallelism:** W1 = 1. W2 = 2 in parallel. W3 = 3 in parallel. W4 = 1.

**The one hard serialisation — preserved from the original plan.** WP1 holds an **exclusive lock** on
`Core/Memory/IMemoryStore.cs`, `Infrastructure/Sqlite/SqliteMemoryStore.cs` and
`Tools/MemoryTools.cs`. WP2 needs `SqliteMemoryStore.cs` and **cannot start until WP1 has merged**.
WP3 shares no file with WP1 and runs alongside it.

**Shared file, resolved by wave ordering:** the host DI composition root is touched by WP3 (register
the recorder and the flusher) and WP6 (register the report service and the tool). They are in
different waves, so they never hold it at once. **No package in the same wave shares a file with
another.**

---

### WP0 — Foundations: contracts, pure statistics, schema

Critical path. Keep it tight; it unblocks everything.

**Owns (all new except the last):**
- `src/AiRaccoon.Core/Memory/SearchResults.cs` — `SearchResults(IReadOnlyList<MemorySearchResult> Results, SearchTimings Timings)` and `SearchTimings(TimeSpan Fts, TimeSpan Vector, TimeSpan Fusion, TimeSpan Affinity, TimeSpan Snippets, TimeSpan Bump)`
- `src/AiRaccoon.Core/Metrics/Measurement.cs` — the universal row shape
- `src/AiRaccoon.Core/Metrics/MeasurementKind.cs` — counter | histogram | gauge
- `src/AiRaccoon.Core/Metrics/IMeasurementRecorder.cs` — the port everything records through
- `src/AiRaccoon.Core/Metrics/Statistics.cs` — **pure** percentile/min/max/mean over a sample list
- `src/AiRaccoon.Core/Metrics/MetricsConfigKeys.cs` — key + default + `Parse` fallback per setting, copying `BankMaintenanceConfigKeys` exactly (note: that file lives in **Core**, `src/AiRaccoon.Core/Memory/BankMaintenanceConfigKeys.cs`, *not* Infrastructure as the recon claimed)
- `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` — the `metrics` table **and its indexes**, appended to the `Ddl` string

**No `IBuildStamp` / `NullBuildStamp`** — finding D. Nothing in phase one consumes a build stamp.

#### The schema, validated against real SQLite

I created this in `sqlite3`, ran the report's core aggregate against it, and re-ran the DDL to confirm
idempotency:

```sql
CREATE TABLE IF NOT EXISTS metrics (
    id             INTEGER PRIMARY KEY,
    name           TEXT NOT NULL,
    kind           TEXT NOT NULL,
    value          REAL NOT NULL,
    unit           TEXT NOT NULL,
    project_id     TEXT NULL,
    query_hash     TEXT NULL,
    correlation_id TEXT NULL,
    tags           TEXT NULL,
    recorded_at    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_metrics_name_time    ON metrics(name, recorded_at);
CREATE INDEX IF NOT EXISTS idx_metrics_project_time ON metrics(project_id, recorded_at);
```

**Three column decisions are load-bearing for every deferral** (see section 5's safety argument):
`project_id`, `query_hash` and `correlation_id` are **real columns, not entries in `tags`**.
`project_id` is the report's primary filter (scenario 26) and making it a column is what lets the
deferred whole-bank scope be *"omit the filter"* rather than a migration. `query_hash` and
`correlation_id` are the join back to `search_quality`. Everything else deferred stores as **rows
under new `name` values**, never new columns.

**Acceptance criteria**
1. `Statistics.Percentile` returns the p50/p95/p99 of a known distribution, matching values computed
   by hand and written as literals in the test.
2. A **fresh** bank exposes `metrics` and both indexes after one open.
3. **A bank stamped at the current version with the table absent** — the legacy path — exposes
   `metrics` **and both indexes** after one open. This is the criterion finding B exists for; a fresh
   bank passing proves nothing about it.
4. `MemorySchema.CurrentVersion` is unchanged at 9, and no `MigrateToV10Async` exists.
5. `MetricsConfigKeys` exposes a key const, a default const and a non-throwing `Parse` for each
   phase-one setting: buffer cap, flush interval seconds, hot-table retention days.

**Gates**
- **G2 (spec.json) — percentiles asserted against hand-computed values.** Lands here, not in a rollup
  package, because `Statistics` is a pure function with no database: a fast unit test.
  *Watch red:* change `Math.Ceiling` to `Math.Floor` in `Percentile` and watch p95 miss.
  **`TestData.Percentile` must not appear in this test.** Verified: it exists at
  `tests/AiRaccoon.Tests/TestData.cs:185-196`, is consumed by the shipping `ParityGateTests` (`:62`,
  `:67`, `:226`), and **has no test of its own** — asserting the product against it is two untested
  implementations agreeing, which is exactly ADR-0056's failure.
- **Legacy-path index gate.** Open a bank stamped at `CurrentVersion` with `metrics` absent; assert
  the table **and both indexes** exist afterwards. *Watch red:* move one index into the `if (fresh)`
  branch (`:360-370`) and watch the legacy-path assertion fail while the fresh-path one still passes.
  One line, and it catches the exact mistake that forced `idx_entries_source_id` to be written twice.
- **Config-key gate**, copying `BankMaintenanceConfigKeysTests`: assert the **literal** key string and
  the **literal** default. *Watch red:* change a default const and watch the test name it.

---

### WP1 — The envelope migration (EXCLUSIVE LOCK)

Owner-ruled: `SearchAsync` returns an envelope. Mechanical, high-volume, low-judgement — suitable for
a cheap model. **Lands in one commit.**

**Measured blast radius** (re-counted here, correcting the brief's "~30"):
- **3** production sites: `Core/Memory/IMemoryStore.cs:8`, `Infrastructure/Sqlite/SqliteMemoryStore.cs:173`,
  `Tools/MemoryTools.cs:147`. (`RecordSearchAsync` matches are **not** call sites — verified.)
- **28** test files, **132** call sites, plus `tests/AiRaccoon.Tests/TestHelpers/FakeMemoryStore.cs:22`.

The change is uniformly `await store.SearchAsync(q)` → `(await store.SearchAsync(q)).Results`. **Do
not split by directory** — a half-migrated interface does not compile, so a split buys nothing and
costs a broken intermediate state.

**Owns:** `Core/Memory/IMemoryStore.cs`, `Infrastructure/Sqlite/SqliteMemoryStore.cs`,
`Tools/MemoryTools.cs`, `TestHelpers/FakeMemoryStore.cs`, the 28 test files.

**Scope:** signature and plumbing only. `SqliteMemoryStore` returns
`new SearchResults(merged, SearchTimings.Empty)` — **no timing yet**. WP2 fills it in. This keeps the
mechanical commit free of judgement.

**The tool-size step, and why it is not number-moving.** `MemoryTools.Search` is at **39** of the
**40**-line cap (`tests/AiRaccoon.Tests/Unit/Layering/ToolMethodSizeTests.cs:37`, history at `:27`).
Unpacking the envelope adds lines. That file's own raise-history explicitly disfavours the cheap fix:
*"It could have been pushed under 30 by moving those lines into a private method of the same class,
but the gate measures per method, so that would move the number without moving the logic."*

So extract **real logic**: the search-quality recording block at `MemoryTools.cs:149-161` — a service
call wrapped in try/catch with a warning log — moves behind the quality service as a
`RecordSearchSafeAsync`. That removes ~11 lines, is logic another caller could want, and
**incidentally fixes** the pre-existing high-performance-logging violation at `MemoryTools.cs:160`
(`logger.LogWarning(ex, …)` instead of a `[LoggerMessage]` `Log` class). That fix is in scope *only*
because the lines are moving anyway; do not go hunting for others.

**Acceptance criteria**
1. Solution builds with 0 warnings; every previously-green test is green.
2. `MemoryTools.Search` body is ≤ 40 lines.
3. No behavioural change: search results identical before and after.

**Gates**
- `ToolMethodSizeTests.EveryToolMethod_IsThin`. *Watch red:* inline the extracted block back into
  `Search` and watch the gate name `MemoryTools.cs::Search` with its line count.
- Full `Speed=Fast` + `Speed=Slow` green — the only real risk is a missed call site, and the compiler
  plus the suite are the check.

---

### WP2 — Phase timings in the store

**Depends on WP1.** Owns `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs`.

**The measurable shape, read off the code** (`SearchAsync`, `:173-273`):

| Phase | Where | Shape |
|---|---|---|
| `fts` | `QueryFtsBatchAsync` `:210-222` | **accumulator** — inside the per-context loop `:204-259`, and runs **twice** per iteration on the fallback path `:216-221` |
| `vector` | `QueryDualVectorBatchAsync` `:224-227` | **accumulator** — same loop |
| `fusion` | `ReciprocalRankFusion.Fuse` `:261-267` | single span, outside the loop |
| `affinity` | `SearchResultMerger.Merge` `:268-269` | single span |
| `snippets` | `ResolveDeferredSnippetsAsync` `:270-271` | single span |
| `bump` | `BumpAccessAsync` `:272` | single span |

`fts` and `vector` must be **accumulators, not spans** — summing across contexts and across the
fallback re-query. Getting this wrong understates a multi-context search.

Measure with `TimeProvider.GetTimestamp()` / `GetElapsedTime()`. `TimeProvider` **is** already
injected (`SqliteMemoryStore.cs:26`), but these two members are used **nowhere in `src/`** today —
first use here, no local precedent to copy.

**Acceptance criteria**
1. Each phase reports a non-zero duration for a search that exercises it, and zero for one that does
   not (e.g. `vector` is zero when `VectorWeight == 0`).
2. A multi-context search reports `fts` ≥ the single-context `fts` for the same query.
3. The six phases sum to no more than the wall time of `SearchAsync`.

**Gates**
- **Phase attribution gate.** Slow exactly one phase; assert the increase lands in *that* phase and no
  other. *Watch red:* swap two phases' stopwatch assignments and watch the assertion name the wrong
  phase.
- **Accumulator gate.** Force the FTS fallback path and assert `fts` covers **both** queries.
  *Watch red:* replace the accumulator with a plain span around the last call and watch the doubled
  time vanish.

---

### WP3 — The writer: buffer, flusher, store

**Depends on WP0.** Runs in parallel with WP1 — shares no file with it.

**Scope trimmed by ruling 1.** This is the *simple* writer. What is **deferred to D1/D2**: the
`Channel.CreateBounded` + `DropWrite` + `itemDropped` machinery, the 60% occupancy aim, the
arrival-rate pressure estimator, and the 4-second rate-limit floor. **Do not build them.**

**What phase one builds:**
- A **capped in-memory buffer** (a lock-guarded list or `ConcurrentQueue` with a count), capacity from
  settings, default 1000. Beyond the cap, **drop and count the drop** — the count is retained because
  it is what keeps a gap in the data visible as a number rather than an absence. It is the *mechanism*
  that is deferred, not the behaviour.
- A **fixed-interval background flusher** (`PeriodicTimer`), interval from settings, default 30s.
  A fixed interval satisfies the idle-sweep rule (scenario 37) exactly and needs no aim.
- Batch insert, and the **save-time query-identity allowlist**: a measurement's query identity is
  exactly `{query_hash, correlation_id}`; a row carrying query text is **rejected on save**. Fails
  closed, per the stage-05 rephrasing.
- **Minimal self-instrumentation**, written **directly** by the flusher, never enqueued: flush
  duration, batch size, drop count. Cannot recurse by construction.

**Deliberately not draining on the maintenance tick.** The fixed interval already covers it, and
avoiding it keeps `BankMaintenanceHostedService.cs` out of this package — which is what lets WP3 and
WP4 run without sharing a file.

**Owns (all new):** `src/AiRaccoon.Infrastructure/Metrics/` — `MeasurementBuffer.cs`,
`MetricsFlusher.cs`, `SqliteMetricsStore.cs`, `MetricsRecorder.cs`; plus the host DI registration.

**Screaming architecture:** `Metrics/` is a domain concept, not a technical bucket. Infrastructure is
the honest home — it writes to the bank, and `BankMaintenanceHostedService` already establishes that
a hosted service lives there (`Microsoft.Extensions.Hosting.Abstractions` is already referenced).
**No new package reference**, and OpenTelemetry never enters (ruling 3.5).

**ADR-0062 is load-bearing for every test here.** A fake clock advanced before the flusher registers
its timer is **lost silently** — the service schedules against the new now and waits for a tick a
frozen clock never delivers. Use the existing seam
(`src/AiRaccoon.Infrastructure/Maintenance/TickSignal.cs`): wait for the observable, never for the
clock. **No `Task.Delay` may appear in these tests.**

**Acceptance criteria**
1. Recording never throws and never blocks; a search succeeds when the writer throws on every write.
2. A failed write is not retried into the caller's latency.
3. A burst of 600 with the flusher paused is written whole on the next flush; exactly 1000 is written
   whole with no drop; 1200 drops exactly 200 and reports 200.
4. No measurement is written on the caller's thread — the metrics row count is unchanged when
   `memory_search` returns.
5. The flusher records its own duration and batch size **without enqueueing anything**, including when
   the buffer is full.
6. A measurement carrying query text is rejected on save.

**Gates**
- **G4 (spec.json, reshaped per ruling 2).** Count `metrics` rows immediately before and after
  `memory_search` returns with the flusher paused; assert unchanged. *Watch red:* write one
  measurement synchronously inside `SearchAsync` and watch the count go non-zero.
- **Drop-count gate.** *Watch red:* remove the cap check and watch 1200-into-1000 stop reporting 200.
- **Best-effort gate.** *Watch red:* let the recorder's exception escape and watch `memory_search`
  fail.
- **No-recursion gate.** Assert the buffer's enqueue count is unchanged across a flush. *Watch red:*
  route one self-metric through `MetricsRecorder` and watch the count rise.
- **Allowlist gate.** *Watch red:* delete the save-time check and watch a text-carrying row persist.

---

### WP4 — The metrics reaper

**Depends on WP0.** Parallel with WP2 and WP6.

**Scope trimmed by ruling 1.** The **reaper only**. Deferred to **D3**: the checkpoint rollup table,
the dual version/fortnight triggers, the one-year checkpoint prune, the commit triple, and the
`RunCheckpointAsync` → `RunWalCheckpointAsync` rename (which existed to resolve a collision with a
rollup that phase one does not build — no collision, no rename).

**Owns:** `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs`.

**Where it hangs.** `RunPassAsync` (`:136-200`) already runs
`RunCheckpointAsync → RetryPendingEmbeds → PurgeExpiredNoise → PurgeExpiredRetention`, each
try/caught so a failure never fails the pass. `PurgeExpiredRetentionAsync:284-318` is the pattern to
copy — one settings key + default, log-and-continue. The test pattern is
`tests/AiRaccoon.Tests/Integration/Maintenance/RetentionReaperTests.cs`: fixed `Now`, a `DaysAgo(n)`
helper, `FakeTimeProvider`, and assertions on the **surviving rows**, not just a count.

**Retention is best-effort four weeks.** Holding more than four weeks is **within** contract, not a
violation (owner, stage 04). The reaper bounds growth; it does not guarantee a ceiling.

**Acceptance criteria**
1. Metric rows older than the window are deleted; rows inside it survive. Both directions asserted on
   the surviving rows.
2. A bank holding 40 days of measurements still answers a 40-day report — holding more than the
   window is not an error.
3. A reaper failure logs and **never** fails the maintenance pass.
4. The retention window is read from settings, with a non-throwing fallback to the default.

**Gates**
- **G3 (spec.json) — the reaper deletes past the window and nothing inside it.** *Watch red:* as
  ADR-0055's reaper gate was — invert the comparison and watch it delete the wrong side.
- **Pass-resilience gate.** *Watch red:* make the reaper throw and watch the pass still complete;
  then remove the try/catch and watch the pass fail.

---

### WP5 — *(deferred: see D3 and section 7)*

---

### WP6 — The report service and `memory_performance`

**Depends on WP0.** Parallel with WP2 and WP4.

**Scope trimmed by ruling 1.** **Project-scoped only.** Deferred to **D6**: the whole-bank scope and
the `EnsureWholeBankAsync` guard member from ruling 3.6. Because there is no whole-bank mode, the tool
takes a **required** `projectId` — which also means **no `ToolTelemetry.Projections` entry is needed**
(that map exists only for tools whose project id is optional or plural).

**Owns:**
- `src/AiRaccoon.Infrastructure/Metrics/MetricsReportService.cs` (new) — window/bucket aggregation
- `src/AiRaccoon/Tools/PerformanceTools.cs` (new) — the MCP tool
- `docs/reference/agent-memory-server.md` — `## Tools (26)` → `(27)` **and** a new table row
- the host DI registration (wave-serialised behind WP3)

**Four structural gates a new tool must satisfy** (`tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs`):
1. `McpToolNames_MatchConstStrings` (`:62-85`) — a `private const string TnMemoryPerformance`; an
   inline literal fails.
2. `EveryTool_NamesTheProjectIdParameter` (`:102-113`) — `projectId` **first**,
   `CancellationToken cancellationToken = default` **last**.
3. `PackagedReadme_ToolsHeading_MatchesActualToolCount` (`:116-124`) and
   `PackagedReadme_ToolsTable_ListsExactlyTheRegisteredTools` (`:127-142`) — the heading count **and**
   a `` | `memory_performance` | … | `` row. Also `ToolsNamespace_ExposesEverySpecTool` (`:26-59`)
   needs a `tools.ShouldContain("memory_performance")` line.
4. `ToolMethodSizeTests` — body ≤ 40 lines, so all logic lives in the service (ADR-0065, mcp-thin).

Copy `MemoryTools.Stats` (`:182-193`) as the shape.

**Report shape**
- Default window 3 hours, default bucket 1 minute → 180 buckets.
- A bucket wider than the window is **clamped** to the window and returns one averaged point — never a
  validation error.
- An empty window is an **empty series**, never an error.
- The series list is **derived from the tool inventory**, not from what is in the table — a
  never-called tool gets a series with count **zero**, present, not omitted (derive-or-delete-the-list:
  two tool lists would drift).
- Per series: count, p50, p95, p99, min, max — via `Core.Metrics.Statistics`.

**Acceptance criteria**
1. Response parses as JSON and carries one series per tool on the derived inventory.
2. A never-called tool appears with `count: 0`.
3. No window / no bucket → 3 hours, 180 buckets. One-hour window in two-hour buckets → 1 bucket
   averaging the hour.
4. A quiet bank returns an empty series and does not error.
5. The report covers only the calling project.
6. `dotnet build` green and all four tool-inventory gates green.

**Gates**
- **Derived-inventory gate.** *Watch red:* build the series list from `SELECT DISTINCT name FROM
  metrics` and watch the zero-count scenario lose its key.
- **Clamp gate.** *Watch red:* throw on `bucket > window` and watch the clamp scenario fail.
- **Project-scope gate.** Seed two projects; assert the report shows only the caller's. *Watch red:*
  drop the `project_id` filter and watch the other project's rows appear.

---

### WP7 — *(deferred: see D4)*

---

### WP8 — G1 coverage gate and the telemetry hook

**Depends on WP2, WP3, WP6.**

**Owns:** `src/AiRaccoon/Observability/ToolTelemetry.cs`,
`src/AiRaccoon/Observability/ToolExecutionActivity.cs`, and the G1 test file.

**The hook point, verified.** `ToolTelemetry.Filter` (`:39-72`) is a `CallToolFilter` already seeing
every call. It sees only `request.Params.Arguments` and **never** the `CallToolResult` — so it cannot
read the correlation id `MemoryTools.Search` returns in the envelope Meta. Duration lives in
`ToolExecutionActivity` (`:46` stopwatch, recorded at `:67`/`:87`). Record alongside
`activity.RecordInvocation()` / `RecordError()`.

**Consequence to accept:** the tool-level measurement carries no correlation id. The correlation id
is carried by the **search phase** measurements, which `MemoryTools` tags itself (ruling 3.4). Two
measurement families with different tag sets, honestly documented, beats restructuring the filter.

**Acceptance criteria**
1. Every tool on the derived inventory records at least one measurement when called once.
2. The measurement carries the tool name and a bounded project id.
3. A refused call still records, with the refused sentinel.

**Gates**
- **G1 (spec.json) — every tool produces at least one measurement.** Iterate
  `tests/AiRaccoon.Tests/TestHelpers/RegisteredTools.cs` — `Methods()` reflects `[McpServerTool]` over
  the `AiRaccoon.Tools` namespace, `Count` is live and documented *"Never write this number into a
  test."* That is spec.json's "derived tool inventory".
  *Watch red:* silence one tool's recording and watch the gate **name that tool** — assert the failure
  message contains the tool name, not merely that a count dropped.

---

### WP9 — *(deferred: see D5)*

---

## 2. Gate summary

| Gate | Package | Watch-red move |
|---|---|---|
| **G1** every tool records | WP8 | silence one tool; assert the message names it |
| **G2** percentiles vs hand-computed | WP0 | perturb `Percentile` (`Ceiling`→`Floor`) |
| **G3** reaper both directions | WP4 | invert the comparison |
| **G4** no synchronous metric write *(reshaped, ruling 2)* | WP3 | write one measurement synchronously in `SearchAsync` |
| Legacy-path index | WP0 | move an index into the `if (fresh)` branch |
| Config-key literals | WP0 | change a default const |
| Tool method size | WP1 | inline the extracted block back |
| Phase attribution | WP2 | swap two phases' assignments |
| FTS accumulator | WP2 | replace the accumulator with a span |
| Drop count | WP3 | remove the cap check |
| Best effort | WP3 | let the recorder's exception escape |
| No self-recursion | WP3 | route a self-metric through the recorder |
| Save-time allowlist | WP3 | delete the check; watch text persist |
| Pass resilience | WP4 | remove the try/catch; watch the pass fail |
| Derived inventory | WP6 | build the series from the table |
| Bucket clamp | WP6 | throw instead of clamping |
| Project scope | WP6 | drop the `project_id` filter |

Every gate has a watch-red move, and none of them has only ever passed
(prove-the-check-fails).

---

## 3. Rulings on the seven mismatches

### 3.1 The v10 ladder step — ceremony. Put the tables in `Ddl`.

**We build:** `CREATE TABLE IF NOT EXISTS` **plus both indexes** appended to `MemorySchema.Ddl`.
`CurrentVersion` stays **9**. No `MigrateToV10Async`.

**Why the alternative loses — measured.** `EnsureAsync` runs `Ddl` at `:347-349` and only *then*
reaches `if (storedVersion >= CurrentVersion) { return; }` at `:373`. A v10 step could only ever
execute `CREATE TABLE IF NOT EXISTS metrics` against a bank where `Ddl` created that exact table
**two lines earlier in the same call**. Guaranteed no-op: more code, one more place to be wrong, zero
behavioural difference.

The schema's own doctrine agrees, at `:38-44`: *"the ladder is for changes that need guarded,
one-time work."* `CREATE TABLE IF NOT EXISTS` is re-runnable by construction.

**On v6, the counter-example.** `MigrateToV6Async` (`:445-468`) adds `noise_clusters` in a ladder step
— but puts the table **only** there, not in `Ddl`. It chose ladder *instead of* `Ddl`, not *in
addition to*. Either single location works; `Ddl` is better because it needs no version bump and no
`MemorySchemaVersionTests` churn.

**The trap this ruling carries with it — see finding B.** Indexes must go in the **`Ddl` string**, not
the `if (fresh)` branch. `idx_entries_source_id` at `:368` and `:1177` is the proof that the fresh
branch does not reach legacy banks; it had to be written twice. Gated in WP0.

### 3.2 `fusion` and `affinity` — the recon was wrong; only `affinity` is impure.

**We build:** `fusion` = a span around `ReciprocalRankFusion.Fuse` at `:261-267`. `affinity` = a span
around `SearchResultMerger.Merge` at `:268-269`, documented as covering the affinity rank **plus** the
order-preserving second fusion **plus** the floor/limit trim.

**Correcting the brief.** The recon said `Merge` does fusion and affinity in one call so neither is
separately measurable. Half right: `ReciprocalRankFusion.Fuse` is called **outside** `Merge`, as its
own statement, and is measurable today with no change. Only `affinity` is a compound.

**Why we do not split `Merge`.** ADR-0058 records that the second fusion was removed, measured four
ways, and **not shipped**: deleting it silently re-scales two constants ADR-0005's sweep calibrated
against the compressed score curve. Splitting the method to get a purer timing invites that edit. A
phase name honestly describing a slightly wider span costs nothing; a re-scaled ranking constant costs
retrieval quality.

### 3.3 The search envelope — owner-ruled, planned as WP1.

`IMemoryStore.SearchAsync` returns `Task<SearchResults>`. Plain name — it is a search result, not a
wrapper or payload. Cost, measured: 3 production sites, 28 test files, 132 call sites, plus
`FakeMemoryStore`. One mechanical commit, cheap model, exclusive lock.

### 3.4 The correlation id — no `SearchQuery` change. The host tags.

**We build:** nothing in Core. `MemoryTools.Search` moves the `Guid.CreateVersion7()` mint from `:149`
to before the store call, holds both the timings (from the envelope) and the correlation id, and hands
both to the recorder.

**Why the alternative loses.** Threading `CorrelationId` onto `SearchQuery` was right *for a
side-channel design*, where the store had to tag its own measurements because nobody else could. The
envelope ruling removes that need: the host holds both halves. Adding a Core member for a value Core
never reads is dead weight, and puts a host-observability concern in a domain record.

### 3.5 The channel and the layer home — Infrastructure, and OpenTelemetry never enters.

`src/AiRaccoon.Infrastructure/Metrics/` holds the buffer, flusher and writer; the MCP tool lives in
the host. **No new package reference in Infrastructure.**

**The design's problem dissolves rather than being solved.** The brief notes OpenTelemetry is
unreachable from Infrastructure. But spec.json rules OTLP export explicitly **out of scope** — the
existing Meter instruments stay untouched and this feature adds none. There is no Meter-vs-table
composition to place.

### 3.6 Cross-project read access — a new guard member, not a lie in the enum.

**Deferred with D6** — phase one is project-scoped only, so nothing needs it yet. The ruling stands
for the package that builds it:

**Build** one new member, `EnsureWholeBankAsync(string callingProjectId, string toolName, …)`,
implemented against the existing `ResolveAsync` + `AccessModePolicy`.

**The spec's premise is false, and by how much.** The spec says crossing the project boundary is *"the
same shape the other cross-project surfaces already use"*. Verified: **no such shape exists.**
`AccessRequirement` is `Read | Write | Destructive`; `Read` short-circuits to always-allowed at
`Access/MemoryAccessGuard.cs:30-33`; `Full` is demanded only by `Destructive`. Worse, the nearest
analogue is an **ungated hole**: `PromotionTools.List` (`:37-40`) skips the gate entirely when
`projectId is null`. And `IMemoryAccessGuard.EnsureAsync` takes a **non-nullable** `string projectId`
(`:12-13`), so a whole-bank call cannot even be expressed through it.

**Why the alternatives lose.** *Calling it `Destructive`* is a lie — a read-only report would demand
write-capable mode. *A fourth enum value* ripples through `AccessModePolicy.Allows`, `RequiredFor` and
every switch, for one caller. *Leaving it ungated* copies a defect.

### 3.7 The commit stamp, and the G4 reshape

**Commit stamping** has moved out of this plan entirely — **ruling 3**, handed off in section 7. Phase
one defines no `IBuildStamp` because nothing consumes one (finding D).

**G4** is reshaped by **ruling 2**, recorded at the top of this document with its full evidence:
`BumpAccessAsync` at `:272` writes on every search, and `total_changes()` is per-connection. The gate
now counts `metrics` rows with the flusher paused. **spec.json still states the old form** — that is
why the reasoning is recorded in two places.

---

## 4. Scenario map — all 39

**16 scenarios are proven by phase one, 7 were claimed but are not, and 16 are deferred with their
package.** No scenario disappears silently.

> **Correction, 2026-08-15, from the integration review.** This section originally claimed 23 proven.
> It was checked scenario by scenario against the tests that claim them, and the claim does not hold.
> No test method was missing or renamed — every gap is an absent test, or a test proving something
> adjacent to what the scenario says. Recorded rather than quietly amended, because a coverage number
> nobody re-derived is exactly the kind of claim `proof-of-done` exists to stop.
>
> **No test exists at all (4):** #1 (a three-week-old bank still answers for its oldest measurement —
> nothing seeds beyond a one-hour window), #2 (a bank holding more than four weeks is within contract
> — the reaper test covers the delete boundary, not the read), #17 (no setting turns measurement off —
> nothing sets all three `MetricsConfigKeys` restrictively and then shows a search still records; this
> is also spec.json's own accepted weakness **O2**, whose stronger derived form was never built), #23
> (two runs of the same query share a hash — only one run is ever hashed).
>
> **Weaker than claimed (3):** #20 (asserts the row count, never that the search returned its own
> results nor that the report excludes it), #19 (proves the async flusher path is not retried, never
> exercises the synchronous recorder path its Given/When describes), #25 (passes, but against the
> denylist the review found — the tests exercise only the five known keys, so they cannot catch the
> gap).
>
> **Correction, 2026-08-16, from closing the seven.** All seven were re-driven through TDD (write,
> watch red against unmodified production code, revert any mutation used to prove the test
> non-vacuous). Test files only — no production code changed. **6 of 7 now hold; 1 is a genuine,
> unresolved conflict.**
>
> - **#1** — closed. `MetricsReportServiceTests.GetReportAsync_OldestMeasurementIs21DaysOld_WindowOf28Days_ReportCoversIt`
>   seeds a row 21 days old and asks for a 28-day window (exactly `PerformanceReportBuilder.MaxWindow`,
>   so not clamped). Watched red by seeding at 29 days instead (`Count` 1→0), then reverted.
> - **#2 — BLOCKED, not resolved.** `GetReportAsync_BankHolding40DaysOfMeasurements_WindowOf40Days_ReportCoversAll40Days`
>   is written and genuinely fails against current production: seeding rows at 40/20/1 days old and
>   asking for a 40-day window returns `Count == 2`, not 3 — the 40-day-old row is discarded by
>   `PerformanceReportBuilder.MaxWindow` (`= MetricsConfigKeys.DefaultRetentionDays`, 28 days), which
>   clamps `effectiveWindow` and re-filters samples against the clamped start regardless of what SQL
>   fetched (PerformanceReportBuilder.cs:23,45). The scenario (stage 04: "we can hold more than four
>   weeks, this is best effort limit") and the review-fix clamp cannot both hold. The test is committed
>   `[Fact(Skip = ...)]`, naming the conflict and this section, so the gap is visible rather than either
>   breaking the suite or vanishing silently. **This is an owner decision** (relax the clamp, or amend
>   the scenario) — not resolved by this pass, per instruction.
>
> **Resolution, 2026-08-16.** Owner ruling: bound the bucket count, not the window. `MaxWindow` is
> gone; `PerformanceReportBuilder.MaxBucketCount` (2000) caps the per-series bucket count instead, by
> widening the bucket to fit the whole requested window rather than truncating the window — the same
> shape the .feature already rules for an over-wide bucket clamping to the window, applied in the
> other direction. The window is always honoured in full; `PerformanceReport.Bucket`/`BucketCount`
> report the bucket actually used, widened or not. `Build_BankHolding40DaysOfMeasurements...` is
> un-skipped and green. Allocation bound verified: `Build_ExtremeWindow_WidensTheBucketInsteadOfTruncatingTheWindow`
> asserts `BucketCount <= MaxBucketCount` for a 525,600-minute (one-year) window at the 1-minute
> default bucket — watched red with the cap removed (`BucketCount` came back as 525600, matching the
> ~18.9M-object unbounded case this exists to prevent).
> - **#17** — closed. `SearchMetricsIsolationTests.Search_EveryMetricsSettingAtItsMostRestrictiveValue_StillRecordsAMeasurement`
>   derives the settings keys from `MetricsConfigKeys`'s own `*Global` constants via reflection (a new
>   setting joins automatically), pins each to its most restrictive functioning value, and shows a
>   search still enqueues a measurement (and that capacity=1 genuinely bites — `DroppedCount > 0`).
>   Passed immediately: production has no kill-switch setting today, matching spec.json O2's
>   deliberately weak framing. No production change needed.
> - **#19** — closed. `MemoryToolsTests.Search_WhenTheRecorderThrows_TheFailedWriteIsNotAttemptedAgain`
>   exercises the caller's own path (`RecordPhaseMeasurements`'s synchronous loop inside
>   `MemoryTools.Search`), not the async flusher, and asserts a call count of 1. Passed immediately
>   against production; verified non-vacuous by temporarily adding a one-line retry to
>   `RecordPhaseMeasurements` (call count 1→2, red), then reverting (confirmed clean via `git diff`).
> - **#20** — closed. `SearchMetricsIsolationTests.Search_ReturnsItsResultsAndIsExcludedFromTheReport_BeforeTheBackgroundReaderRuns`
>   asserts all three: the search enqueues nothing durable (existing test), returns its own non-empty
>   results, and the `search.fts` series reads back at `Count == 0` before any flush — cross-validated
>   by the sibling `MetricsReportServiceTests.GetReportAsync_PhaseMeasurements_AppearAsSeriesAlongsideTools`,
>   which proves the same series shows `Count > 0` once the identical data is actually flushed, so the
>   zero here is not a filter that always returns zero.
> - **#23** — closed. `SearchMetricsIsolationTests.Search_CalledTwiceWithTheSameQuery_BothRunsShareTheSameQueryHash`
>   runs `memory_search` twice (two distinct correlation ids, asserted), flushes, and reads back two
>   `search.fts` rows sharing one distinct `query_hash`.
> - **#25** — **already covered, correction was wrong about this one.** `SqliteMetricsStoreTests` already
>   has `SaveBatchAsync_TagsKeyNotOnTheAllowlist_IsRejected` (an arbitrary unlisted key, `"prompt"`) and
>   `SaveBatchAsync_AllowedTagKeyWithAnUnrecognisedValue_IsRejected` (free text under the allowed
>   `"phase"` key) — both passing (15/15 in the file). These landed in `ac1244fa` ("fix: close the two
>   review blockers — a real query-identity allowlist...") on `wp/review-fixes`, after this correction
>   block was written and before this task's base commit merged it in. The plan's correction predates
>   the fix; no new test was needed.

Reqnroll binding is deferred (**D5**), so phase-one scenarios are proven as **ordinary unit and
integration tests**. The `.feature` stays **unlinked**, which is what keeps the `Category=bdd` job
green — an unlinked feature generates no test class, so `BddGateCoverageTests` never sees it.

### Claimed by phase one (23 — of which 16 hold; see the correction above)

| # | Scenario | Package | Proof |
|---|---|---|---|
| 1 | Three-week-old bank answers for its oldest measurement | WP6 | integration (report over 28d) |
| 2 | Bank holding >4 weeks is within contract | WP4 | integration |
| 8 | Every tool produces ≥1 measurement when called | WP8 | **G1** |
| 9 | A never-called tool carries a zero-count series | WP6 | **derived-inventory gate** |
| 10 | `memory_performance` returns JSON, not prose | WP6 | integration |
| 11 | Empty window is an empty series, not an error | WP6 | unit (service) |
| 17 | No setting turns measurement off | WP3 | integration — knowingly weak, see below |
| 18 | Search succeeds when the metric write throws | WP3 | **best-effort gate** |
| 19 | A failed metric write is not retried | WP3 | unit (attempt counter) |
| 20 | Search returns before its measurement is written | WP3 | integration (flusher paused) |
| 21 | No bank write during the call | WP3 | **G4 — reshaped, ruling 2** |
| 22 | A burst within capacity is flushed whole | WP3 | unit (600, flusher paused) |
| 23 | Two runs of the same query share a hash | WP6 | integration |
| 24 | Query identity is exactly hash + correlation id | WP3 | unit (save-path allowlist) |
| 25 | A measurement carrying query text is rejected on save | WP3 | **allowlist gate** — fails closed |
| 26 | Report defaults to the calling project | WP6 | **project-scope gate** |
| 29 | Burst beyond capacity drops; count reports how many | WP3 | **drop-count gate** (1200→200) |
| 30 | Exactly 1000 arrive between two flushes | WP3 | unit (no drop) |
| 33 | No window/bucket → 3 hours, 180 buckets | WP6 | unit (service) |
| 34 | Bucket wider than window clamps to one point | WP6 | **clamp gate** |
| 37 | Idle bank flushes after thirty seconds | WP3 | unit (fixed interval; ADR-0062) |
| 38 | Flush records its own duration without enqueueing | WP3 | **no-recursion gate** |
| 39 | Self-metrics written even when the buffer is full | WP3 | unit |

**Adapted, and stated rather than glossed:** 22, 29, 30 and 39 are written against *"a channel holding
1000 measurements at most"*. Phase one has a **capped buffer**, not a `Channel<T>`. The observable
behaviour asserted is identical — capacity, whole-flush, drop-and-count — and D2 swaps the mechanism
behind `IMeasurementRecorder` without changing these assertions. 35 (the 4-second floor) is **not**
covered by the fixed interval and is deferred honestly rather than claimed.

### Deferred (16)

| # | Scenario | Deferred with |
|---|---|---|
| 3 | Failed checkpoint leaves the hot table unpruned | **D3** rollups |
| 4 | A quiet fortnight still writes a checkpoint | **D3** |
| 5 | Checkpoint older than a year is removed | **D3** |
| 6 | A year of checkpoints kept however many | **D3** |
| 7 | Discarding takes the oldest, not the newest | **D3** |
| 12 | CLI prints a path and the file exists at it | **D4** CLI |
| 13 | Report lands in the invocation dir, not the data root | **D4** |
| 14 | Unwritable dir falls back to temp | **D4** |
| 15 | A measurement carries no dimensions of its own | **D7** bank shape |
| 16 | Bank shape sampled once per flush | **D7** |
| 27 | Whole-bank report requires elevated mode | **D6** whole-bank |
| 28 | A checkpoint answers for p99, not only the mean | **D3** — but **G2 lands in phase one** via `Statistics`, so the percentile *correctness* is already gated |
| 31 | Checkpoint records commit, version, commit timestamp | **D3** + section 7 |
| 32 | A new version starts a checkpoint early | **D3** |
| 35 | No second flush within four seconds | **D1** adaptive flush |
| 36 | Rising pressure lowers the aim | **D1** |

### Scenarios I judge problematic as written

**#21 — was unimplementable; reshaped by ruling 2.** Evidence at the top of this document.

**#17 — knowingly weak, already accepted.** spec.json `acceptedWeaknesses.O2` records that this proves
one setting combination does not disable measurement, not that none can. Built as written. The
stronger derived form belongs beside G1 as a structural gate; noted, not smuggled in.

**#14 — implementable, but silently vacuous as root.** `chmod 0500` is bypassed for root, so it would
pass without exercising the fallback. When D4 is built, guard it with root detection and `Assert.Skip`
naming the reason, so a vacuous pass is visible as a skip. **HYPOTHESIS:** CI runs as non-root —
confirm then.

**#36 — needs "pressure" observable before it can be asserted.** Deferred with D1; when built, it
requires the flusher to expose occupancy-at-flush as a test seam. The most fixture-heavy scenario in
the file.

**#15 — proven by consequence, not inspection, and that is deliberate.** Flagged only so nobody
"improves" it into a row inspection when D7 is built.

---

## 5. Deferred scope, and why deferring each is safe

Recorded as a named list rather than an omission — the treatment spec.json gives card S1, because an
unexplained absence reads as an oversight and gets built anyway.

**The property that makes all of this safe, stated once:** phase one fixes the `metrics` table's shape
— a universal row (`name`, `kind`, `value`, `unit`, `recorded_at`) plus three indexed dimensions
(`project_id`, `query_hash`, `correlation_id`) chosen precisely so nothing deferred needs a new column.
**Every deferred item stores its data as rows under new `name` values, or in a new table of its own.**

| | Deferred item | Scenarios | Why deferring is safe |
|---|---|---|---|
| **D1** | Adaptive flush aim: arrival-rate pressure, 60% occupancy aim, 4-second rate-limit floor | 35, 36 | **Settings and flusher internals only.** Touches no table and no port. A new settings key is a row in the existing KV settings table — never a migration. |
| **D2** | `Channel.CreateBounded` + `DropWrite` + `itemDropped` machinery | *(none — behaviour covered)* | **Swaps a mechanism behind `IMeasurementRecorder`.** Phase one already has the cap-and-count *behaviour* and its gates; D2 changes how it is achieved. The drop count is stored as an ordinary row (`name = "metrics.dropped"`), so no column either way. |
| **D3** | Checkpoint rollups: `metric_checkpoints` table, dual version/fortnight triggers, one-year age prune, the commit triple, and the `RunCheckpointAsync`→`RunWalCheckpointAsync` rename | 3, 4, 5, 6, 7, 28, 31, 32 | **A new table, added to `Ddl`, reaching every bank on open** (ruling 3.1 — and its indexes go in `Ddl` too, per finding B). The rollup **reads** `metrics` and never alters it. Needs `IBuildStamp` + the unavailable-stamp contract, both defined then; see section 7. |
| **D4** | CLI `ai-raccoon performance` | 12, 13, 14 | **Read-only over the same tables through the same `MetricsReportService`.** A pure addition in the host: a verb in `CliCommandTree`, a handler, a DI line, a `ConfigCommands` switch arm. No product change. |
| **D5** | Reqnroll bindings + linking the `.feature` | *(proof mechanism)* | **Tests only.** The `.feature` stays unlinked, so it generates no test class and `Category=bdd` never sees it. Phase-one scenarios are proven as ordinary tests, which the linking would later wrap, not replace. |
| **D6** | Whole-bank scope + `EnsureWholeBankAsync` (ruling 3.6) | 27 | **`project_id` is a real indexed column in phase one**, so whole-bank is *"omit the filter"* — a query change, not a migration. The guard member is additive to an interface the tool layer already depends on. |
| **D7** | Bank-shape correlation dimensions: entry count, bank bytes, project count, embedded fraction, sampled once per flush | 15, 16 | **Stored as ordinary measurement rows** (`name = "bank.entries"`, `"bank.bytes"`, …), so no columns. The sampler is flusher-internal. The expensive dimension (embedded fraction, 1.286 ms per read on 2,518 rows) is why it is sampled per flush — that design survives the deferral unchanged. |

### Direct answer to ruling 4: does any deferral force a `metrics` migration?

**No — provided WP0 ships the three column decisions above.** I validated the schema against real
SQLite: created it, ran the report's project-scoped windowed aggregate, and re-ran the DDL to confirm
idempotency.

The reasoning per item is in the table. The two that could have forced a migration, and why they do
not:
- **D6 whole-bank** would force one if `project_id` lived inside the `tags` JSON, because filtering
  and indexing on it would then need a real column. Phase one makes it a column. **This is the single
  most load-bearing decision in WP0.**
- **D3 rollups** would force one if checkpoint statistics were columns on `metrics`. They are a
  separate table, which ruling 3.1 shows reaches every bank for free.

**The one residual risk, stated:** if a later package wants to filter or aggregate on a dimension that
phase one put inside `tags` JSON, that is a **query-performance** concern, not a migration — and the
fix is an index added to the `Ddl` string, which reaches legacy banks on the next open. That is only
true because of ruling 3.1 and finding B; if an index were ever added to the `fresh` branch instead,
this escape hatch closes silently. **That is why WP0's legacy-path index gate matters beyond WP0.**

---

## 6. The simpler-shape question — asked, answered, and enacted

Retained as the record of *why* phase one is what it is. Ruling 1 adopted this analysis.

### What earns its complexity

- **Phase timings (WP2).** This is the feature. spec.json's own constraint says it: *"WP5's +1.4 ms
  per KNN (ADR-0068) is invisible in production until the store times its phases."* (That "WP5" is the
  prior ctx-partition-key campaign's package, not this plan's.) Today a `memory_search` is one opaque
  number covering six phases; `grep -rn "Stopwatch" src/AiRaccoon.Infrastructure/` returns nothing.
- **The `metrics` table plus its reaper (WP0 + WP4).** Not speculative hygiene: `promotion_discards`
  reached 965 rows and `search_quality` 424, both with no reaper anywhere in `src/` until ADR-0055. A
  per-measurement table grows far faster.
- **Best-effort recording (WP3).** A diagnostics feature that can fail a search is worse than no
  diagnostics feature.
- **One read-back surface (WP6).** The owner needs to *see* the numbers.

### What was ceremony at this project's scale — and is now deferred

- **The arrival-rate-adaptive flush aim (D1).** Three settings, a rate estimator, an
  occupancy-at-flush test seam and the file's most fixture-heavy scenario — to decide when to write a
  batch of rows to a **local SQLite file** for **one developer**. Highest cost-per-value item in the
  spec. A fixed interval is indistinguishable in outcome at this volume.
- **The `Channel`/`DropWrite` machinery (D2).** Capacity 1000, fed by one developer's agent traffic;
  the buffer will not fill. And it is *new mechanism with no local precedent* — the repo's only
  channel is `CreateUnbounded` with no drop path (`Watch/WatchPipeline.cs:56`) — so it is not cheap
  insurance either.
- **The commit triple (D3 + section 7).** What the metrics need is *one field*. What it grew into was
  version single-sourcing, a three-source MSBuild stamping target with a degradation contract, and a
  tag-and-release workflow — three packages of release engineering hanging off one column of a summary
  table. Version alone gets most of the value for a maintainer who can map a date to a commit with
  `git log`.
- **Whole-bank access gating (D6).** For a single-maintainer local server, gating a read-only
  diagnostics report protects one developer's timing data from that same developer — and the spec
  justified it by analogy to a shape that does not exist (ruling 3.6).
- **Reqnroll linking (D5).** 39 step bindings plus a hand-wired context in `Hooks.cs` so a diagnostics
  feature reads as Gherkin in CI. The `.feature` is already the spec of record and valuable as such.

### On the scope growth

This task was commissioned as "implement the performance metrics spec" and accreted commit stamping,
release automation and version single-sourcing. Each was owner-authorised and each is independently
worth doing — the six-copy version literal whose drift guard is itself one of the copies is a genuine
defect. But it is not metrics work, and Track M needed **one field** from all of it. Ruling 3 split it
out; section 7 is the handoff.

---

## 7. Handoff — release engineering (a follow-up task)

**Not in this plan's execution scope (ruling 3).** Everything a follow-up task needs is here.

Order is strict: **R1 → R2 → R3**. R3 needs R1's VERSION file as its trigger.

### Verified by experiment — including one correction that must not creep back

I built throwaway projects and read the emitted attributes.

1. **The VERSION property function works.** With `VERSION` containing `9.9.9\n` and
   `<Version>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())</Version>`,
   a built assembly reports `AssemblyVersion 9.9.9.0` and `PackageVersion 9.9.9`. **The `.Trim()` is
   required** — the trailing newline otherwise lands in the version string.
2. **`SourceRevisionId` auto-appends.** With `SourceRevisionId = deadbeefcafe`, the built assembly
   reports `InformationalVersion = "9.9.9+deadbeefcafe"`, read from
   `AssemblyInformationalVersionAttribute` on the compiled binary.
3. **CORRECTION — an explicit `<InformationalVersion>` does NOT block the `+sha` append.** I tested a
   project with `<InformationalVersion>9.9.9</InformationalVersion>` *alongside* `SourceRevisionId`
   and the SDK appended anyway: `"9.9.9+deadbeefcafe"`. **The coordinator asserted the opposite to the
   owner and has retracted it.** Removing the explicit property is worth doing for **deduplication**
   (R1's actual purpose); it is **not** what unblocks stamping, and R1 and R2 are therefore
   **independent**. Do not re-justify R1 by claiming it unblocks R2.

### R1 — VERSION as the single source of truth

**The problem, verified.** Six literal copies across three files: `AiRaccoon.csproj:14-16`
(`PackageVersion`, `InformationalVersion`, `AssemblyVersion`), `src/AiRaccoon/.mcp/server.json`
(top-level `version`, `packages[0].version`), `VersionContractTests.cs:16` (`ExpectedVersion`).
`scripts/version-bump.py` rewrites all six.

**The drift guard is itself one of the copies.** `VersionContractTests` asserts the csproj and
server.json against `ExpectedVersion` — a constant the same script rewrites. It catches a hand-edit
that misses a file; it **cannot** catch a script that is consistently wrong.

**Shape.** `VERSION` at repo root (bare semver, one line). `Directory.Build.props` — already
repo-root and inherited by every project — reads it into a single `<Version>`; the other three
properties derive. Delete csproj lines 14-16.

**`VersionContractTests` must be rewritten, not adjusted.** Verified: it loads the csproj as XML and
calls `.Elements("PackageVersion").First()`. Once those elements are gone it throws
`InvalidOperationException` — it does not fail with a clear message, it crashes.
`DeclaredVersions_CarryNoPrereleaseSuffix` breaks the same way.

**Ruling on server.json — keep the file, delete the constant.** It is a shipped MCP manifest packed
into the nupkg and genuinely needs the literal. *Generating it at pack time* is cleanest by
derive-or-delete-the-list but changes packaging, and a manifest correct in the repo but wrong in the
package is a defect nobody sees until a registry rejects it — **loses on risk**. *Keeping the file and
asserting it against the **assembly** version with **no literal constant*** **wins**: two independent
artifacts compared directly, strictly stronger than three compared against a script-maintained
constant. Note the assembly's `InformationalVersion` carries `+sha` once R2 lands, so compare the part
before `+`.

**Ruling on `scripts/version-bump.py` — it survives, reduced to one file.** Its
`replace_version(path, old, new, expected)` count guard is a real check; with one file it becomes "this
file holds exactly the version it claims to". Deleting the script would make bumping an unvalidated
hand-edit.

### R2 — Build stamping

**This is the only package the metrics care about.** It supplies `IBuildStamp` (which phase one does
**not** define — finding D) and its implementation.

**Why stamping is needed despite 35 existing tags:** the running process is an installed NuGet tool
with no git repository near it. Nothing reads a tag at runtime. Values are baked in at build/pack time
or they do not exist. (Verified: 35 tags, format `vX.Y.Z`, all **lightweight**; `git describe --tags
--long` returns `v1.15.0-4-ge1f211b0`; tagging is manual today.)

| Field | 1. CI | 2. Local git | 3. Fallback |
|---|---|---|---|
| **version** | `VERSION` via `<Version>` | same | same — always present |
| **commit sha** | `GITHUB_SHA` → `SourceRevisionId` | `git rev-parse HEAD` | `"unknown"` |
| **commit timestamp** | `git log -1 --format=%cI` | same | *omitted* (NULL) |

Sha from `GITHUB_SHA` needs no git and no tags. Timestamp from `git log`, not the GitHub API — the API
route needs a token, a network call and error handling inside an MSBuild target for what `git` yields
in one command.

**VERIFIED TRAP: four of five checkouts are shallow.** `fetch-depth: 0` is set on **exactly one**
checkout, `build.yml:47`. The other three in build.yml (`:90`, `:120`, `:160`) and — critically —
**`publish.yml:52`**, the job that builds the shipped binary, are default-shallow with no tags, so
`git log -1 --format=%cI` comes back empty there. **Add `fetch-depth: 0` to publish.yml's pack-job
checkout.** `actions/checkout` is already pinned to `3d3c42e5aac5ba805825da76410c181273ba90b1` — keep
it pinned (pin-actions-to-sha).

**The unavailable-stamp contract — defined, never silent.** Git absent, tagless or shallow: the target
**degrades and never fails the build**. `SourceRevisionId` unset, commit `"unknown"`, timestamp
attribute **not emitted** — never an empty string, which reads downstream as a real value. **A
checkpoint written by such a build records `commit = "unknown"`, `commit_timestamp = NULL`, version
from `VERSION`, and is never skipped.** D3 consumes exactly this.

**Do not break `scripts/version-bump.py`:** literal count-and-replace with an exact-count guard
(`:32-37`), hard-failing when the count differs. **Introduce no additional literal of the current
version into those three files** — a fourth `1.15.0` in the csproj makes the next bump exit with
"markers drifted?". Keep stamping in `GitStamp.targets`, which the script does not read.

### R3 — Tag and release automation

Exists for **traceable-releases**, not for the metrics. No metrics package depends on it.

**Trigger:** `on: push: { branches: [main], paths: ['VERSION'] }` — a native Actions path filter
firing exactly when the version changes. No diffing, no bot commit, no CI loop, no push to a protected
branch. **HYPOTHESIS:** confirm the `paths` filter syntax against current Actions docs.

**This resolves a sequencing defect.** Tagging on *publish* cannot stamp the binary it publishes:
publish.yml's `pack` job (`:34`, `dotnet pack` at `:70`) runs **before** the `publish` job (`:85`,
`needs: pack`, `environment: production` — the required-reviewer gate at `:89`) would create the tag,
so a 1.15.0 build would carry `v1.14.1-N-gSHA`. Tagging before the approval gate is worse — it tags
releases that then fail review. Tagging on main when VERSION changes puts the tag on the commit
*before* the manual publish dispatch, so at pack time `git describe` resolves to the real release tag.

**Hardening:** idempotent (if the tag exists, skip — **never force-push or move a tag**, which would
silently invalidate every checkpoint that recorded it); `permissions: contents: write` on **that job
only** (publish.yml declares workflow-level `contents: read` at `:26`); a concurrency group; VERSION
format validated in the workflow *and* in a test; release notes generated from merged PRs (one PR per
task, so PR titles are the changelog — do not hand-maintain a notes file). It **cannot disagree with
`version-bump.py`**: the workflow reads `VERSION`, the script writes it. One writer, one reader, one
file.

**Gates for the follow-up task:** single-source (re-add a csproj literal; watch the package version
stop tracking `VERSION`); malformed-VERSION (write `not-a-version`; must fail **loudly**, not produce
an empty version — this is the one that ships if missed); stamp-present (hardcode a fake sha);
CI-precedence (delete the `GITHUB_SHA` branch); stamp-fallback (make the fallback return the real sha,
watch the `"unknown"` test fail); shallow-checkout (remove `fetch-depth: 0`); tag idempotence (remove
the exists-check, watch it move the tag); least privilege (widen to workflow-level `contents: write`).

---

## 8. Follow-ups raised, deliberately not in scope

1. **`PromotionTools.List` whole-queue read is ungated** (`Tools/PromotionTools.cs:37-40` skips the
   gate when `projectId is null`). A real access defect, found while ruling 3.6. Own issue.
2. **`ToolMethodSizeTests.cs:75`** pins a literal `26` with a stale message. Derive from
   `RegisteredTools.Count` if it is one line; otherwise an issue.
3. **`TestData.Percentile`** (`TestData.cs:185-196`) has no test of its own yet is consumed by the
   shipping `ParityGateTests` (`:62`, `:67`, `:226`). WP0's `Statistics` does not replace it. Either
   test it or retire it in favour of `Core.Metrics.Statistics`.
4. **Per-phase CI budgets** — spec.json out-of-scope card S1, blocked on this feature's measurements.
5. **spec.json's G4 wording** still states the unimplementable form. Ruling 2 supersedes it; consider
   amending spec.json so the next reader does not implement the vacuous version.
