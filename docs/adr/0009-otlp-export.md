# 0009 — OTLP export

Date: 2026-08-07

Status: Accepted. Supersedes the "No OTLP / gRPC export" non-goal of ADR 0002.

## Context

ADR 0002 deliberately stopped at BCL-only `System.Diagnostics.Metrics` and
`System.Diagnostics.ActivitySource`, and named OTLP export as a non-goal:
"`dotnet-counters` and `dotnet-trace` handle local collection; the OTel
Collector is a future concern." That was the right Wave 0 scope — nobody was
running a collector for a local MCP server.

The owner has now explicitly reversed that call: AiRaccoon should be able to
export its existing meters and traces to an OTLP collector. **This ADR
records that reversal on explicit owner instruction, not as an agent's
architectural judgment** — the provenance of the decision is the point of
writing it down, not just the decision itself. The two BCL instruments this
builds on (`AiRaccoon.MemoryTools` — Meter + ActivitySource — and
`AiRaccoon.PromotionQueue` — Meter only, added by ADR 0007) already exist
and needed no change to become exportable; ADR 0002's "the BCL span *is* the
OTel span" bet is what makes this ADR a thin addition instead of a rewrite.

## Decision

Adopt the OpenTelemetry SDK and export metrics and traces over OTLP,
opt-in, gated on `OTEL_EXPORTER_OTLP_ENDPOINT` being set.

### Packages

Three packages, pinned to `1.17.0` via central package management in the
root `Directory.Packages.props`: `OpenTelemetry`,
`OpenTelemetry.Extensions.Hosting`,
`OpenTelemetry.Exporter.OpenTelemetryProtocol`. `tests/Directory.Packages.props`
is a standalone file (it does not import the root) and separately gains
`OpenTelemetry.Exporter.InMemory` for test collectors.

### What gets exported

Both application instruments: the `AiRaccoon.MemoryTools` Meter and
ActivitySource (ADR 0002), and the `AiRaccoon.PromotionQueue` Meter (ADR
0007) — which has no ActivitySource, only counters/histograms/an observable
gauge. An exporter wired to pick up meters by name and silently drop the
promotion-queue one would be a real observability gap, not a small
oversight, so both meter names are registered explicitly with
`.AddMeter(...)`. The built-in `System.Runtime` Meter is exported alongside
them — see ".NET runtime metrics" below.

**Known cost, accepted:** two meters tag `project_id`. `PromotionQueueMetrics`
tags four of its seven instruments — the queued, eviction, promoted and
discarded counters (`RecordQueued`, `RecordEviction`, `RecordPromoted`,
`RecordDiscarded`); the wait-seconds histogram and the capacity-utilization
gauge are untagged. `ToolCallMetrics` tags its `ai_raccoon_tool_invocations`
counter (2026-08-08; the duration histogram stays untagged — see ADR 0002).

The two carry different costs, and the difference matters more than the
similarity. On the queue counters `project_id` is the *only* tag, so N
projects cost N series. On the tool counter it multiplies `tool` (22) ×
`result` × `error_type`, so each project costs tens of series rather than
one. The tool counter is also incremented on the error path, and the access
gate that rejects unknown or unauthorized project ids runs *inside* the
timed block — so a rejected call still mints a series under the id the
caller supplied. `memory_share_extract` is deliberately exempt: it tags the
counter with the sentinel `multi` rather than its comma-joined project list,
which would have been combinatorial rather than linear.

`project_id` is unbounded, so every
distinct project becomes its own metric time series. That was free while
collection was local-only over EventPipe, which is the context in which the
cardinality was originally accepted; exporting changes the cost profile,
because hosted collectors generally bill per series. The tag stays — a
promotion-queue metric without the project dimension answers almost nothing —
but an operator pointing this at a paid backend should know the series count
grows with their project count. This is a *separate* question from the
plaintext/hashing one settled below: cardinality is about cost, plaintext is
about disclosure, and only the latter was superseded by this ADR.

### .NET runtime metrics (GC, memory, CPU) — in scope; ASP.NET auto-instrumentation stays out

