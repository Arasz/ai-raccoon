# OTLP export review — metrics and traces emission

Date: 2026-08-09 · Scope: `src/AiRaccoon/Observability/**`, its call sites, and the OTLP wiring ·
Baseline: `main` @ `166aedf3`, packages `OpenTelemetry*` 1.17.0, .NET 10 ·
Gate run: `dotnet test --filter "Otlp|Observability"` → **96 passed, 0 failed**, build green.

Four independent lanes produced this: a cited OTLP/.NET fact sheet, a review of the emission code,
a review of the exporter wiring and export lifecycle, and a semantic-convention conformance
mapping. Every SDK claim below was verified against the pinned 1.17.0 assemblies (decompiled) or
the upstream spec, by two lanes independently where it is load-bearing. Findings that a lane could
not substantiate were dropped rather than reported — those are listed at the end.

## Verdict

The design is sound and better documented than most: ADR-0009 anticipates and consciously accepts
a large share of what a reviewer would otherwise flag, and the HTTP-mode pipeline is genuinely
proven end-to-end against a real loopback collector. The problems are not in the concept. They are
these, in descending order of what they cost an operator:

1. **Two promotion-queue instruments report provably wrong numbers** — they model SQLite-persisted
   state with process-lifetime deltas, so queue depth can read negative after a restart.
2. **One host path never flushes** — `ai-raccoon --transport http` (as distinct from `serve`) never
   disposes the host, and disposal is the *only* flush trigger the SDK has.
3. **A mistyped endpoint kills the server** — `OTEL_EXPORTER_OTLP_ENDPOINT=127.0.0.1:4317` (no
   scheme) throws out of startup. An observability opt-in must not be able to take down the thing
   it observes.
4. **Every exported span is an orphan** — the tool span parents onto an ASP.NET Core Activity that
   is deliberately never exported, so each trace is a one-span fragment with a dangling parent id.
5. **Naming and units depart from OTel convention throughout**, and an MCP semantic convention now
   exists that the instrumentation could target instead.

Nothing here suggests the ADR was wrong to ship. Items 1–4 are bugs against the ADR's own intent;
item 5 is a convention gap the ADRs never considered — verified: neither ADR-0002 nor ADR-0009
contains any naming rationale, so snake_case is an unexamined default, not a defended position.

---

## A. Defects — the telemetry is wrong or absent

