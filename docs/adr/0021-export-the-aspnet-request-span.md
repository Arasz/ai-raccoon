# 0021 — Export the ASP.NET request span

Date: 2026-08-09

Status: Accepted. Supersedes ADR 0002 §Non-Goals bullet 2 and ADR 0009 §Non-Goals bullet 1
("No ASP.NET / HTTP auto-instrumentation"). Retires ADR 0009's 2026-08-08 update block.

> ADR 0020 is the stdio→HTTP proxy decision and is unrelated; this one took 0021 to avoid a
> collision between two lanes writing at the same time.

## Context

ADR 0002 named ASP.NET/HTTP auto-instrumentation a non-goal — "Kestrel spans for every `/mcp` POST
… are their own decision, not a side effect of adopting the OTel SDK" — and ADR 0009 restated it
when it adopted OTLP export.

A four-lane review on 2026-08-09 (`docs/reviews/2026-08-09-otlp-export-review.md`) found that
**every exported span was an orphan**. ASP.NET Core creates a `Microsoft.AspNetCore.Hosting.HttpRequestIn`
Activity per request whether or not anyone listens; it is never recorded, and its source was
deliberately never registered. The tool span inherits it as parent, so each exported span carried a
`parent_span_id` the collector could never resolve — every trace a one-span fragment with a broken
link.

ADR 0009's 2026-08-08 update had already met the *sampling* half of this: the same unrecorded parent
made `ParentBased`'s local-parent-not-sampled branch return `AlwaysOff`, so `StartActivity` returned
null and no span existed at all. Its fix, `.SetSampler(new AlwaysOnSampler())`, made the tool span
record — but left it parented to something that would never be exported. The orphan is what remained
after that fix, not something new.

Two remedies were put to the owner:

- **A — root the tool span**, starting it with an explicit empty parent context. Keeps the non-goal
  and additionally lets the hardcoded sampler go, restoring `OTEL_TRACES_SAMPLER`.
- **B — register `AddSource("Microsoft.AspNetCore.Hosting")`**, so the parent is recorded and
  exported and the tool span nests under a real request span. Reopens the non-goal.

**The owner chose B**, on a dated review form (`docs/work/2026-08-09-otlp-export-feedback.md`, F5
answered "B"), and separately approved reopening the non-goal outright (T2). As with ADR 0009, the
provenance is the point: this reverses a standing architectural position **on explicit owner
instruction**, not on an agent's judgment. The implementation lane recommended A and said so; the
owner ruled B with that dissent on the record.

## Decision

Register the ASP.NET Core hosting ActivitySource so the request span is exported, and nest tool
spans under it.

### What gets exported

`Microsoft.AspNetCore.Hosting` joins the tracing registration, alongside the existing
`AiRaccoon.MemoryTools` source. One span per inbound HTTP request — 1:1 with tool-call volume, since
each Streamable HTTP POST carries one JSON-RPC call — plus the `/observability` GET from ADR 0008.

Both names live in `OtlpNames`, the derived registry, so the exporter and the `dotnet-trace` command
renderer read one list rather than two hand-kept copies.

### Semantic-convention tags: the AppContext switch, not a fourth package

On .NET 10 the hosting Activity carries **no** OTel HTTP semconv tags by default: the
`Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData` switch defaults to `true`, and flips
to `false` only in ASP.NET Core 11. Two ways to get the tags now:

- `AppContext.SetSwitch(…, false)` — one line, zero packages, and exactly what .NET 11 will do by
  default.
- `OpenTelemetry.Instrumentation.AspNetCore` — a fourth OTel package for a tag set the switch already
  produces.

**Take the switch.** This is the same call ADR 0009 made for `System.Runtime` over
`OpenTelemetry.Instrumentation.Runtime`: prefer the built-in when it already covers the ask. The
switch must be set before any ASP.NET Core hosting type is touched, so it goes at the top of the
web-host construction path, inside the OTLP opt-in gate — an unconfigured process never mutates
process-global state.

**Unverified, flagged rather than guessed:** whether the framework reads that switch lazily per
request or caches it at type initialisation was not confirmed. The ordering above is chosen to be
correct either way, but a test should pin that the tags actually arrive.

### Tool spans stay `ActivityKind.Internal`

