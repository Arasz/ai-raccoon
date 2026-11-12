# ADR-0144: Measure the delay before MCP tool dispatch

Date: 2026-11-11

Status: Accepted for 2.7.0.

## Context

The server records the duration of each MCP tool call. It also records `search.total` and the search phases, including vector, FTS, fusion, adjustment, and snippets. The ASP.NET Core request Activity covers an HTTP request through response completion when tracing is enabled.

These measurements leave one gap in an HTTP call. The tool Activity starts when the MCP call filter runs. It does not show how much time passed between the request Activity start and that filter. A large delay there can point to request handling or MCP dispatch before tool service begins.

The request Activity and tool Activity also give the outer response boundary. Their durations, together with the dispatch delay and tool duration, let an operator estimate time outside the tool handler. This residual includes response encoding and transport work. It is not a direct measurement of serialization alone.

## Decision

Add an always-on middleware for HTTP `POST /mcp` requests. It stores a monotonic start timestamp on the current Activity. If no Activity exists, it creates a local request Activity for the duration of the call. The MCP call filter records `mcp.dispatch.delay_ms` from that timestamp to filter entry, with the tool's canonical project id. The middleware records `mcp.request.duration_ms` after the response completes, under the bank-wide self-metrics project. Both values appear in the local performance report. The existing `AiRaccoon.MemoryTools` meter also emits `ai_raccoon.mcp.dispatch.delay` and `ai_raccoon.mcp.request.duration` in seconds.

Do not record a zero when a request timestamp is absent. In that case the dispatch delay is unknown. This applies to stdio calls, which do not use this HTTP host.

The metrics have no caller-supplied tags. Their stored names and project ids are bounded. The existing search phases remain the measure of storage and ranking service. The request duration measures the HTTP pipeline through response completion. It is bank-wide because the middleware does not own a project identity.

The dispatch delay is not pure queue wait. It starts when the middleware enters and includes protocol parsing and any wait before the filter. It locates the pre-tool portion of an HTTP request without claiming which component owns it. The request duration minus dispatch delay and tool duration estimates work outside the tool handler, but does not isolate serialization from transport.

## Options

- Record no new value and ask operators to infer the pre-tool delay from spans. This keeps code unchanged but requires a trace collector and does not put the value in the local performance report.
- Add a queue or semaphore inside the memory store. No current measurement identifies such a queue, and this would change service behavior before evidence supports it.

## Consequences

- The local performance report can compare project-scoped dispatch delay and search service measurements with bank-wide request completion and tool duration.
- Traces retain the ASP.NET request span as the completion boundary when tracing is enabled.
- Client-side batch timing remains necessary to measure transport and caller-visible latency.
- No cache or concurrency limit follows from this measurement alone. A later change must target a stage that the measurements show is hot.

## Confirmation

A test advances a fake clock inside the middleware and confirms dispatch delay, request duration, and stored series. A no-timestamp call records no dispatch-delay sample. The source review confirms that the middleware wraps the complete `/mcp` pipeline.
