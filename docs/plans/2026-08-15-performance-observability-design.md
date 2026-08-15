# Performance observability — what exists, what is missing, and the design

Date: 2026-08-15 · Base commit `02f2512e` · Owner request: *"do we measure and emit metrics for
search performance? Or any performance inputs? If not, design and plan the store and tools + CI
interface that will return collected performance characteristics."*

## The short answer

**Yes for tool-level latency. No for anything inside the store, and no for reading it back.**

Three things exist, one CI gate guards a single path, and the two gaps that matter are that nothing
times the *phases* of a search and nothing can *answer* a question about performance without an OTLP
collector standing by.

---

## What exists — measured, at `path:line`

| | What it records | Where it goes |
|---|---|---|
| `ToolCallMetrics` (`src/AiRaccoon/Observability/ToolCallMetrics.cs:21-38`) | per-invocation **count** and **duration histogram** in seconds, tagged `tool` / `result` / `project_id` / `error.type` | `Meter` "AiRaccoon.MemoryTools" → `dotnet-counters`, and OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| `BackgroundTelemetry` (`:32-43`) | background pass **count** and **duration** | same |
| `PromotionQueueMetrics` (`:26-50`) | evictions, evicted score, wait seconds, promoted, discarded, pruned, failures | same |

The duration histogram already uses the bucket boundaries the MCP semantic convention prescribes for
`mcp.server.operation.duration`, so `memory_search` and `memory_write` latency **is** captured per
call and is exportable today. Recording is a `CallToolFilter` (`ToolTelemetry.cs:44`), so it covers
every tool uniformly rather than per tool by hand.

**In CI:** `ParityGateTests` asserts a **p95 latency budget** for one managed query at corpus size
(`:23`, `:62-63`), and `WritePerformanceBenchmarkTests` covers write throughput.

## The three gaps

**1. Nothing times anything inside the store.** `grep -rn "Stopwatch" src/AiRaccoon.Infrastructure/`
returns **nothing**. A `memory_search` is one opaque number covering FTS5, the vector KNN, RRF fusion,
source-affinity ranking, snippet resolution and the access bump. When p95 moves, there is no way to
say which phase moved — and WP5's own decision (drop the `ctx` partition key, +1.6 ms per KNN measured
on a synthetic shape) is exactly the kind of change whose effect this cannot see in production.

**2. Nothing can answer a question about performance.** Metrics leave the process or they are lost.
`search_quality` looks like it might hold this and does not — its columns
(`MemorySchema.cs:302-316`) are `result_count`, `top_source_files`, `follow_through_*`,
`usefulness_grade`. **There is no duration column.** An agent, an operator on a laptop, or a CI job
with no collector has no way to ask "how is this bank performing?".

**3. The CI gate is one query on one path.** A p95 budget on the managed query is real but narrow: no
per-phase budget, no write budget in the same gate, and no record of the number across runs — so a
5% regression per release is invisible until it becomes a 50% one.

---

## Design

Three layers, each independently useful, each shippable alone.

### Layer 1 — the store reports its phases

A `SearchTimings` record carried out of `SqliteMemoryStore.SearchAsync` alongside the results:

```
fts, vector, fusion, affinity, snippets, bump   (each a TimeSpan)
```

Measured with `TimeProvider.GetTimestamp()` (the store already takes `TimeProvider`, so no new
dependency and it stays fake-clock testable). A parallel `WriteTimings` covers chunk, embed, insert.

**Why on the result and not straight to a Meter:** a Meter write is fire-and-forget and unreadable
in-process. Returning the timings lets layer 2 aggregate them *and* layer 1 stay a pure value — the
store gains no observability dependency, which keeps `mcp-thin`'s sibling rule intact for the
infrastructure layer.

### Layer 2 — a rolling in-process summary, readable back

`IPerformanceSnapshot` in Core, one implementation holding a bounded ring buffer per operation:

```
count, p50, p95, p99, max, and the phase breakdown for the same window
```

Bounded and in-memory — no new table, no growth, nothing to reap. It is a *snapshot*, not a history:
the durable history is OTLP's job and already works.

Fed by the existing `ToolTelemetry` filter (which already sees every call and its duration) plus the
phase timings from layer 1.

### Layer 3 — the interfaces that return it

| Surface | Shape | Why |
|---|---|---|
| **MCP tool** `memory_performance` | `{ operation, count, p50, p95, p99, phases }` per operation | an agent can notice its own bank is slow and say so; read-only, `AccessRequirement.Read` |
| **CLI** `ai-raccoon performance` | the same snapshot, formatted | an operator with no collector; sits beside `serve observability` which already exists |
| **CI** | assert per-phase budgets on the parity corpus **and write the numbers to the run summary** | turns the single p95 into a per-phase gate, and makes the trend visible run over run |

The CI leg is the one that changes behaviour over time: a budget that only fails on catastrophe still
lets 5%-per-release rot through. Emitting the numbers into the job summary makes the drift readable
without anyone building a dashboard.

---

## Sequencing, and the gate for each

1. **Layer 1** — timings on the result. *Gate:* a test asserting the phases sum to within a tolerance
   of the whole, and that a deliberately slowed phase (inject a delay in the fake `TimeProvider`)
   shows up in *that* phase and not another. Watch it fail by attributing the delay to the wrong phase.
2. **Layer 2** — the snapshot. *Gate:* feed a known distribution and assert p50/p95 exactly; watch it
   red with an off-by-one percentile. **A percentile function is the classic place a gate passes on
   any input** — assert against a hand-computed distribution, not against itself.
3. **Layer 3a** — the tool and CLI. *Gate:* the tool appears in the derived tool inventory (26 → 27,
   which `RegisteredTools` will force) and returns a populated snapshot after a known number of calls.
4. **Layer 3b** — the CI budgets. *Gate:* **break each budget deliberately and watch it go red**, one
   at a time. A per-phase budget nobody has seen fail is the vacuous-gate failure this campaign has
   already hit twice.

**Do layer 1 and 2 before 3.** A tool that returns an empty snapshot is worse than no tool.

## Open questions for the owner

1. **Is the in-process snapshot enough, or should durations persist?** A `search_quality.duration_ms`
   column is one migration and makes latency queryable next to usefulness — attractive, because it
   would let "slow searches" and "unhelpful searches" be correlated for the first time. It also grows
   a table this campaign just had to add a reaper to. **Recommendation: start in-memory; add the
   column only if the correlation is actually wanted.**
2. **Should `memory_performance` be project-scoped or bank-wide?** Bank-wide is more useful and leaks
   cross-project timing shape. Recommendation: project-scoped by default, bank-wide behind the same
   `full` gate the other cross-project surfaces use.
3. **What are the budgets?** They must be *measured on this corpus first*, then pinned with a raise
   history — not guessed. That measurement is the first task of layer 3b, not an input to it.

## What this deliberately does not propose

- **No new dependency.** No BenchmarkDotNet in the product, no second metrics stack. `System.Diagnostics.Metrics`
  and `TimeProvider` are already here and already exported.
- **No sampling or tracing changes.** OTLP already carries spans; this is about what the process can
  answer on its own, not about replacing the collector.
- **No storage growth by default** — see open question 1.