The MCP semantic convention specifies `SERVER` for a server-side tool span. We deviate, deliberately.

With the request span now exported and also `SERVER`, two nested `Server` spans exist per tool call.
Backends that derive request rates by counting `Server` spans would report roughly **double** the
real rate, and APM "requests" views would show two entries per call. One server span per request is
the honest shape. **Owner ruling, 2026-08-09.** The rest of the MCP convention is adopted —
`mcp.method.name`, `gen_ai.tool.name`, the `{method} {target}` span name, `error.type`.

### The sampler stays until another lane's test says otherwise

`.SetSampler(new AlwaysOnSampler())` should become unnecessary under this ADR: once the parent is
recorded, `ParentBased` samples the root positively and the child inherits it, and removing the
override restores `OTEL_TRACES_SAMPLER`/`_ARG` as live configuration.

It is **held**, on a dependency outside this ADR. ADR 0009 justified the override, and the review
justified removing it, on the same premise: *this app propagates no incoming distributed trace
context*. The stdio→HTTP proxy (ADR 0020) makes that premise checkable rather than assumed — if the
proxy propagated `traceparent`, the request span would become the child of an unrecorded remote
parent and `ParentBased` would drop **every** server span, silently.

The proxy lane confirmed it propagates nothing — no tools, no exporter, no listener — and pinned it
with `ProxyWireE2ETests.ForwardedRequests_CarryNoTraceparent`, asserted on the wire against a real
backend with the real bare `ai-raccoon` process as the client. **That test is the check this decision
rests on.** It is named here rather than paraphrased so that anyone who later wires an exporter into
the proxy host meets this decision immediately instead of discovering it three layers down. Remove
the override once it is green post-merge on the proxy's integration branch; if it ever goes red, the
override stays and the remedy is revisited.

## Consequences

- **Positive.** Traces stop being one-span fragments with dangling parents. A tool call reads as
  request → tool, which is what an operator opens a trace to see.
- **Positive.** The fix costs one registration and one switch — no package, and it converges with
  ASP.NET Core 11's own default.
- **Negative.** One additional exported span per request, with its own attribute surface. Volume is
  bounded by request count, which for this server equals tool-call count.
- **Negative.** A standing non-goal from ADR 0002 is gone, and with it the position that the OTel SDK
  should describe only the tool layer. ADR 0002's Non-Goals list retains exactly one bullet after
  this ("No Azure Monitor exporter"), so **ADR 0002 moves from "partially superseded" to Superseded**,
  with that surviving non-goal restated in ADR 0009 rather than left to be inferred from a superseded
  document.
- **Neutral.** Deviating from the MCP convention on span kind is a documented, reasoned exception,
  not an oversight — see above.

## Non-Goals (still in force)

- **No `OpenTelemetry.Instrumentation.AspNetCore` package.** The switch covers it.
- **No HttpClient/outbound auto-instrumentation *by this ADR*.** `System.Net.Http` metrics and traces
  were approved separately (owner gate, T1) on their own merits — outbound client telemetry, not
  ASP.NET request instrumentation. Recorded here only so the two are not conflated.
- **No Azure Monitor exporter**, inherited from ADR 0002 and restated in ADR 0009.

## Alternatives considered

### A — root the tool span with an explicit empty parent

Rejected by the owner. It fixes the dangling parent with a one-line change to the single class that
owns span creation, keeps the non-goal, and would have let the sampler override go immediately
without waiting on another lane. Its cost is that traces carry no request-level span at all, so
per-request framework time — routing, deserialisation, everything outside the tool body — stays
invisible. The implementation lane recommended A; the owner chose B for the request-level visibility.
Recorded because the argument for A was sound and should not have to be rediscovered.

### `OpenTelemetry.Instrumentation.AspNetCore`

Rejected. A fourth OTel package for tags the `AppContext` switch already produces on .NET 10, and
which the framework produces by default from ASP.NET Core 11. Same reasoning ADR 0009 applied to
`OpenTelemetry.Instrumentation.Runtime`.

### Keeping tool spans as `SERVER` per the MCP convention

Rejected. Two nested `Server` spans per call double-count request rates in any backend that derives
them by counting span kind — a silent metric error that looks like traffic growth.