| # | Sev | Where | What happens |
|---|-----|-------|--------------|
| **A1** | high | `PromotionQueueMetrics.cs:25-27`, writers `PromotionQueueService.cs:27,48,97,114` | `ai_raccoon_queue_queued` is an `UpDownCounter` fed **deltas since process start**, but the queue is persisted in SQLite. Restart with 40 rows queued, discard 5, and the exported depth reads **−5** — permanently offset for the life of the process. Fix: `CreateObservableUpDownCounter` reading the store. |
| **A2** | high | `PromotionQueueMetrics.cs:20,43-46,73`; no writer in `PromotionQueueService.DiscardAsync:106-125` | `ai_raccoon_queue_capacity_utilization` returns a **cached field**, not an observation. It exports `0.0` ("queue empty") from startup until the first propose — on a queue that may be full — and `DiscardAsync` never updates it, so it stays stale-high after a bulk discard. Fix: compute inside the callback; closes both halves. |
| **A3** | high | `Program.cs:33,53`, `HostExtensions.cs:14-26` vs `ServeRunner.cs:90-93` | The bare-launch path never disposes the host. `TelemetryHostedService.StopAsync` is a hard `return Task.CompletedTask` (verified in the 1.17.0 assembly) — provider **disposal** is the only thing that reaches `Shutdown(5000)` and flushes. So `--transport http` loses up to 60 s of metrics and 5 s of spans on **every clean exit**. `ServeRunner` does it correctly; the asymmetry is the bug. One line. |
| **A4** | high | `OtlpExport.cs:65,68`; `OtlpExportState.cs:19-29` | `new Uri(rawEnvString)` runs inside the exporter delegate, i.e. inside `app.StartAsync`. `OTEL_EXPORTER_OTLP_ENDPOINT=127.0.0.1:4317` throws `UriFormatException`, and the only catch there filters on `IsAddressInUse` — so it **propagates unhandled and the MCP server dies at boot**. Confirmed by executing it, not by reading. |
| **A5** | medium | same lines | The near-miss of A4 is worse than the crash: `new Uri("localhost:4318")` *succeeds* with `Scheme=localhost`. The exporter builds, every export fails against an EventSource nobody listens to, and `serve observability otlp` reports "enabled" throughout. |
| **A6** | medium | `OtlpExport.cs:63,68` | `SignalEndpoint` doubles the path on two real input shapes: `…/v1/traces/` → `…/v1/traces/v1/traces`, and `…/V1/TRACES` → `…/V1/TRACES/v1/traces`. The SDK's own `AppendPathIfNotPresent` handles both (trailing-slash form, `OrdinalIgnoreCase`); this is a weaker hand-rolled copy of a method that ships in the same package. |
| **A7** | medium | `OtlpExport.cs:38` + `ToolExecutionActivity.cs:35` | `AlwaysOnSampler` makes the tool span recorded, but the span still **inherits** the unrecorded `HttpRequestIn` parent, whose source is deliberately never registered. Every exported span therefore carries a `parent_span_id` the collector will never resolve. Starting the span with an explicit empty parent fixes the orphan **and** the original sampling problem, and leaves `OTEL_TRACES_SAMPLER` working — a narrower remedy than the one shipped. |
| **A8** | medium | `ToolCallMetrics.cs:19,74` | `Dispose()` disposes the `Meter` but not the `ActivitySource`, so the source is never detached. Benign in production (one instance); in the test process 42 instances accumulate and every new listener matching `"AiRaccoon.MemoryTools"` attaches to all of them — a latent source of flaky `ShouldHaveSingleItem()` assertions. |
| **A9** | medium | `ToolCallMetrics.cs:18`, `PromotionQueueMetrics.cs:24`, `OtlpExport.cs:31,32,39`, `MonitoringCommandRenderer.cs:8` | The meter/source names are string literals hand-copied into four places. A third metrics class added tomorrow compiles, emits over EventPipe, and is **silently dropped from export** — exactly the failure ADR-0009:44-46 calls "a real observability gap, not a small oversight". The ADR fixed the instance and left the mechanism. Violates `derive-or-delete-the-list`. |
| **A10** | low | every tool `catch`, e.g. `MemoryTools.cs:69-73` | A client disconnecting mid-call is caught by the blanket `catch` and recorded `result=error`, `error_type=TaskCanceledException`, with the span marked `Error`. Client cancellation inflates the server's error rate. |
| **A11** | low | `ToolExecutionActivity.cs:45` | `_stopwatch.Reset()` in `Dispose` zeroes `Elapsed`. Harmless today; it silently turns the obvious future fix (record in `Dispose`) into "every call took 0 ms". |
| **A12** | low | `MemoryTools.cs:66-67` and every sibling | `RecordInvocation()` fires before the envelope is serialised by the MCP layer, so a serialisation failure is counted as `result=success`. |

## B. Conformance deviations — it works, but departs from OTel

