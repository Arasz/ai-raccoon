# Built-in .NET telemetry — what we could collect, and what is worth it

Date: 2026-08-09 · Companion to `docs/reviews/2026-08-09-otlp-export-review.md` ·
Target: .NET 10, HTTP serve mode (stdio never wires the exporter)

Every built-in `Meter` and `ActivitySource` the BCL, ASP.NET Core and `Microsoft.Extensions.Diagnostics.*`
ship, with what each actually tells us **about this server** — a SQLite-backed memory server with
outbound embedding/Blob calls and a two-route MCP HTTP surface. The point is to filter, not to
collect everything.

Already wired: meters `AiRaccoon.MemoryTools`, `AiRaccoon.PromotionQueue`, `System.Runtime`; source
`AiRaccoon.MemoryTools` (`OtlpExport.cs:31-39`).

## The short answer

Adopt two things, one line each, no package, no non-goal conflict:

- **`AddMeter("System.Net.Http")`** — how long embedding-endpoint and Azure Blob calls take and how
  often they fail. One meter covers both, because Blob traffic rides `HttpClient` underneath.
- **`AddSource("System.Net.Http")`** — the same calls as spans *nested under the existing tool span*,
  which turns "the tool call was slow" into "the embedding HTTP call was slow". Self-populating on
  .NET 10; the instrumentation package is only needed pre-.NET 9.

Then one genuine decision, which is the ASP.NET Core hosting source — see the join below. Everything
else is either gated on a feature we have not built, or answers nothing we would act on.

## The join: the hosting source is also the orphaned-span fix

The review found that every exported span carries a dangling `parent_span_id` (finding A7): the tool
span parents onto ASP.NET Core's `HttpRequestIn` Activity, which is created on every `/mcp` POST
whether or not anyone listens, and is deliberately never exported.

`AddSource("Microsoft.AspNetCore.Hosting")` would cause that parent to be recorded and exported under
the existing `AlwaysOnSampler`, turning today's orphaned one-span fragments into a proper
request span with tool spans nested underneath — **no other code change required**.

That is exactly the "Kestrel span per `/mcp` POST" that ADR-0002 and ADR-0009 name as a standing
non-goal. So the choice is real and belongs to the owner: either adopt the source (reopening the
non-goal, one line, fixes the trace shape), or keep the non-goal and fix the orphan the other way by
starting the tool span with an explicit empty parent. Both close A7; they differ in whether we want
request-level spans at all.

## Part 1 — Metrics

### `System.Runtime` — already adopted, keep

Automatic since .NET 9, no package. 18 instruments: `dotnet.process.cpu.time`,
`dotnet.process.memory.working_set`, `dotnet.gc.collections`, `dotnet.gc.heap.total_allocated`,
`dotnet.gc.last_collection.{memory.committed_size, heap.size, heap.fragmentation.size}`,
`dotnet.gc.pause.time`, `dotnet.jit.{compiled_il.size, compiled_methods, compilation.time}`,
`dotnet.thread_pool.{thread.count, work_item.count, queue.length}`,
`dotnet.monitor.lock_contentions`, `dotnet.timer.count`, `dotnet.assembly.count`,
`dotnet.exceptions` (carries `error.type`). No unbounded attributes.
[ref](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-runtime)

### `System.Net.Http` — **adopt**

Since .NET 8, shared framework, `AddMeter` alone.
[ref](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-system-net)

| Instrument | Type | Unit | Answers |
|---|---|---|---|
| `http.client.request.duration` | Histogram | s | How long do embedding/Blob calls take; how often do they fail (`error.type`, `http.response.status_code`) |
| `http.client.open_connections` | UpDownCounter | {connection} | Pooled/active outbound connections |
| `http.client.connection.duration` | Histogram | s | Connection lifetime |
| `http.client.request.time_in_queue` | Histogram | s | Are outbound calls queuing for a pooled connection |
| `http.client.active_requests` | UpDownCounter | {request} | Outbound calls in flight |

Cardinality: `server.address`/`server.port` — a handful of hosts here, not per-project.

### `Microsoft.AspNetCore.Hosting` — decision, see the join above

