# Live parallel search timing on 1.59.4

Date: 2026-10-19 (Europe/Warsaw). This run measured caller-visible latency for concurrent `memory_search` requests against the live AiRaccoon bank after the 1.59.4 deployment.

## Environment and method

- CLI: `1.59.4+5176452fa1850cbaad9b05103f8de66210ebfba5`.
- Bank: `/Users/arasz/.ai-raccoon/memory.db`, approximately 1,005.4 MB; `ai-raccoon doctor` reported schema 19, HEALTHY, and no pending memory or code rows.
- Corpus: `memory_stats` reported 9,439 entries across shared, project, and custom contexts.
- Workload: same query in every call (`parallel vector search context partition performance response latency`), `scope=all`, `kind=memory`, `limit=5`; each call used a unique session ID.
- Load: three rounds each at concurrency 1, 8, and 20, for 87 successful searches. Wall time runs from dispatch of a batch to its final response. Request completion medians are calculated within each round.

## Results

| Concurrency | Round 1 batch | Round 2 batch | Round 3 batch | Mean batch wall time | Per-request median range |
|---:|---:|---:|---:|---:|---:|
| 1 | 738 ms | 774 ms | 633 ms | 715 ms | 633–774 ms |
| 8 | 5,667 ms | 5,917 ms | 5,842 ms | 5,809 ms | 2,991–3,036 ms |
| 20 | 14,217 ms | 13,699 ms | 14,572 ms | 14,163 ms | 6,758–7,709 ms |

All 87 searches returned successfully. Throughput stayed near 1.4 searches per second at all three concurrency levels. Caller-visible batch time therefore increased almost linearly with the number of simultaneous requests.

## Server telemetry

The server's `memory_performance` report for the surrounding 15-minute window contained 102 `memory_search` measurements, so it includes activity beyond these 87 calls. It reported search latency at 203 ms p50 and 386 ms p95. Within the same window, vector search measured 10.8 ms p50 / 13.8 ms p95, while FTS measured 175 ms p50 / 288 ms p95.

The server-side measurements are much shorter than the client-observed completion times under concurrency. The data points to queueing somewhere along the MCP request path, but does not identify whether that queue is in the MCP host, transport, or server. The vector phase itself remained fast in the aggregate telemetry.

## Interpretation and limits

The live end-to-end result does not show concurrent throughput scaling: increasing concurrency from 1 to 20 left throughput around 1.4 searches per second. This is materially slower than the 1.59.4 changelog's separate copy-bank benchmark (20 searches in 1.21 seconds on a 1 GB bank). These runs are not directly comparable: this measurement includes MCP dispatch and response delivery, and used the live bank and its current machine load.

There is no 1.59.3 measurement on this same live bank and MCP path, so this is not a before/after comparison. The report records current client-visible performance and the related server telemetry only; it does not establish that the 1.59.4 database change caused the queueing.