The owner asked for GC/memory/CPU telemetry alongside the tool metrics, while
keeping ADR 0002's "no ASP.NET/HTTP auto-instrumentation" non-goal in force
(no Kestrel span per `/mcp` POST). These are two different instrumentation
surfaces and this ADR treats them differently:

- **`System.Runtime` — the built-in runtime Meter — is exported.** Per
  Microsoft's own reference
  ([.NET runtime metrics](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-runtime)),
  the `System.Runtime` Meter "reports measurements from the GC, JIT,
  AssemblyLoader, Threadpool, and exception handling portions of the .NET
  runtime as well as some CPU and memory metrics from the OS" and is
  "available automatically for all .NET apps" starting in .NET 9 — no
  extra package. It covers exactly the ask: `dotnet.process.cpu.time`,
  `dotnet.process.memory.working_set`, `dotnet.gc.collections`,
  `dotnet.gc.heap.total_allocated`, `dotnet.gc.pause.time`,
  `dotnet.thread_pool.*`, and more. Wiring it is one line —
  `.WithMetrics(m => m.AddMeter("System.Runtime"))` alongside the existing
  `AddMeter("AiRaccoon.MemoryTools")` /
  `AddMeter("AiRaccoon.PromotionQueue")` calls — and, like the rest of this
  ADR, it costs nothing when `OTEL_EXPORTER_OTLP_ENDPOINT` is unset,
  because the SDK is never built.
- **`OpenTelemetry.Instrumentation.Runtime` (`.AddRuntimeInstrumentation()`)
  is NOT added.** This is a fourth, separate NuGet package (seen in
  Microsoft's own Aspire `ServiceDefaults` template and Azure Monitor
  onboarding docs) that predates .NET 9's built-in `System.Runtime` Meter
  and instruments the same GC/thread-pool/JIT surface through its own
  meter. Its documentation was not fetched in this research pass, so
  whether its instrument names fully duplicate `System.Runtime`'s or add
  something distinct is **unverified** — flagged rather than guessed.
  Given that `System.Runtime` alone already covers the owner's stated ask
  (memory, CPU, GC) with zero added packages, the simpler shape is taken
  per this project's "ask if a simpler shape would do" invariant: no fourth
  package is added on the strength of an unverified overlap. Revisit only
  if a real gap in `System.Runtime`'s coverage shows up in practice.
- **ASP.NET Core auto-instrumentation remains a non-goal**, unchanged from
  ADR 0002 — see Non-Goals below. Runtime metrics describe the process; HTTP
  auto-instrumentation would describe every request, which is the thing
  ADR 0002 explicitly deferred and this ADR does not revisit.

### Opt-in, and why

If `OTEL_EXPORTER_OTLP_ENDPOINT` is unset, the OpenTelemetry SDK is never
built: zero background threads, zero sockets, zero cost. This also makes
the `observability otlp` CLI verb's "not enabled" answer a fact read from
whether the SDK exists, not a guess about whether a collector happens to be
reachable.

### Configuration channel: standard `OTEL_*` environment variables only

No settings-table option, no CLI flag. Three reasons, in order of weight:

1. **Startup ordering.** The exporter has to be wired at host-build time, but
   the settings bank is not proven decryptable until *after* the host is
   built (`TryProbeBankDecryption` in `ServeRunner.cs` runs post-build).
   Reading OTLP configuration from the bank would invert that order.
2. **No reimplementing the SDK's own parsing.** Endpoint, protocol, headers,
   and timeout are already parsed by the OpenTelemetry SDK from `OTEL_*`
   variables; duplicating that parsing on top of the settings bank buys
   nothing.
3. **Secrets stay out of the bank.** `OTEL_EXPORTER_OTLP_HEADERS` is how
   collector API keys travel; that must never land in a persisted settings
   table.

A `serve --otlp-endpoint` CLI option was rejected on the same grounds as the
settings-table channel, plus one more: it would duplicate a single standard
variable while covering exactly one of the roughly ten knobs `OTEL_*`
already exposes (endpoint, protocol, headers, timeout, per-signal overrides,
...).

