# 0002 — OpenTelemetry observability for AiRaccoon

Date: 2026-08-04

Status: Accepted

## Context

Before this ADR, AiRaccoon had no observability surface. The 19-tool MCP server
(16 `memory_*` + 3 `watch_*`) exposed no metrics, no tracing, and no structured
telemetry — every call into `MemoryTools` was a black box. Operators had no way
to answer basic questions: how many calls per tool, how long do they take,
which tools fail most often.

The .NET BCL ships a production-grade observability foundation —
`System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource` — that
emits metrics and traces with zero third-party packages. `dotnet-counters`
reads these meters over EventPipe for local monitoring without any SDK or
exporter. The OpenTelemetry SDK is the natural next step once richer exporters
(OTLP, Azure Monitor) are needed, but it brings 5+ NuGet packages and
configuration surface that AiRaccoon does not need today.

The jsaa repo established the reference pattern: a `LlmCallMeter` class
wrapping a `Meter` with `Counter<long>` and `Histogram<double>`, registered as
a singleton and injected where needed. This ADR applies the same BCL-only
approach to AiRaccoon's MCP tool layer.

## Decision

**Wave 0: BCL observability only.** AiRaccoon will emit tool-call metrics and
traces using `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource`
— no OpenTelemetry SDK packages.

### Meter

A single `Meter` named `"AiRaccoon.MemoryTools"` exposes two instruments:

| Instrument | Type | Name | Unit | Tags |
|---|---|---|---|---|
| Call counter | `Counter<long>` | `ai_raccoon_tool_invocations` | `{call}` | `tool`, `result`, `error_type` |
| Call duration | `Histogram<double>` | `ai_raccoon_tool_duration_ms` | `ms` | `tool`, `result`, `error_type` |

Custom histogram buckets (milliseconds):
`1, 5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000`.
These cover sub-millisecond reads up to 30-second timeouts.

The `tool` tag carries the MCP tool name (`"memory_write"`, `"memory_search"`)
— exactly as surfaced to MCP clients. The `result` tag is `"success"` or
`"error"`. The `error_type` tag carries the exception type name when
`result` is `"error"`; it is absent when the call succeeds.

### ActivitySource (tracing)

An `ActivitySource` named `"AiRaccoon.MemoryTools"` creates one `Activity` per
tool call. Tags attached to the span:

| Tag | Value |
|---|---|
| `tool` | MCP tool name |
| `project_id` | Project identifier from the call |
| `result` | `"success"` or `"error"` |
| `error_type` | Exception type name when `result` is `"error"`; absent on success |

The `Activity` wraps the tool body in a `try`/`catch`: `SetStatus(Error)` on
failure, `SetStatus(Ok)` on success. This gives `dotnet-trace` and OTel
collectors a clean error/success signal per span.

### Instrumentation pattern

Instrumentation is centralized in the `ToolExecutionActivity` helper: one object
per tool call that starts the `Activity` (ctor: `tool` + `project_id` tags),
times the call, and records the invocation metric on success or error. Each
tool method is 3–5 lines:

```csharp
using var activity = new ToolExecutionActivity(observability, TnMemoryWrite, projectId);
try
{
    // ... tool body ...
    activity.RecordInvocation();
    return result;
}
catch (Exception ex)
{
    activity.RecordError(ex);
    throw;
}
```

`RecordInvocation()` marks the activity `Ok` + `result=success` and records the
metric; `RecordError(ex)` marks it `Error` + `error_type`/`result=error` and
records the error metric; `Dispose()` stops the activity. No decorator class, no
interceptor, no AOP — the helper is the single place the tags/status/metrics
contract lives, so the 22 tools cannot drift apart.

### ToolCallMetrics class

A dedicated `ToolCallMetrics` class owns the `Meter`, both instruments, the
`ActivitySource`, and a `Record` method that writes both metrics atomically
with a single `TagList`. The class implements `IDisposable` (disposes the
`Meter`) and exposes the `Meter` instance for test collectors.

### DI registration