Shared framework, `AddMeter` alone. `http.server.request.duration` (Histogram, s, tags `http.route`,
`error.type`, `http.request.method`, `http.response.status_code`) and `http.server.active_requests`.
Two fixed routes, so no route cardinality risk.
[ref](https://learn.microsoft.com/aspnet/core/log-mon/metrics/built-in#microsoftaspnetcorehosting)

**Does it double-count `ai_raccoon_tool_duration_ms`? No.** Different windows, different tags: the
ASP.NET metric times the whole HTTP request at the hosting layer (routing, deserialisation, framework
overhead included; no `tool`/`project_id` tags; also covers `/observability`), while ours times only
the tool-execution body. Two legitimate views of largely the same request. The risk is dashboard
confusion, not duplicate series.

### `Microsoft.AspNetCore.Server.Kestrel` — skip unless the deployment changes

8 connection/TLS instruments (`kestrel.active_connections`, `kestrel.connection.duration`,
`kestrel.rejected_connections`, `kestrel.queued_connections`, `kestrel.queued_requests`,
`kestrel.upgraded_connections`, `kestrel.tls_handshake.duration`, `kestrel.active_tls_handshakes`).
On a single-client localhost server these answer nothing we would act on. Revisit if the HTTP surface
is exposed beyond localhost with concurrent clients, or terminates TLS in Kestrel.
[ref](https://learn.microsoft.com/aspnet/core/log-mon/metrics/built-in#microsoftaspnetcoreserverkestrel)

### `System.Net.NameResolution` — skip

`dns.lookup.duration` only. Useful if DNS is a suspected latency source; with our fixed small host
set, low information content.

### Gated on features we have not built — skip, not applicable

Verified absent from `src/` by grep, so these meters would emit **nothing** if registered:

- **`Microsoft.AspNetCore.Diagnostics`** — needs `UseExceptionHandler`/`IExceptionHandler`; we register
  neither. Adopting it is an error-response-shape decision first, a telemetry decision second.
- **`Microsoft.AspNetCore.RateLimiting`** — needs `AddRateLimiter`; not present.
- **`Microsoft.Extensions.Diagnostics.HealthChecks`** — needs `AddHealthChecks()` *and* the explicit
  `AddTelemetryHealthCheckPublisher()` opt-in (`AddMeter` alone is not enough). We have no health
  checks at all.

### `Microsoft.Extensions.Diagnostics.ResourceMonitoring` — skip

Separate NuGet package, versioned independently, and **marked experimental by Microsoft**. Most
instruments are container-scoped and inapplicable to a local process; the two process-scoped gauges
duplicate `System.Runtime`'s CPU/memory instruments. This is the same "ask if a simpler shape would
do" call ADR-0009 already made against `OpenTelemetry.Instrumentation.Runtime`. Revisit only if we
containerise and container-limit utilisation becomes a real question.

### Do not exist — stop looking for them

- **`System.Net.Sockets` and `System.Net.Security` have no Meters.** Microsoft states it plainly: as of
  .NET 8 only `System.Net.Http` and `System.Net.NameResolution` are instrumented with Metrics; the
  lower stack is EventCounters-only, which is not OTLP-exportable via `Meter`.
  [ref](https://learn.microsoft.com/dotnet/fundamentals/networking/telemetry/metrics#metrics-vs-eventcounters)
- **`System.Threading.Tasks.TplEventSource` is an EventSource, not a Meter** — consumed via
  `dotnet-trace`/ETW, no `AddMeter` equivalent.
- **`Microsoft.AspNetCore.HeaderParsing`** exists but ships in a standalone typed-header-parsing NuGet
  package we do not use; its metrics are a side effect of adopting that library, not a telemetry knob.
- Blazor, SignalR, Authentication/Authorization meters — subsystems we do not use.

## Part 2 — Traces

### `System.Net.Http` (`HttpRequestOut`) — **adopt**

One span per outbound `HttpClient` request. Because Azure SDK clients transport over `HttpClient`,
this also captures Blob traffic with no Azure-specific wiring. **Self-populating since .NET 9** — tags,
`Status` and `DisplayName` are filled by the runtime, so on .NET 10 we need **zero extra package**,
just `AddSource("System.Net.Http")`. Tags: `http.request.method`, `server.address`, `server.port`,
`url.full` (query redacted by default), `error.type`, `http.response.status_code`,
`network.protocol.version`. Volume is bounded by our own outbound calls, not by client traffic.
[ref](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-builtin-activities#http-client-request)

### `Microsoft.AspNetCore.Hosting` — the decision

`AddSource("Microsoft.AspNetCore.Hosting")` turns the span on. How populated its tags are depends on a
version boundary that matters to us:

**We are on the .NET 10 side of it.** The `AppContext` switch
`Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData` defaults to **`true`** through .NET 10 —
the hosting Activity carries **no** OTel HTTP semconv tags (no `http.route`, `http.request.method`,
`http.response.status_code`). It flips to `false` by default only in **ASP.NET Core 11**. Two ways to
get the tags now:

1. **Zero package** — `AppContext.SetSwitch("Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData", false)`
   at startup. One line, and it is what .NET 11 will do by default anyway.
2. **+1 package** — `OpenTelemetry.Instrumentation.AspNetCore`, which also covers Kestrel and adds
   enrichment hooks, at the cost of a fourth OTel package this codebase does not currently take.

Cost: exactly one span per inbound HTTP request — 1:1 with tool-call volume, since each Streamable
HTTP POST carries one JSON-RPC call — plus the `/observability` GET.
[ref](https://learn.microsoft.com/aspnet/core/breaking-changes/11/http-activity-otel-semconv)

### Experimental `System.Net.*` connection tracing — debugging only

`Experimental.System.Net.Http.Connections` (wait-for-connection, connection-setup),
`Experimental.System.Net.NameResolution` (DNS), `Experimental.System.Net.Sockets` (socket connect),
`Experimental.System.Net.Security` (TLS handshake). All .NET 9+, no package, one `AddSource` each.
Microsoft's own docs call them *"too verbose for use 24x7 in production scenarios with high
workloads"*. Adopt for a specific connection-latency investigation, then remove — not as standing
collection.

### Kestrel has no built-in span source

Only the meter (Part 1). The contrib package bundles "ASP.NET Core and Kestrel" together, consistent
with Kestrel having no independent built-in ActivitySource to subscribe to.

### `Azure.*` (Blob) — verify the name before wiring

`Azure.Core`-based clients (including `Azure.Storage.Blobs`, used by `AzureBlobCloudStore.cs`) are
OpenTelemetry-instrumented by the SDK itself, no separate package documented. **The exact
`ActivitySource` name is unverified** — the common convention is `AddSource("Azure.*")`, but that was
not confirmed against an official page, so check the emitted `Activity.Source.Name` before wiring.
Note that `AddSource("System.Net.Http")` already yields the HTTP leg of these calls; the Azure source
would add "which Blob operation" framing on top, and only matters when cloud sync is enabled.

## Part 3 — Ranked verdict

| # | Signal | Cost | Value here | Verdict |
|---|---|---|---|---|
| 1 | `System.Runtime` metrics | done | Process health | **Keep** |
| 2 | `System.Net.Http` metrics | 1 line | Embedding + Blob call latency and failure rate | **Adopt now** |
| 3 | `System.Net.Http` traces | 1 line | Same calls, nested under the tool span — per-invocation attribution | **Adopt now** |
| 4 | `Microsoft.AspNetCore.Hosting` traces | 1 line (+1 for semconv tags) | Fixes orphaned tool spans (review A7) | **Owner decision — reopens the non-goal** |
| 5 | `Microsoft.AspNetCore.Hosting` metrics | 1 line | Transport-level duration independent of tool timing; covers `/observability` | **Adopt if** transport-level visibility is wanted; cheaper and less contentious than #4 |
| 6 | `System.Net.NameResolution` | 1 line | Only if DNS is suspected | Skip |
| 7 | Kestrel metrics | 1 line | Single-client localhost — answers nothing actionable | Skip until deployment changes |
| 8 | Experimental `System.Net.*` traces | 1 line each | Too verbose for standing use, per Microsoft | Skip; ad hoc only |
| 9 | `Microsoft.AspNetCore.Routing` | 1 line | 2 fixed routes — near-zero information | Skip |
| 10–12 | Diagnostics / RateLimiting / HealthChecks | 1 line **+ the feature itself** | Emit nothing today | Not applicable |
| 13 | ResourceMonitoring | +1 experimental package | Container-scoped; duplicates `System.Runtime` | Skip |
| 14 | HeaderParsing | +1 library adoption | Not used | Not applicable |
| 15 | `Azure.*` traces | unverified | Only when sync is enabled; HTTP leg already covered | Skip / verify first |
| 16 | `System.Net.Sockets`, `System.Net.Security` | — | **No Meters exist** | N/A |
| 17 | Blazor / SignalR / Auth / YARP / DurableTask | — | Subsystems unused | N/A |
