# OTLP fix plan

Date: 2026-08-09 · Branch `task/otlp-fix`, stacked on `task/otlp-export-review` (PR #213) ·
Inputs: `docs/reviews/2026-08-09-otlp-export-review.md`, `docs/work/2026-08-09-otlp-export-feedback.md`
(owner gate, 18/18 APPROVE, F5 = "B"), `docs/work/2026-08-09-dotnet-builtin-telemetry-catalog.md`,
and a three-lane MoE panel (architect / test-engineer / dotnet-engineer).

Baseline gate: `dotnet test --filter "Otlp|Observability"` → **97 passed, 0 failed**.

## Scope

Nineteen work items: the eighteen the owner approved at the gate, plus quiet-mode log routing, added
by owner ruling mid-task. Every item below carries acceptance criteria and the gate that proves
them — a point without both is a wish, not a plan.

**Three things the panel found that were not in the gate**, and which change the shape of the work:

1. **The seam for tool instrumentation already exists.** `McpServerSetup.cs:169` registers
   `ToolRefusals.Filter` as a `CallToolFilter`, on both host paths. A telemetry filter beside it
   covers all 22 tools *and every future one* by construction, and delivers three approved items for
   free (MCP semconv naming, `project_id` after the gate, success recorded after serialisation).
   ADR-0002 rejected exactly this — "a decorator … for 3 lines of code" — when no filter pipeline
   existed. One now does. **Open decision D1.**
2. **Quiet mode leaks today**, before any change here: `AddStderrConsoleLogging`
   (`McpServerSetup.cs:153-162`) keeps a stderr console provider and only lowers the level to
   `Warning`. Hermes owns that channel.
3. **Two approved decisions collide.** N2/N3 make the tool span `ActivityKind.Server`; T2 makes the
   ASP.NET request span — also `Server` — its exported parent. Backends deriving request rates from
   `Server` spans will double-count every tool call. **Open decision D3.**

## Naming consolidation

One namespace, one registry, one edit to rename. Namespace is **`ai_raccoon.`** — dot-separated
segments, underscores only *within* a segment. That is what the convention actually says ("use
namespacing, delimit with a dot"; "separate words within an element by underscores"), and it keeps
the product recognisable; `airaccoon.` was considered and rejected as a second spelling of the same
name.

The `.`→`_` rewrite and the `_total` suffix are the **Prometheus exporter's** job at translation
time. Neither belongs in an instrument name.

| Today | Becomes | Unit | Note |
|---|---|---|---|
| `ai_raccoon_tool_invocations` | `ai_raccoon.tool.invocations` | `{invocation}` | No semconv equivalent — the MCP convention prescribes only the duration histogram |
| `ai_raccoon_tool_duration_ms` | `mcp.server.operation.duration` *(D2)* | `s` | Same measurement the MCP convention already names; ms→s with rescaled buckets either way |
| `ai_raccoon_queue_queued` | `ai_raccoon.queue.queued` | `{item}` | Becomes observable (WP1) |
| `ai_raccoon_queue_evictions_total` | `ai_raccoon.queue.evictions` | `{eviction}` | `_total` dropped |
| `ai_raccoon_queue_evicted_score` | `ai_raccoon.queue.eviction.score` | `1` | Gains `project_id` + `reason` (R1) |
| `ai_raccoon_queue_wait_seconds` | `ai_raccoon.queue.wait` | `s` | Gains `outcome=promoted\|discarded` (R1) |
| `ai_raccoon_queue_promoted_total` | `ai_raccoon.queue.promoted` | `{row}` | |
| `ai_raccoon_queue_discarded_total` | `ai_raccoon.queue.discarded` | `{row}` | |
| `ai_raccoon_queue_capacity_utilization` | `ai_raccoon.queue.capacity.utilization` | `1` | Becomes a real observation (WP1) |
| tag `error_type` | `error.type` | — | Stable registry attribute; value shape already correct |
| tag `tool` | `gen_ai.tool.name` | — | `mcp.tool.name` does **not** exist |
| — | `mcp.method.name` = `tools/call` | — | Required by the MCP convention |
| tag `project_id` | `ai_raccoon.project.id` | — | Nothing in the registry covers a tenant id |
| tag `reason` | `ai_raccoon.queue.eviction.reason` | — | Single-valued today (`capacity`) |
| span name `memory_search` | `tools/call memory_search` | — | `{mcp.method.name} {target}` |

Scope names — `AiRaccoon.MemoryTools`, `AiRaccoon.PromotionQueue`, and a new `AiRaccoon.Background`
— plus `System.Runtime`, `System.Net.Http`, `Microsoft.AspNetCore.Hosting` all move into one
`TelemetryScopes`/`OtlpNames` registry that `OtlpExport` and `MonitoringCommandRenderer` **derive**
from. Centralising is not enough on its own: a hand-kept list in one file is still a hand-kept list.
The registry earns its keep only with WP2's guard test.

**Blast radius, measured:** ~35 string-literal assertions across 11 test files, 7 documents, and
zero dashboards or alerts — nothing outside the repo consumes these names.

## Work packages

Each names its gate. "Red first" means the project's `prove-the-check-fails` invariant applies:
put the defect in front of the check and watch it fail before fixing it.

| WP | Item | Acceptance criteria | Gate |
|---|---|---|---|
| **WP1** | Queue instruments read the store (F1) | Depth reads 35 — not −5 — after a simulated restart plus a discard; utilization reflects a pre-loaded store before any mutation, and updates on discard | Two new `PromotionQueueMetricsTests`, red first against today's delta counter and cached field |
| **WP2** | Name registry + derivation guard (N1, A9) | Every meter/source the real container creates is in the registry; a stray meter turns the guard red | New guard test; red first by adding a throwaway meter |
| **WP3** | Flush on exit (F2) | A host started and stopped the way `Program.cs` exits — **no** hand `ForceFlush` — delivers to the collector | New `OtlpFlushOnExitTests`, red first against today's non-disposing path |
| **WP4** | Endpoint validation (F3) | `127.0.0.1:4317` and `localhost:4318` both disable export with a warning; the server still starts and still serves a tool call | New `OtlpExportTests` cases; red first (crash / silent-pass respectively) |
| **WP5** | Quiet-mode file logging (owner ruling) | In quiet mode nothing reaches stdout/stderr and **all** levels reach the file; an unwritable path degrades to silence, never a crash | Rewritten `QuietLoggingTests` — the current ones pin the old Warning+-on-stderr model |
| **WP6** | Signal-path composition (F4) | `…/v1/traces/` and `…/V1/TRACES` do not double | Two cases added to the existing `SignalEndpoint` theory, red first; E2E path assertion changed from `Contains` to equality |
| **WP7** | Remedy B + sampler revert (F5=B, T2) | The request span is exported and the tool span nests under it with a resolvable parent; `OTEL_TRACES_SAMPLER` works again | New parentage assertion in `OtlpTraceExportE2ETests`, red first; the existing non-goal test is **replaced**, not flipped |
| **WP8** | MCP semconv on the span (N2, N3) | Span is `tools/call <tool>`, carries `mcp.method.name`/`gen_ai.tool.name`/`error.type`, and records the exception | `ToolExecutionActivityTests` extended |
| **WP9** | `project_id` after the gate (R2) | A rejected call records a bounded sentinel, not the caller's string | New test; plus a count check — `new ToolExecutionActivity(` vs the authorise call |
| **WP10** | Promotion tags (R1) | Wait histogram carries `outcome`; eviction score carries `project_id` + `reason` | `PromotionQueueMetricsTests` extended |
| **WP11** | `service.version` + scope versions (N4) | One assembly-attribute read feeds resource and every scope | `OtlpExportTests` assertion on the resource |
| **WP12** | `System.Net.Http` meter + source (T1) | Outbound calls appear as metrics and as spans nested under the tool span | E2E assertion |
| **WP13** | Background instrumentation (R3) | Each hosted service emits a pass span + duration/failure metrics under `AiRaccoon.Background` | Per-service tests + a container-derived guard that notices a fifth hosted service |
| **WP14** | Test-gap closure (R4) | The four named gaps closed; the two misnamed "configured exporter" tests renamed; the vacuous non-goal test given a positive counterpart | Gate re-run |
| **WP15** | Hygiene batch (R5) | ActivitySource disposed; cancellation not counted as a server error; `_stopwatch.Reset()` gone | Existing suite + one cancellation test |
| **WP16** | Doc corrections (C1, C2) | ADR-0009's four false claims fixed; README's `observability otlp` overclaim corrected; `IPromotionQueueMetrics`'s false layering rationale corrected; `Quiet` doc comments rewritten | Doc review |
| **WP17** | **ADR-0021** (not 0020 — the proxy lane already owns 0020) | Records the non-goal reversal, its provenance, remedy B, the `AppContext` switch, and the `Internal` span-kind ruling; supersedes ADR-0002 §Non-Goals b2 and ADR-0009 §Non-Goals b1; retires ADR-0009's 2026-08-08 sampler block; ADR-0002 → **Superseded** | Doc review |
| **WP18** | `ServerInfo` reads resolved state (panel) | The CLI verb cannot report "enabled" for an endpoint the exporter refused | New test; this is a **regression WP4 would otherwise introduce** |
| **WP19** | `IMeterFactory` migration | Meters come from the factory; the two `IDisposable`s go | Existing suite — runs **last**, on settled constructors |

## Sequencing

`OtlpExport.cs` and `McpServerSetup.cs` are the hot files — five and three reasons to edit them
respectively. Serialise around the files, not the items.

- **Parallel from the start:** WP1, WP3 (`HostExtensions.cs` only), WP15.
- **One dispatched unit** owning `OtlpExport.cs` + `McpServerSetup.cs`: WP4 → WP5 → WP7 → WP12.
  Three separate agents here would serialise on the same file anyway.
- **WP1 before WP2/WP10**, which need its final instrument shape; both share test files with it.
- **WP2 before WP8/WP13** — the registry has to exist before new scopes are added to it.
- **WP9's 22-site fan-out** runs in parallel once WP8's signature exists.
- **WP13 after WP8**, which extracts the shared span/timing mechanics from the MCP-specific tags.
- **WP19 last** — it touches the same constructors as WP1, WP2 and WP11 and should touch them once.
- **WP16/WP17 anytime**, docs only.

## Open decisions — second gate

The panel surfaced trade-offs the first gate could not have anticipated. These are genuinely the
owner's, and the plan does not assume them.

- **D1 — Tool spans move to a `CallToolFilter`?** Covers all 22 tools and every future one by
  construction; removes `ToolCallMetrics` from 7 constructors and 11 test files; delivers MCP-semconv
  naming, post-gate `project_id` and post-serialisation success for free. Cost: `projectId` comes
  from `request.Params.Arguments` rather than a typed parameter, plus a projection for the two tools
  that don't pass a plain id. **Reverses ADR-0002's AOP rejection.** Recommend yes — it converts
  WP9's 22-site fan-out and WP13's drift risk into one registration.
  **Proxy constraint:** once `DefaultOptions.Transport` flips to `Proxy`, bare `ai-raccoon` executes
  no tools — it forwards. So this filter registers on the **backend host only**; on the proxy it
  would mint tool metrics for calls that process never ran. The proxy's own forwarding uses
  `IncomingFilters` at the same seam, so the two must be shown never to fire in one process.
- **D2 — Adopt `mcp.server.operation.duration`, or keep `ai_raccoon.tool.duration`?** Same
  measurement; adopting avoids a private shape we would migrate off later, but the convention is
  Development stability and could move again. Recommend adopt.
- **D3 — Tool span `Server` or `Internal`?** ~~Recommend `Internal`~~ **RULED 2026-08-09: `Internal`.**
  The ASP.NET span carries `Server`; one server span per request, one documented deviation from the
  MCP convention. WP8 implements `ActivityKind.Internal`.
- **D4 — Background services: one generic `IOperationTelemetry` port in Core, or an inline meter per
  service?** The port is one abstraction for ~15 measurements; inline is no new type but four copies
  of the timing/status/error contract. Recommend the port.
- **D5 — Quiet log rotation?** None as designed; the file accumulates across the *installation's*
  lifetime, since every Hermes-spawned connection shares one path.
- **D6 — Is quiet HTTP/combined mode reachable?** `CreateWebHost` has its own console-logging block
  with the identical leak. If Hermes only ever uses stdio, it can wait; if not, it needs WP5's
  treatment now.
- **D7 — Breaking-change window.** WP2 + WP8 + D2 together rename essentially every name we emit.
  Nothing outside the repo consumes them today. Ship as one break, or dual-emit for a period?
- **D8 — `dotnet-trace --providers` scope list**: keep naming only `AiRaccoon.MemoryTools`, or every
  AiRaccoon scope now that background passes exist?
- **D9 — Split `Observability/` into `Emission/`, `Export/`, `Monitoring/`?** The ADR-0008 CLI verb
  is a product feature filed next to exporter plumbing. Lowest-value item here; skip if it reads as
  churn.

## Corrections to our own records

Beyond WP16/WP17: `.ai-badger/state.json`'s `stillTrue` list claims `MCP_TRANSPORT=http` selects the
transport. **Verified false** — `MCP_TRANSPORT` and `AIRACCOON_DATA_ROOT` appear only under
`tests/`; production uses `--transport`. Two panel lanes flagged it independently. Corrected at the
finish protocol, not mid-task.

## The stdio→HTTP proxy changes two of these items

A parallel workstream is building a stdio MCP server that proxies to the HTTP MCP server. That shape
was evaluated here on 2026-08-06 and deferred as out of scope at the time — *"a built-in stdio→HTTP
bridge in the binary (the spawned stdio process proxies JSON-RPC to the shared HTTP server; first
spawn creates it, later spawns attach) — i.e. mcp-remote in reverse, embedded"*
(`docs/work/archive/2026-08-06-http-serve-moe-protocol-switch.md:115`). It is now being built, and it
touches this plan in two places.

### F3/WP4 gets more severe, not less

Three compounding effects, all in the same direction:

1. **Blast radius.** Today a malformed `OTEL_EXPORTER_OTLP_ENDPOINT` kills only the `serve` process;
   stdio wires no exporter (ADR-0009) so stdio clients are immune. Once the first stdio spawn
   *creates* the HTTP backend, a boot crash there takes down **every** MCP client, not one path.
2. **Reachability.** The proxy spawns the backend and the spawn inherits the environment, so `OTEL_*`
   set in a client's config (`.mcp.json` `env`, the Hermes provider config) now reaches the process
   that can die on it. The variable moves from "something a shell sets before `serve`" to "something
   a client config file sets" — a much easier place to typo.
3. **Diagnosability.** The client sees a connection failure; the cause is in the backend's log, which
   under the quiet-mode ruling is a file. WP18 (`ServerInfo` reads resolved, not env, state) stops
   being a nicety. The proxy lane's `BackendLauncher` does capture the child's exit code, so the
   failure reads as "serve exited N" rather than "backend unavailable" — but that only helps once
   WP4 stops the crash happening at all.
4. **Worse than per-client, per the proxy lane.** The spawned `serve` inherits the *proxy's*
   environment, and the proxy is spawned by every MCP client on the machine. So **the first client to
   start the backend fixes `OTEL_*` for every other client's traffic** — one project's `.mcp.json`
   typo takes memory down for all of them. This is the argument for WP4 being defensive at the parse
   site rather than validated at one edge, and it is specifically the "never throw" half that earns
   its keep.

None of this changes WP4's design — validate, disable, warn, never throw. It raises its priority and
makes "never take the server down" the load-bearing property rather than a nicety.

### WP7's sampler revert may be unsafe — hold it

**This is the one that bites.** ADR-0009 justified `AlwaysOnSampler`, and the panel justified
*removing* it, on the same premise: *"this app propagates no incoming distributed trace context —
there is no genuine remote-parent case."* A stdio→HTTP proxy is an HTTP client calling our own
server, so that premise stops being true the day it ships.

If the proxy propagated `traceparent`, `HttpRequestIn` would become a child of a *remote* parent
instead of a trace root, `ParentBased` would honour the proxy's sampling decision, and — since a
stdio proxy wires no exporter under ADR-0009 — the remote parent would read as not-sampled and
**every server span would be dropped**. That is issue #181 one layer up, introduced by the very fix
meant to close it.

**Answered by the proxy lane (2026-08-09): it propagates nothing.** By construction of their
ADR-0020 the proxy registers no tools and no prompts, wires no exporter, and with no `ActivityListener`
or `MeterListener` in the process, `HttpClient`'s `DiagnosticsHandler` is bypassed and injects no
headers. `HttpRequestIn` stays a trace root.

**But that holds only incidentally** — it is a consequence of three independent design choices, any
one of which could be reversed without anyone noticing our premise broke. The proxy lane is adding an
explicit test pinning "a forwarded request carries no `traceparent`/`tracestate` header", citing this
decision as the reason it exists. They also flagged that their reading of `DiagnosticsHandler`'s
global-enable behaviour is not yet measured, and asked us to hold.

**Therefore:** WP7 splits. The `AddSource("Microsoft.AspNetCore.Hosting")` half proceeds now. The
`SetSampler` removal is **held with a named unblock condition** — the proxy lane's no-`traceparent`
test observed green. Not an open question any more; a dependency. If that test comes back red, the
always-on sampler stays and D10 reopens: either the proxy samples (reopening "stdio gets no
exporter"), or the server keeps a root-forcing sampler.

## Risks the plan carries

- **WP7's pairing is load-bearing.** Removing `SetSampler` is only safe *because* the hosting source
  is registered. Remove the source later and both the orphan and the silent-drop return. The E2E
  trace test is the guard, and it must be seen to fail with the source absent.
- **The `AppContext` switch's read timing is unverified.** It must be set before any ASP.NET hosting
  type is touched; whether the framework caches it at type initialisation was not confirmed. Treat as
  an implementation risk, not a settled design.
- **WP8's bucket boundaries must be read from the convention at implementation time**, not recalled.
- **WP9's fan-out is the widest single-line change** — 22 sites across 7 files; a missed one silently
  restores unbounded cardinality on that tool's rejection path. D1 removes this risk entirely.