`ToolCallMetrics` is registered as a singleton in `Dependencies.cs`.
`MemoryTools` receives it via constructor injection — no service locator, no
static state.

### Local monitoring

```bash
dotnet-counters monitor -n AiRaccoon AiRaccoon.MemoryTools
```

This reads live tool invocation counts and latency distributions over
EventPipe — no exporter, no collector, no configuration.

## Consequences

- **Observability from day zero**: every tool call is counted, timed, and
  traced with no runtime cost to set up.
- **No new NuGet dependencies**: the BCL provides everything needed for Wave 0.
- **`dotnet-counters` works immediately**: operators can watch live traffic
  during development and debugging.
- **Tracing is local-first but OTel-ready**: the `ActivitySource` produces
  standard `System.Diagnostics.Activity` spans. When the OTel SDK is added
  later, these spans are picked up by the SDK's `ActivityListener` with zero
  source changes — the BCL span *is* the OTel span.
- **The meter name is discoverable**: `dotnet-counters list` shows
  `AiRaccoon.MemoryTools` by name, and the OTel SDK's `.AddMeter(...)` call
  takes the same string.
- **The helper centralizes the contract**: `ToolExecutionActivity` is the single
  place tags/status/metrics are set (extracted 2026-08-06 from the inline
  pattern below), so the 22 tools cannot drift apart and adding another tool is
  the same 3–5 lines.

## Non-Goals (explicit)

These are deferred to future ADRs and **must not** appear in the Wave 0
implementation:

- **No OpenTelemetry SDK packages**: no `OpenTelemetry.Extensions.Hosting`, no
  `OpenTelemetry.Exporter.OpenTelemetryProtocol`, no
  `OpenTelemetry.Exporter.Console`.
- **No ASP.NET / HTTP auto-instrumentation**: AiRaccoon does not host an HTTP
  server. If it ever does, ASP.NET instrumentation is its own ADR.
- **No OTLP / gRPC export**: `dotnet-counters` and `dotnet-trace` handle local
  collection; the OTel Collector is a future concern.
- **No Azure Monitor export**: Application Insights / Azure Monitor exporter
  belongs in a hosted deployment ADR (MS3).

## Future evolution

1. **OpenTelemetry SDK adoption** (separate ADR): add
   `OpenTelemetry.Extensions.Hosting`, configure `.WithMetrics()` and
   `.WithTracing()` in the host, wire OTLP or Azure Monitor exporters. The
   existing `Meter` and `ActivitySource` are re-registered with
   `.AddMeter("AiRaccoon.MemoryTools")` and
   `.AddSource("AiRaccoon.MemoryTools")` — no changes to `ToolCallMetrics` or
   `MemoryTools`.

2. **ASP.NET Core auto-instrumentation**: if AiRaccoon later hosts HTTP
   endpoints, add `AddAspNetCoreInstrumentation()` — standard OTel setup,
   independent of tool metrics.

3. **`project_id` hashing**: the `project_id` tag on spans may contain
   user-supplied values. Before traces leave the process via OTLP, hash the
   value so no project identifier appears in plain text in an external
   collector.

4. **Helper extraction**: done 2026-08-06 — `ToolExecutionActivity` (see
   *Instrumentation pattern* above) is the shared helper; all 22 tools use it
   uniformly.

## Alternatives considered

### Full OTel SDK from the start

Rejected. The OTel SDK requires 5+ NuGet packages and an exporter
configuration to be useful. AiRaccoon is a local MCP server today — nobody is
running an OTel Collector for it. The BCL meters work with zero setup and
migrate to OTel with a one-line registration change later.

### ILogger-only approach

Rejected. Structured logging can answer "what happened" but cannot answer
"how many" or "how fast" without external log parsing infrastructure. Metrics
and traces are the right shape for these questions.

### AOP / decorator instrumentation

Rejected. A decorator wrapping every tool method adds a layer of indirection
for 3 lines of code — the cure is heavier than the disease. If this is
revisited, it belongs in the "helper extraction" future ADR.