| # | Where | Deviation → conforming shape |
|---|-------|------------------------------|
| **B1** | all 9 instruments | Snake_case names with embedded units and `_total` suffixes. OTel names are dot-namespaced; the `.`→`_` rewrite **and** the `_total` suffix are the Prometheus exporter's job, applied at translation time. `ai_raccoon_queue_evictions_total` → `ai_raccoon.queue.evictions`, etc. Concrete risk downstream: an already-`_total`-suffixed monotonic sum can become `…_total_total`. |
| **B2** | `ToolCallMetrics.cs:25-34,71` | Duration in **milliseconds**. Semconv: *"When instruments are measuring durations, seconds (i.e. `s`) SHOULD be used."* Consequence today is concrete, not theoretical — this process also exports `System.Runtime`'s `dotnet.gc.pause.time` in **seconds**, so the two cannot share a dashboard axis. Buckets must be rescaled with the unit; it is one edit, not two. |
| **B3** | 8 of 9 instruments | **No `unit` argument at all.** Includes `ai_raccoon_queue_wait_seconds`, whose name promises seconds the code never sets, and `ai_raccoon_tool_invocations`, which ADR-0002:40 documents as `{call}` — code/doc drift. |
| **B4** | `ToolExecutionActivity.cs:13,72,74`; `ToolCallMetrics.cs:56,68` | `error_type` should be **`error.type`** — a *stable* registry attribute. The value shape (`exception.GetType().Name`) is correct and bounded; only the key is wrong, and a semconv-aware backend's error panels key on it. |
| **B5** | `ToolExecutionActivity.cs:35` | `ActivityKind.Internal` for an inbound RPC. Backends route `Server` → "requests" and `Internal` → "dependencies", so AiRaccoon currently presents as a service with **no server surface at all**. MCP semconv is explicit: server spans SHOULD be `SERVER`. |
| **B6** | `ToolExecutionActivity.cs:63-75` | `RecordError` sets status and a tag but **drops the exception object** — no stack trace anywhere in the trace. `Activity.AddException(ex)` is built into the BCL since .NET 9 and populates `exception.type`/`message`/`stacktrace` automatically. |
| **B7** | `ToolCallMetrics.cs:18-19`, `PromotionQueueMetrics.cs:24` | Meter/ActivitySource constructed with no **version**, so `InstrumentationScope.version` is empty on every signal — you cannot tell which build produced a series across a rollout. |
| **B8** | `OtlpExport.cs:29` | Resource carries only `service.name`. `service.version` is missing while `1.4.0` sits in the csproj. *(`service.instance.id` was investigated and **refuted** — `AddService` auto-generates it per process.)* |
| **B9** | — | An **MCP semantic convention now exists** (GenAI conventions repo, Development status): `mcp.method.name` (required), `gen_ai.tool.name`, `mcp.session.id`, span name `{method} {target}` → `"tools/call memory_search"`, and a metric `mcp.server.operation.duration` (histogram, unit `s`, with prescribed buckets) that is *the same measurement* as `ai_raccoon_tool_duration_ms`. Targeting it is available; it is Development-stability, which is the argument against. |

## C. Where ADR-0009 is now wrong

These are documentation defects, and the ADR is load-bearing — it is what a future maintainer will
trust.

- **C1 — Per-signal endpoint variables do not work, for two independent reasons.** ADR-0009:171-173
  claims the restored `OTEL_*` channel gives the SDK "per-signal endpoint/protocol/timeout".
  Verified false twice over: (a) the `AddOtlpExporter(o => …)` delegate runs *after* options
  construction, so `options.Endpoint = …` wins over anything env-derived; (b) even without the
  delegate, `AddOtlpExporter` uses configuration type `Default`, which parses only the **generic**
  variables — the per-signal ones are read only by the newer cross-signal `UseOtlpExporter()` API,
  which this code does not use. The .NET exporter README lists them as "Not supported" for exactly
  this registration style. The OTLP spec says per-signal endpoints MUST be used as-is; here they are
  ignored silently.
- **C2 — `OTEL_EXPORTER_OTLP_TIMEOUT` is defeated**, same mechanism (`OtlpExport.cs:54`). And the
  ADR's stated number is wrong: it says this lowers the timeout "from the 30s default", but the
  actual `OtlpExporterOptions.TimeoutMilliseconds` default is **10000**. 30000 is
  `BatchExportProcessor.ExporterTimeoutMilliseconds` — a different knob. The 5 s decision is still
  defensible; the justification cites the wrong baseline.
- **C3 — The instrument accounting is short by one.** ADR-0009:50-56 says four of seven promotion
  instruments are tagged and names two as untagged — that is six. The seventh,
  `ai_raccoon_queue_evicted_score`, appears nowhere, and its sibling counter *is* tagged with
  `project_id` **and** `reason`, so the score distribution cannot be sliced by the dimensions of the
  event count it accompanies.