> **2026-08-07 update.** Reason 2 above claimed the SDK "already parsed"
> endpoint, protocol, headers, and timeout from `OTEL_*` — that was not true
> in the running process. `McpServerSetup.CreateWebHost`'s
> `builder.Configuration.Sources.Clear()` (Ruling 3) emptied the exact
> `IConfiguration` the SDK's own `OTEL_*` parsing binds through, so only the
> values this codebase set explicitly in C# (`OtlpExport.ConfigureExporter`'s
> `Endpoint`/`Protocol`/`TimeoutMilliseconds`, and, after #109, the
> hand-parsed `OTEL_METRIC_EXPORT_INTERVAL`/`_TIMEOUT`) ever reached the SDK.
> `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_COMPRESSION`, and
> `OTEL_RESOURCE_ATTRIBUTES` were silently dropped — a collector requiring an
> `Authorization` header returned 401 with no visible failure (ADR 0009's own
> "Failure posture" section, below), and `serve observability otlp` kept
> reporting "enabled" throughout, because it reads config state, not exporter
> health.
>
> `CreateWebHost` now re-admits only `OTEL_`-prefixed environment variables
> into the cleared configuration before building the host — every process
> environment variable starting with `OTEL_` is copied into an in-memory
> configuration source, nothing else. This restores the SDK's entire
> `OTEL_*` parsing surface in one place (headers, compression, resource
> attributes, ~~per-signal endpoint/protocol/timeout~~ [see correction below], mTLS certificates,
> `OTEL_TRACES_SAMPLER`/`_ARG`, `OTEL_BSP_*`, metrics temporality) while
> Ruling 3 stays exactly as strict as before: no non-`OTEL_` variable
> re-enters config, so the settings table remains the only runtime channel
> for everything else.
>
> `OtlpExport.ConfigureExporter` still sets `Endpoint`/`Protocol`/
> `TimeoutMilliseconds` explicitly — those are product decisions
> (`SignalEndpoint`'s http/protobuf path composition, and a 5s timeout
> ceiling lower than the SDK's own default), not config-parsing gaps. The
> hand-rolled `OTEL_METRIC_EXPORT_INTERVAL`/`_TIMEOUT` reads added by #109
> (`OtlpExportState.MetricExportIntervalMilliseconds`/
> `MetricExportTimeoutMilliseconds`, `OtlpExport.ConfigureMetricReader`) are
> deleted — the SDK reads them correctly through the restored channel now,
> and duplicating that parsing was exactly what reason 2 above always meant
> to avoid.
>
> `service.name` also changes, deliberately: `OtlpExportState.ServiceName`
> (added by #107 to read `OTEL_SERVICE_NAME` by hand, for the same
> cleared-config reason) is removed. `service.name` is now a fixed product
> identity — `OtlpExportState.DefaultServiceName`, `"ai-raccoon"` — set via
> `.ConfigureResource(r => r.AddService(OtlpExportState.DefaultServiceName))`
> unconditionally. This is registered after
> `ResourceBuilder.CreateDefault()`'s own environment-variable detector, and
> resource-builder actions merge later-registered-wins, so the explicit call
> still wins even now that `OTEL_SERVICE_NAME` reaches that detector too —
> confirmed by test
> (`OtlpExportTests.HttpHost_ServiceName_StaysAiRaccoon_EvenWhenOtelServiceNameIsSet`),
> not assumed. `OTEL_SERVICE_NAME` can therefore no longer rename the
> reported service; #107's actual goal — no more `unknown_service:<process>`
> — is unaffected, since that goal never depended on honoring the override.

> **2026-08-08 update (issue #181).** Traces silently never reached the collector in HTTP serve
> mode, even though metrics did: `StartedHttpHost_AttachesATraceListener_SoToolCallsProduceSpans`
> only proved `ActivitySource.StartActivity` returns non-null outside a real request — it never
> ran inside one. Handling a real MCP tool call, ASP.NET Core's hosting layer creates its own
> `Microsoft.AspNetCore.Hosting.HttpRequestIn` `Activity` per request — unrelated to and
> unaffected by the "no ASP.NET/HTTP auto-instrumentation" non-goal above, since it is created
> independent of any OTel listener — and that Activity becomes `Activity.Current`, the ambient
> parent for every tool-call span. It is never recorded (`Recorded = false`). The SDK's default
> `ParentBased(AlwaysOnSampler)` sampler's local-parent-not-sampled branch is `AlwaysOffSampler`,
> so `ToolCallMetrics.ActivitySource.StartActivity` returns **null** for every tool call made
> inside a real request — confirmed directly (`Activity.Current` logged as `HttpRequestIn`,
> `Recorded=False`, and the returned Activity was `null`), not inferred from the OTLP payload's
> absence alone.
>
> `OtlpExport.AddOtlpExport` now calls `.SetSampler(new AlwaysOnSampler())` on the tracing
> builder. This corrects the "Configuration channel" 2026-08-07 update's claim above that
> `OTEL_TRACES_SAMPLER`/`_ARG` reach the SDK through the restored `OTEL_*` config channel: an
> explicit `.SetSampler(...)` call always wins over the SDK's own env-var-driven sampler
> resolution, so those two variables no longer have any effect. That claim was never covered by
> a test and does not survive this fix. Accepted because `AiRaccoon.MemoryTools` is the only
> registered source and this app propagates no incoming distributed trace context — there is no
> genuine remote-parent case for a configurable sampler to arbitrate.

### Which host paths get the exporter

| Host path | Exporter wired? | Why |
|---|---|---|
| `CreateWebHost` (`serve`, HTTP) | Yes | The long-lived process an operator actually wants to watch. |
| `CreateAppHost` (stdio) | **No** | A stdio server is a per-connection process that recycles roughly every 5 minutes — the recycle `serve` mode exists to avoid. Against that lifetime the exporter's 5 s batch schedule delay plus the non-configurable 5 s per-provider shutdown grace is mostly overhead, and much of what it buffers would never flush. Owner decision, 2026-08-07, reversing an earlier symmetric call. |
| `CliCommandRunner` (one-shot CLI verbs) | No | A one-shot command would either exit before the batch exporter flushed, or block shutdown waiting for a flush that serves no one. |
| `ObservabilityRunner` (the new `observability` verb) | No | Same reasoning as `CliCommandRunner` — it queries a running server, it does not itself need to be observed. |

### `project_id` export — plaintext; ADR 0002 §Future evolution item 3 retired

ADR 0002 deferred a decision: "before traces leave the process via OTLP,
hash the value so no project identifier appears in plain text in an external
collector." **That item is retired, not implemented.** `project_id` is
exported over OTLP exactly as it appears on the span today — in plaintext —
and no hashing processor is added to the tracing pipeline.

The tag surface, confirmed in code: `ToolExecutionActivity`
(`src/AiRaccoon/Observability/ToolExecutionActivity.cs`) tags spans with
`tool`/`project_id`/`result`/`error_type`; `ToolCallMetrics.RecordInvocation`
(`src/AiRaccoon/Observability/ToolCallMetrics.cs`) tags the
`ai_raccoon_tool_invocations` counter with `tool`/`result`/`error_type`/
`project_id` and the `ai_raccoon_tool_duration_ms` histogram with
`tool`/`result`/`error_type` only — the histogram is the one instrument with
no `project_id`, kept off it for cardinality reasons (ADR 0002), not for
disclosure reasons. Everywhere `project_id` does appear — the span and the
invocation counter — it is plaintext, hashed or not.

Reasoning, in order of weight:

1. **Consistency — the decisive argument.** `project_id` already leaves the
   process in plaintext today, unconditionally, on every build: at least 10
   `[LoggerMessage]` templates write it to stderr, which lands in
   `serve.log` under the README's own
   `ai-raccoon serve > serve.log 2>&1 &` pattern. Two examples, verified in
   this worktree: `PromotionQueueService.Log.Proposed`
   (`src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:146`,
   `"Propose for {ProjectId}: ..."`) and
   `ExtractionHostedService.Log.Pass`
   (`src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs:179`,
   `"Extraction pass for {ProjectId} ({Mode}): ..."`); the full set spans
   `PromotionQueueService.cs`, `ExtractionHostedService.cs`,
   `WatchEventSource.cs`, `WatchDigestExecutor.cs`, and `Dependencies.cs`.
   Hashing one *opt-in* export channel while an *always-on* log channel
   prints the same value in plaintext is security theater, not a real
   control.
2. **The protection would have been illusory.** An unsalted digest of a
   short, guessable project slug (`ai-raccoon`, `jsaa`, `hermes-default` —
   real values from this bank) is reversible by dictionary in milliseconds.
   It would have defeated casual shoulder-reading and nothing else.
3. **It would destroy the tag's purpose.** `project_id` exists on the span
   and on the invocation counter so an operator can filter and group by
   project in the collector — it is what makes "how many writes did project
   X do" answerable at all. An
   opaque digest is unusable without an out-of-band mapping back to the
   real project — and keeping that mapping anywhere reintroduces the
   exposure it was meant to remove.
4. **Scope of what is actually exposed stays narrow either way.** The value
   is a scope *name*, not content: the span carries `tool`, `result`,
   `error_type`, and duration alongside it, never entry text, search
   queries, or file contents. A collector reader learns "project X called
   `memory_search` 40 times, 2 errored" — never what was searched, written,
   or stored.

**Residual risk, stated plainly and assigned to the operator.** Pointing
`OTEL_EXPORTER_OTLP_ENDPOINT` at a shared team or third-party vendor
collector makes project names visible to whoever reads that backend. A
project id that is itself sensitive (a client name, an unreleased codename)
should not be used if OTLP export to such a collector is enabled. This
residual risk is also recorded in `SECURITY.md`, in the threat-model table
and its "What leaves the process when OTLP export is on" section — that is
the canonical statement of it; this ADR records the decision, that document
records the operator-facing risk.

**Where a redaction switch would go, if it is ever needed.** An opt-in
`BaseProcessor<Activity>` in the OTLP tracing pipeline is the correct
location for hashing or dropping `project_id`, should a real consumer ever
need it — it is the one point that affects only what leaves the process
over OTLP without touching what a local `dotnet-trace` session sees on the
same span. It is deliberately **not built now**: per this project's "ask if
a simpler shape would do" invariant, a redaction switch with no caller
asking for it is a cost with no buyer, and reasons 1–3 above mean it
wouldn't buy much even if a caller did ask.

### Failure posture with no collector reachable

This is measured against the opentelemetry-dotnet SDK sources, cited as
such rather than guessed:

- Exporter connections are lazy — nothing blocks host startup waiting for a
  collector to answer.
- Internal exporter errors go to an `EventSource`; self-diagnostics is OFF
  unless an `OTEL_DIAGNOSTICS.json` file is present. A dead collector
  therefore produces **no stderr output at all** by default. This matters
  here specifically: `serve` reserves stdout for the bound-URL line, and a
  chatty exporter writing to stderr would interleave with and corrupt
  `serve.log` when redirected the way the README's
  `ai-raccoon serve > serve.log 2>&1 &` pattern does.
- The batch queue is capped at `MaxQueueSize` 2048 and **drops** on overflow
  rather than growing without bound.
- Defaults: `ScheduledDelayMilliseconds` 5000, `ExporterTimeoutMilliseconds`
  30000, `MaxExportBatchSize` 512.
- **No retry by default.** In-memory and disk retry are experimental
  opt-ins behind `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`.
- `TracerProviderSdk.Dispose` / `MeterProviderSdk.Dispose` hardcode a
  **non-configurable 5000 ms** shutdown grace period per provider. With
  both a tracer and a meter provider, an unreachable collector can add up
  to roughly 5s per provider to process exit.

**Consequence of this posture.** We lower the per-export timeout from the
~~30s~~ **10s** default (see correction below) so the worst case per export is bounded, and we accept silent
failure as the deliberate trade for a CLI that stays silent when idle. The
`observability otlp` verb reports live exporter state on request, and
`OTEL_DIAGNOSTICS.json` is documented as the escape hatch when someone
needs to see exporter errors.

**Unverified, flagged as such.** The exact end-to-end shutdown-path timing
when the collector is unreachable was not measured against this codebase —
only the SDK's own hardcoded 5000ms-per-provider constant is confirmed from
source. Treat "up to ~5s per provider" as a ceiling derived from that
constant, not a measured number.

### Default protocol and ports

gRPC is the .NET SDK default, at `localhost:4317`; `http/protobuf` defaults
to `localhost:4318`. One asymmetry matters for configuration: when the
endpoint comes from the generic `OTEL_EXPORTER_OTLP_ENDPOINT`, the SDK
appends the per-signal path (e.g. `/v1/traces`) for `http/protobuf`. When
the endpoint is set per-signal (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`) or set
explicitly in code, it is used verbatim — no path is appended.

Because we set the endpoint explicitly (`OtlpExport.ConfigureExporter`,
forced by the cleared configuration sources — see "Configuration channel"
below), the SDK's own append never fires for us either. We reproduce it
ourselves for `http/protobuf` (`OtlpExport.SignalEndpoint`), appending
`/v1/traces` or `/v1/metrics` idempotently so an endpoint that already
carries the signal path is not doubled; gRPC endpoints are left verbatim,
matching the SDK's own behavior for that protocol.

## Consequences

- **Positive.** Existing instrumentation (ADR 0002, ADR 0007) becomes
  exportable to any OTLP collector with zero source changes to the meters
  or activity source themselves — only host wiring is new; no tracing
  processor is added (see `project_id` retirement above).
- **Positive.** GC/CPU/memory telemetry (`System.Runtime`) becomes
  exportable for zero added packages, riding the same opt-in gate as the
  application meters.
- **Positive.** Zero cost when unconfigured: the opt-in gate means every
  environment that never sets `OTEL_EXPORTER_OTLP_ENDPOINT` pays nothing —
  no threads, no sockets, no behavior change.
- **Negative.** New dependency surface in `src/AiRaccoon`: three
  OpenTelemetry SDK packages, none of which existed before this ADR.
- **Negative.** `project_id` leaves the process in plaintext over OTLP,
  same as it already does over the stderr log — a real, named residual
  risk, not a hidden one. See "`project_id` export" above and `SECURITY.md`
  for the operator-facing statement of it.
- **Neutral.** Shutdown can take up to ~5s longer per provider when a
  configured collector is unreachable (unverified precise figure; see
  above) — accepted as the cost of "opt-in and otherwise invisible."

## Non-Goals (explicit, remaining in force from ADR 0002)

Only the OTLP/gRPC non-goal is superseded by this ADR. These two remain —
note that ".NET runtime metrics" is a distinct surface from "ASP.NET/HTTP
auto-instrumentation" and this ADR takes the former while the latter stays
a non-goal (see ".NET runtime metrics" above):

- **No ASP.NET / HTTP auto-instrumentation.** Kestrel spans for every
  `/mcp` POST (and now `/observability` GET, per ADR 0008) are their own
  decision, not a side effect of adopting the OTel SDK for tool metrics or
  of adding `System.Runtime`.
- **No Azure Monitor exporter.** Application Insights / Azure Monitor
  belongs in a hosted-deployment ADR, not this local-collector one.

## Future evolution

- **A `project_id` redaction switch**, only if a real consumer asks for
  stronger guarantees than plaintext export — see "Where a redaction switch
  would go" above. Not built speculatively here; ADR 0002 §Future evolution
  item 3 is retired, not deferred.
- **`OpenTelemetry.Instrumentation.Runtime`**, only if `System.Runtime`'s
  built-in coverage turns out to miss something a real operator needs —
  the overlap between the two was not verified in this ADR (see ".NET
  runtime metrics" above).
- **`OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`** stays experimental upstream;
  revisit adopting it once it graduates, rather than opting into
  experimental retry behavior now.

## Alternatives considered

### Settings-table configuration (`ai-raccoon config set otlp.endpoint ...`)

Rejected. See "Configuration channel" above: the startup-ordering conflict
(bank not provably decryptable until after the host that needs OTLP
configuration is built) is disqualifying on its own; duplicated SDK parsing
and the risk of persisting `OTEL_EXPORTER_OTLP_HEADERS`-equivalent secrets
in the bank are the additional reasons it stays rejected even if the
ordering problem were solved.

### `serve --otlp-endpoint` CLI flag

Rejected. Duplicates a variable the SDK already reads
(`OTEL_EXPORTER_OTLP_ENDPOINT`) while covering exactly one of roughly ten
configuration knobs (protocol, headers, timeout, per-signal endpoints, ...)
that `OTEL_*` already exposes as a set. A flag per knob does not scale;
deferring to the standard variables does.

### Hashing `project_id` before OTLP export (ADR 0002 §Future evolution item 3)

Rejected — see "`project_id` export" above for the full reasoning. In
summary: `project_id` already leaves the process in plaintext on every
build via stderr logging, so hashing only the opt-in OTLP channel would
have protected nothing an attacker with log access couldn't already read;
an unsalted digest of a short, guessable project slug is reversible by
dictionary in milliseconds; and a hashed tag is unusable for the filtering
and grouping the tag exists for, without an out-of-band mapping that
reintroduces the exposure. Had it been built anyway, the correct location
would have been a `BaseProcessor<Activity>` in the OTLP tracing pipeline —
not at `ToolExecutionActivity` construction, which would have hashed the
value for local `dotnet-trace` consumers too and destroyed the plaintext
local-debugging experience ADR 0002 was written to provide.

### `OpenTelemetry.Instrumentation.Runtime` alongside the built-in `System.Runtime` Meter

Rejected for now. The built-in `System.Runtime` Meter (automatic since .NET
9, confirmed via Microsoft's own built-in-metrics reference) already covers
the owner's stated ask — GC, memory, CPU — with zero added packages. Adding
the separate `OpenTelemetry.Instrumentation.Runtime` package on top was not
ruled out for a technical reason so much as an unverified one: whether it
duplicates `System.Runtime`'s instruments under different names was not
checked in this research pass. Per "ask if a simpler shape would do," the
simpler, already-covering, zero-package shape is taken; the fourth package
is revisited only if a real gap shows up.

## Corrections — 2026-08-09

Found by the four-lane review in `docs/reviews/2026-08-09-otlp-export-review.md`, verified against
the decompiled pinned `opentelemetry-dotnet` 1.17.0 assemblies. Recorded here rather than edited
away, because this ADR is what the next maintainer will trust.

1. **Per-signal endpoint variables never worked.** The 2026-08-07 update above claims the restored
   `OTEL_*` channel gives the SDK "per-signal endpoint/protocol/timeout". False, for two independent
   reasons: the `AddOtlpExporter(o => …)` delegate runs *after* options construction, so anything it
   assigns wins over env-derived values; and `AddOtlpExporter` uses configuration type `Default`,
   which parses only the **generic** variables — the per-signal ones are read solely by the
   cross-signal `UseOtlpExporter()` API, which this code does not use. The .NET exporter's own README
   lists them as "Not supported" for this registration style. The OTLP spec says per-signal endpoints
   MUST be used as-is; here they are ignored, silently.
2. **`OTEL_EXPORTER_OTLP_TIMEOUT` is likewise defeated**, same mechanism — and the baseline is
   misstated. `OtlpExporterOptions.TimeoutMilliseconds` defaults to **10000**, not 30000. The 30s
   figure is `BatchExportProcessor.ExporterTimeoutMilliseconds`, a different knob. Lowering to 5s
   remains defensible; the justification cited the wrong number.
3. **The instrument accounting is short by one.** The paragraph above names four tagged and two
   untagged instruments — six of seven. The seventh, `ai_raccoon_queue_evicted_score`, appears
   nowhere, and its sibling counter *is* tagged with `project_id` **and** `reason`, so the score
   distribution cannot be sliced by the dimensions of the event count it accompanies.
4. **Instrument names throughout this document are historical.** The 2026-08-09 owner gate renamed
   every instrument onto the OTel dotted convention with units supplied, and moved the duration
   histogram to `mcp.server.operation.duration` in seconds. `ai_raccoon_tool_invocations` →
   `ai_raccoon.tool.invocations`; `ai_raccoon_tool_duration_ms` → `mcp.server.operation.duration`;
   the queue instruments lose their `_total` suffixes and gain units. Current names live in
   `OtlpNames`. Neither this ADR nor ADR 0002 ever recorded a naming rationale — the snake_case was
   an unexamined default, not a defended position, which is why it changed without superseding
   anything.
5. **The `AlwaysOnSampler` decision (2026-08-08 update) is superseded by ADR 0021**, which exports
   the ASP.NET request span so the unrecorded-parent condition the override exists for stops
   existing. Its removal is held against a named test in the proxy lane — see ADR 0021.
