# Read back performance metrics

Ask the running server how it is performing — no OpenTelemetry collector required.

---

## What this is, and what it is not

AiRaccoon records a measurement for every MCP tool call, every `memory_search` phase
(`search.open`, `search.embed`, `search.fts`, `search.vector`, `search.fusion`, `search.affinity`,
`search.adjustment`, `search.snippets`, `search.bump`), and one measured total per search (`search.total` — not itself
a phase; see [Reading `search.*` series](#reading-search-series) below), persists them in a
`metrics` table inside `memory.db`, and reads them back through the `memory_performance` MCP tool.
This is diagnostics for **you**, the person running or
developing AiRaccoon — not a replacement for
[the Meter/OTLP export path](monitor-and-export-telemetry.md), which is unchanged and keeps
exporting the same counters and spans to a collector when one is configured. Use this page when
there is no collector, or when you want a quick answer from inside an agent conversation instead
of standing one up.

## Call `memory_performance`

```json
{"tool": "memory_performance", "arguments": {"projectId": "acme"}}
```

`projectId` is the only required argument. With no other arguments the report covers the last 3
hours in 1-minute buckets (180 points):

```json
{
  "generatedAt": "2026-08-15T14:32:07Z",
  "window": "03:00:00",
  "bucket": "00:01:00",
  "bucketCount": 180,
  "series": [
    {
      "tool": "memory_search",
      "count": 42,
      "p50": 18.4,
      "p95": 61.2,
      "p99": 94.0,
      "min": 6.1,
      "max": 101.7,
      "buckets": [{"start": "2026-08-15T11:32:00Z", "count": 3, "average": 22.1}, "..."]
    },
    {
      "tool": "search.fts",
      "count": 42,
      "p50": 4.2,
      "p95": 9.8,
      "p99": 12.5,
      "min": 1.0,
      "max": 15.0,
      "buckets": ["..."]
    },
    {"tool": "memory_write", "count": 0, "p50": null, "p95": null, "p99": null, "min": null, "max": null, "buckets": []}
  ]
}
```

A few things worth knowing before reading a report:

- **The series list is fixed, not discovered.** It is every tool on the server's own tool
  inventory plus `SearchTimings.SeriesNames` — the nine `memory_search` phases and the measured
  `search.total` — never `SELECT DISTINCT name FROM metrics` — so a tool, phase or `search.total`
  nothing has called yet still appears, at `count: 0`, rather than being silently omitted.
- **A quiet window is an empty series, never an error.** Asking about a bank with no traffic in
  the requested window is a well-formed answer ("nothing happened"), not a failure.
- **The report is project-scoped.** It covers only the `projectId` you pass; there is no
  whole-bank view yet (deferred — see
  [the implementation plan](../plans/2026-08-15-performance-metrics-implementation.md), item D6).
- **`windowMinutes` / `bucketMinutes`** override the defaults. A `bucketMinutes` wider than
  `windowMinutes` is never rejected — it clamps to the window and the series returns one averaged
  point covering the whole thing.
- **`search.*` series carry no correlation id of their own in this report** — they are aggregated
  durations, not individual events. Correlating a specific slow search with `search_quality`'s
  usefulness data needs the raw `metrics` rows (`query_hash`, `correlation_id` columns), which this
  tool does not expose directly.

## Reading `search.*` series

Four things a reader can get wrong about the phase series, each because the shape of the data
looks like something it is not:

1. **A window spanning the day `search.open`/`search.embed`/`search.total` were added will show
   them at a lower count than `search.fts`.** Rows recorded before the change never wrote those
   three names, and `PerformanceReportBuilder` reports a series with no samples in the window as
   `count: 0` with null percentiles and empty buckets — it does not backfill, because an old row
   does not know its own bank-open or embed duration. Sum-versus-total arithmetic (`Σ phases` vs.
   `search.total`) is only valid where `search.total`'s count in the window equals `search.fts`'s;
   a count mismatch is the honest signal that the window straddles the change, not a defect in
   either series.
2. **`search.total` is not `memory_search`.** `search.total` brackets only
   `SqliteMemoryStore.SearchAsync`; `memory_search`'s own reported duration additionally covers the
   access gate, the query guard, the post-search `search_quality` write, envelope shaping and MCP
   SDK dispatch. The two will not agree, and that gap is expected — see
   [ADR-0080](../adr/0080-the-phases-close-against-search-total-not-the-tool-total.md) for why it
   cannot be closed from inside `SearchAsync`. Sum the phases against `search.total`, never against
   `memory_search`.
3. **`search.open` covers only `SearchAsync`'s own bank open**, not the query guard's separate one
   (a single settings-prefix read, evaluated before `SearchAsync` is even called). Connection
   dispose / return-to-pool happens at method exit, after the `search.total` bracket has already
   closed, so it is in neither `search.open` nor `search.total`.
4. **`search.embed` reads near zero on a bank with no embedding engine configured** — that is
   `EntryEmbedder` returning null right after its own settings read, not evidence that embedding is
   cheap. On a bank *with* an engine configured, the same span also covers the ONNX session build
   on its first use, so the very first search after server startup can show as an outlier in this
   series without anything being wrong.

## What is not read back yet

Some of what the owner-elicited spec describes is deferred past this first phase — see
`docs/work/specs/PerformanceMetrics.feature` and
[the implementation plan](../plans/2026-08-15-performance-metrics-implementation.md) §5 for the
full list and why each is safe to defer:

- No `ai-raccoon performance` CLI verb — `memory_performance` (the MCP tool) is the only read
  surface today.
- No checkpoint rollups — the `metrics` table holds a rolling window (default 28 days,
  best-effort), not a year of summarized history pinned to a release commit/version/timestamp.
- No whole-bank (cross-project) scope.

## Tuning the writer

The buffer capacity, flush interval and hot-table retention are settings-table rows with defaults
— see [Configure and run the AiRaccoon server](configure-ai-raccoon-server.md#self-instrumentation-metrics)
for the keys and how (not yet) to change them.

## Related documentation

- [Monitor and export server telemetry](monitor-and-export-telemetry.md) — the Meter/OTLP path,
  unchanged and complementary
- [Architecture: Performance metrics](../explanation/architecture.md#performance-metrics) — the
  data flow from hot path to table to report
- [ADR-0074](../adr/0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-g4.md) — why the
  writer is a capped buffer, not a channel
- [ADR-0080](../adr/0080-the-phases-close-against-search-total-not-the-tool-total.md) — why the
  phases close against `search.total`, not `memory_search`
- [docs/reference/agent-memory-server.md](../reference/agent-memory-server.md#tools-27) — the full
  `memory_performance` tool contract