- **C4 — README overclaims.** `README.md:174-175` says `serve observability otlp` "reports what the
  server is actually exporting to". It reports the raw env vars — for `http/protobuf` it prints the
  base URL, not the `/v1/metrics` one actually used. ADR-0009:162-163 is honest about this ("it
  reads config state, not exporter health"); the README should match the ADR.

## D. Deliberate decisions — arbitrated

| Decision | Verdict |
|---|---|
| **`project_id` on the counter, not the duration histogram** (ADR-0002) | **Confirmed defensible** — the arithmetic backs it. But name the cost the ADR omits: you can never answer "is project X slow", and the counter and histogram **cannot be joined**, because their denominators aggregate over different tag sets. |
| **Promotion histograms untagged** (ADR-0009:52-56) | **Challenged.** `_waitSeconds` is written by *both* `RecordPromoted` and `RecordDiscarded`, so "how long do rows wait before leaving the queue" cannot distinguish the system working from the agent rejecting everything — the only interesting question about that distribution. An `outcome` tag costs **two** series, so the cardinality reasoning that justifies omitting `project_id` does not reach it. |
| **`project_id` exported in plaintext** (ADR-0009:234-297) | **Confirmed, no challenge.** The same value already leaves the process unconditionally via ~10 log templates; hashing one opt-in channel would not be a control. Risk is stated and assigned in `SECURITY.md`. |
| **Caller-supplied `project_id` reaches the counter before the access gate** (ADR-0009:60-63) | **Challenged on framing.** The ADR files this under per-series billing, i.e. money. The sharper consequence is *availability*: `project_id` is a free-form client string and the activity is constructed **before** `gate.RequireAsync`, so a client looping with a fresh GUID mints a series per rejected call until the SDK's cardinality cap folds everything into the overflow bucket — at which point the counter stops being usable for legitimate projects too. |
| **Stdio gets no exporter** (ADR-0009:230) | **Confirmed for metrics** (60 s interval vs a ~5-minute process), **but the silence is not defensible**: set `OTEL_EXPORTER_OTLP_ENDPOINT`, start on stdio — the default transport for every MCP client — and nothing anywhere says the telemetry is being discarded. The trace half of the reasoning is also weaker than stated: a 5 s batch delay against a 5-minute lifetime would have flushed ~60 times. |
| **`AlwaysOnSampler`; `OTEL_TRACES_SAMPLER` defeated** (ADR-0009:216-223) | **Diagnosis confirmed exactly** (unrecorded parent → `ParentBased`'s local-parent-not-sampled branch is `AlwaysOff` → `StartActivity` returns null). The remedy is broader than needed — see A7. |
| **`service.name` fixed, `OTEL_SERVICE_NAME` overridden** (ADR-0009:186-200) | **Confirmed, mechanism and all.** Later-registered detector wins the resource merge; the ADR's claim and its cited test are both correct, and non-`service.name` entries from `OTEL_RESOURCE_ATTRIBUTES` survive. |
| **`AddMeter("System.Runtime")` with no extra package** | **Confirmed** — built into the runtime since .NET 9. |
| **No ASP.NET Core / HttpClient instrumentation** | Accepted as a standing non-goal. Note it is also what makes A7's orphan parent unfixable by registration alone. |

## E. Coverage gap

The tool surface is **complete**: 22 `[McpServerTool]` methods, 22 `ToolExecutionActivity` sites,
one per tool, all with the identical try/record/catch/record shape. No tool is uninstrumented, and
no meter or source is emitted-but-unregistered.

**Background work is entirely uninstrumented.** `ExtractionHostedService`, `WatchHostedService`,
`BankMaintenanceHostedService` and `IdleWatchdog` emit no span and no metric of their own — no pass
duration, no failure count, no watcher backlog, no embedding throughput, no sync volume. The
promotion counters fire only as a side effect of extraction reaching `ProposeAsync`. This is scoped
out by ADR-0002 rather than missed, but the consequence is that a degrading background pass is
visible only in `serve.log` — invisible to the collector this whole feature exists to feed.

## F. Test gaps

The E2E tests are **better than expected**: both boot the real `Program` through
`WebApplicationFactory`, make a genuine `tools/call` over Streamable HTTP, and assert against a real
`HttpListener` collector. Honest end-to-end proof for the happy path. Missing:

1. **No flush-before-exit test.** Both E2E tests call `ForceFlush()` by hand — precisely the thing
   production never does. Nothing would have caught A3.
2. **The path assertion cannot detect A6.** `path.Contains("/v1/metrics")` also passes for
   `/v1/metrics/v1/metrics`. The one test positioned to catch the bug asserts a substring where it
   should assert equality.
3. **The `SignalEndpoint` theory stops one case short of the bug** — it covers `…/v1/traces` and
   `…:4318/` but not `…/v1/traces/` or a case-varied path: exactly the two shapes A6 gets wrong.
   The table reads as exhaustive and isn't.
4. No test for a malformed endpoint, an unreachable collector, a per-signal override, a
   `OTEL_EXPORTER_OTLP_TIMEOUT` override, or an unknown protocol.
5. Applying the project's own rule — *a check you have not seen fail is not a check* —
   `EndpointSet_DoesNotRegisterAspNetCoreInstrumentation` asserts an absolute `false` and passes
   identically if `AddOtlpExport`'s body is deleted. Two more tests are *named* after "the
   configured exporter" but flush to an in-memory exporter chained on afterwards; they prove
   registration, not OTLP.

## G. Investigated and cleared

Recorded so the next reviewer does not re-derive them:

- **No unrecorded exit path exists in any of the 22 tools.** Every `catch` is unfiltered; cancellation,
  validation throws, gate rejections and `await`-propagated faults all reach `RecordError`. The
  `_recorded` guard makes double-recording impossible.
- **The `Dispose`-before-stopwatch-stop ordering is unobservable** — both record paths read `Elapsed`
  inside the `using` block. The hypothesis does not survive contact with the code.
- **The SDK does not double-append.** Setting `Endpoint` programmatically flips
  `AppendSignalPathToEndpoint` to `false`; the manual append is therefore **required**, not
  redundant, and the "gRPC verbatim" reasoning is exactly right.
- **`Volatile.Read(ref double)` is sound** — atomic and non-torn even on 32-bit; the gauge's problems
  are semantic, not memory-model.
- **`error_type`'s value is bounded** — it ranges over a finite set of reachable exception types.
  Only the key is non-conforming.
- **The `OTEL_*` config re-admission works and is minimally scoped**, and three tests pin it.
- **Export failures cannot block or leak into tool calls** — bounded queue, drop-on-overflow,
  dedicated export threads; worst case ~5 s per provider at shutdown, matching the ADR's ceiling.
- **`service.instance.id` is present** (auto-generated) — a fleet is already distinguishable.
- **`MCP_TRANSPORT` does not exist** in the code; transport is `--transport`. A stale name inherited
  from an old note, already corrected once in `docs/work/2026-08-07-moe-f-docs.md:37`.
- **No dashboard or alert consumes these metric names** — the rename blast radius is ~35 pinned
  string assertions in the test suite plus 7 documents, not any operator artefact.

---

## Sources

OTLP spec and exporter env-var contract; OTel semantic conventions (metrics/units, `error.type`
registry, resource/service); the GenAI conventions repo's MCP page; the `opentelemetry-dotnet`
exporter README and SDK source; Microsoft Learn on built-in runtime/ASP.NET/HTTP metrics,
`Activity.AddException`, and the .NET 10 Activity-sampling breaking change. SDK behaviour was
verified against the decompiled pinned 1.17.0 assemblies and, where load-bearing, corroborated by a
second independent lane. Full citation list is carried in the lane reports for this task.
