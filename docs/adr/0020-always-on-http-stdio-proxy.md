# 0020 — Always-on HTTP: the stdio entry point becomes a proxy

Date: 2026-08-09

Status: Accepted. Reverses the "no self-spawning daemon" decision recorded in
`docs/work/archive/2026-08-06-http-serve-design.md:137-146`, and supersedes R15 and R16 of
`docs/plans/2026-08-06-http-serve-mode-plan.md:33-34`.

## Context

A stdio `ai-raccoon` process is not a client of `ai-raccoon serve`. It is a second, independently
composed, complete server that opens the same SQLite bank directly — both hosts call the identical
`RegisterMemoryServices(...)` (`McpServerSetup.cs:53` for stdio, `:80` for HTTP). There is no path
by which a stdio tool call could ever have reached the `serve` process. They are peers, not client
and server.

The consequence is N full servers writing to one bank. Measured on one developer machine:
`ai-raccoon serve` plus four stdio processes, all on a 136 MB `~/.ai-raccoon/memory.db` with 12,890
entries and 7 watch registrations, aggregate RSS 484-620 MB against ~193 MB for a single shared
server. Each process loads its own ONNX `InferenceSession` (`OnnxEmbeddingGenerator.cs:19`) with no
`SessionOptions`, so each also sizes an ORT thread pool for the whole machine.

The fan-out is not only a cost problem. It causes correctness defects:

- **Watch digestion races.** Every process starts a `FileSystemWatcher` for *every* registration,
  not just its own project (`Dependencies.cs:144`; `SelectWatches` has no project predicate,
  `MemorySql.cs:287-292`) — 5 processes × 7 registrations = 35 concurrent watchers on 7
  directories. The scan lease (`watches.scan_owner`) guards only the catch-up scan.
  `WatchDigestExecutor.DigestAsync` (`WatchDigestExecutor.cs:39-53`) is an unguarded, untransacted
  read-modify-write that writes the content hash *last*, so all N read the same stale hash, none
  skips, and each deletes every chunk for the path before re-ingesting. Between one process's
  delete and its re-ingest the file returns nothing from `memory_search`; if that process is killed
  in the window — and stdio processes are killed on client session end — the chunks stay gone until
  the file changes again, because the hash row another process already wrote makes the next digest
  hash-skip.
- **Mixed-binary version lockout** (ADR-0019:68-77) has the fan-out as its mechanism:
  `dotnet tool update` replaces the binary while N processes keep their old assembly loaded.
- **Concurrent migration.** `MemorySchema.EnsureAsync` reads `PRAGMA user_version` outside any
  transaction; N processes racing the first open after an upgrade all enter the same step, and
  `RebuildVecTableAsync` drops and rebuilds `vec_entries` over 12,890 rows once per process.
- **Every `memory_search` is a writer.** `SearchAsync` ends in an unconditional `BumpAccessAsync`
  (`SqliteMemoryStore.cs:258`), a per-hash SELECT-compute-UPDATE with no transaction. The count
  increment is atomic; the rating is not, so rating and access count silently diverge — and both
  are ranking and sweep inputs.

Observability is the least of it, and is misdescribed as "stdio skips the HTTP server".
Instrumentation runs identically in every process — the recording code lives in the tool bodies and
is transport-independent. What a stdio process lacks is a *listener*: no `MeterListener` is ever
attached so every metric record is a no-op, no `ActivityListener` means `StartActivity` returns
null and the span is never allocated, and `/observability` is behind the HTTP guard
(`McpServerSetup.cs:126-130`). The loss is aggregation and reach, not recording.

`VACUUM` is also structurally dead: the 7-day clock is seeded per process
(`BankMaintenanceHostedService.cs:120-125`) and no stdio process lives 7 days.

The owner's requirement is that all traffic goes through one HTTP server, started on first use,
**without re-editing every installed client config**. That constraint eliminates the obvious
alternative — putting an HTTP `url` entry in each client config — because no MCP client launches a
process for a `url` entry, so nothing would ever start the server.

## Decision

**The stdio entry point becomes a thin proxy to a single HTTP server, and that is the default.**

- A new `McpTransport.Proxy`, and `DefaultOptions.Transport` changes from `Stdio` to `Proxy`. This
  is the only lever that reaches installed clients: `.mcp/server.json:14` declares
  `"packageArguments": []` and the ai-badger declaration is a bare `"command": "ai-raccoon"`.
  Neither file changes, and `"transport": {"type": "stdio"}` in `server.json` stays true — from the
  client's side the proxy *is* a stdio MCP server.
- `Program.cs` gains a pre-branch to `ProxyRunner` before the bare-launch host construction. The
  proxy resolves no encryption key, opens no bank, and loads no ONNX model — the work
  `Program.cs:41-51` does today, and the reason a first call currently costs a 254 ms median
  against 43 ms for every later call.
- **Acquire:** probe `/mcp`; if nothing answers, spawn `ai-raccoon serve` and poll the probe on a
  bounded budget. The N-way start race is already solved and tested — `ServeRunner`'s probe-attach
  (`ServeRunner.cs:47-50,148-178`), its `AddressInUseException` catch plus one re-probe (`:69-81`),
  and the two-real-process test at `ServeRunnerTests.cs:135-183`. The proxy reuses that probe rather
  than adding any lock or lease of its own.
- **Forward:** one `IncomingFilters` entry switching on JSON-RPC *message kind*. Requests go to
  `backend.SendRequestAsync(request, ct)` with the client's id restored on the response;
  notifications go to `SendMessageAsync`; the local handler is suppressed by not calling `next`. No
  method is named anywhere in the proxy — adding a tool or an entire new MCP method requires no
  proxy change *travelling client→backend*, which is the only direction this relays (see the
  one-directional constraint in Consequences). `initialize` is forwarded, not synthesised, so capabilities are the backend's. The
  local server registers no tools, so an interception failure surfaces as an empty tool list rather
  than a second server quietly opening the bank.
- **`--transport stdio` keeps today's behaviour exactly** — a complete in-process server, no proxy,
  no autostart. It is the escape hatch and the E2E suite depends on it.
- **Failure is loud.** If the backend can neither be reached nor started within the budget, the
  process exits `ExitCode.ProxyBackendUnavailable` with one stderr line naming the URL, the `serve`
  exit code, and `ai-raccoon --transport stdio`. There is no in-process fallback.
- **The proxy never kills or signals any process.** Daemon lifetime belongs to `IdleWatchdog` alone.
  When a forward fails at the connection level the proxy re-acquires once and retries once; it does
  not ping `/mcp` on a timer to keep the backend alive.
- **`/mcp` is guarded by a loopback token, shipped in this same change.** `serve` mints a random
  secret into `<data-root>/mcp-token` (0600, exclusive create, reused across restarts) *before* it
  binds, and the proxy reads that file after a successful probe and sends it as a header. Comparison
  is `CryptographicOperations.FixedTimeEquals`; generation is `RandomNumberGenerator.GetBytes`. No
  client config changes, so the zero-config property survives. `/observability` stays open — it
  returns a PID and OTLP status, nothing that touches the bank, and ADR-0008's discovery depends on
  it being reachable. The probe also stays unauthenticated and gains 401 as an accepted status, so
  it keeps recognising a live server across data roots. Full design:
  [the token flow](../plans/2026-08-09-mcp-loopback-token-flow.md).

## Consequences

- **Positive.** One process, one bank opener, one ONNX session, one watcher set. The watch
  digestion race, the concurrent-migration rebuild, the `BumpAccessAsync` rating divergence and the
  promotion-eviction race stop being reachable as a class, rather than being fixed one at a time.
  Metrics, traces, `/observability` and OTLP export begin covering all traffic without any new
  instrumentation. `VACUUM`'s 7-day clock becomes meaningful for the first time. Cold-start cost
  moves off the client's first call.
- **Positive, unplanned.** The ai-badger scaffolder blocker dissolves. `_render_entry`
  (`mcp_tools.py:420-437`) can only emit `command`/`args`/`cwd`/`env`/`tools`, and
  `stack-mcp.schema.json` is `additionalProperties: false` with no `url`. That blocked an HTTP `url`
  entry; a proxy needs a spawnable `command`, which is exactly what the scaffolder emits. R16's
  compromise is now satisfied by design instead of by concession, and no upstream change is needed.
- **Mixed — the "opt-in" mitigation is removed and replaced, not simply dropped.**
  `SECURITY.md:50-51` names the old mitigation explicitly: *"Keep the HTTP endpoint opt-in and
  loopback-only for the same reason: an unauthenticated `localhost` listener is reachable by any
  local process."* This decision removes the "opt-in" half — a server now runs whenever any agent
  has touched memory, exposing 22 tools including delete and sweep, and the settings table holding
  cloud-sync credentials. The loopback token above replaces it, raising the bar from "any local
  process" to "any process that can read a 0600 file in the data root". That is a bar, not a
  boundary: a process running as the same user can read the file. On Windows the 0600 does not
  apply at all and the file inherits the directory ACL. `SECURITY.md:37-38` is corrected in the
  same change: "stdio transport (default)" and "HTTP transport (opt-in)" are both now false.
- **Negative — a behaviour change for every installed client.** Bare `ai-raccoon` no longer serves
  memory by itself. A machine where `serve` cannot start loses memory entirely rather than
  degrading, which is deliberate (see Alternatives) but is a real blast radius: one broken backend
  affects every agent on the machine at once.
- **Negative — the daemon may die with a client's process group.** No portable detach exists in
  .NET, `setsid` has no macOS binary, and the archive design already rejected that machinery as
  fragile. Accepted rather than engineered around: the next proxy start simply restarts it, at one
  cold start.
- **Neutral — ADR-0009's stdio OTLP exclusion stays in force and simply stops mattering.** That
  exclusion (ADR-0009 §"Which host paths get the exporter"; owner decision 2026-08-07, reversing an
  earlier symmetric call) is still correct: a per-connection process that recycles every few minutes
  cannot pay the exporter's 5 s batch schedule delay plus the non-configurable 5 s per-provider
  shutdown grace. The proxy is exactly such a process — and it now has nothing to export, because it
  records nothing. The server it forwards to does. `CreateAppHost` keeps no exporter,
  `OtlpExportTests.cs:153-164` keeps passing unchanged, and the practical coverage gap the exclusion
  created closes without the exclusion being touched.
- **Neutral.** `McpEntryRenderer` and `serve --mcp-entry` are unaffected; a direct `url` entry
  remains valid for anyone who wants one.
- **Constraint — the relay is one-directional, so "no proxy change" holds only client→backend.**
  `ProxyForwarder` filters the client's incoming messages and registers no notification or request
  handler on the backend session, so anything the backend originates — `notifications/progress`,
  `notifications/message`, `tools/list_changed`, a `sampling/createMessage` request — is dropped
  rather than relayed. Nothing emits these today, which is why it costs nothing yet; the first tool
  that reports progress or the first server-initiated sampling call needs a reverse relay built,
  not just a tool added.
- **Constraint for later readers — the proxy host composes no tool-layer filters.** Bare
  `ai-raccoon` executes no tools; it forwards. Anything registered beside `ToolRefusals.Filter`
  (`McpServerSetup.cs:169`) — tool metrics, refusal policy, a `CallToolFilter` — belongs on the
  **backend host only**. Registered on the proxy it would mint tool-layer signal for calls that
  process never ran. The proxy's forwarding sits at the same seam via `IncomingFilters`, which is
  what makes the mistake easy to make.
- **Constraint — the proxy propagates no `traceparent`, and that is load-bearing elsewhere.** It
  registers no tools and wires no exporter, so no `ActivityListener` exists, `HttpClient`'s
  `DiagnosticsHandler` is bypassed, and no trace context reaches the backend. `HttpRequestIn` on
  the server therefore stays a trace root. If the proxy ever propagated context, ASP.NET's span
  would become a child of a remote parent, `ParentBased` would honour the proxy's unrecorded
  Activity, and every server span would be dropped. A test pins the absence of the header on the
  wire; ADR-0021's sampler decision depends on it.
- **Constraint — the backend's error status, and why `JsonRpcErrorHandler` exists.** From revision
  `2026-07-28` on — the SDK's default, so the proxy's own session negotiates it — the backend maps
  JSON-RPC error codes onto HTTP statuses: an unknown method answers **404 with
  `text/event-stream`** carrying `-32601` and a correlating id. `StreamableHttpClientSessionTransport`
  converts an error body only when the content type is `application/json`, so the client would
  otherwise get a bare `HttpRequestException` that the SDK flattens to `-32603`, and
  `resources/list` / `completion/complete` would read as "backend broken" rather than "capability
  absent". On `2025-06-18` and `2025-11-25` the same methods answer **200**, which is why the
  handler looks dead if it is measured on a pinned older revision. It is driven off the body and
  never off the status, and only rewrites an error carrying an id the client can correlate — a null
  id (the token gate's 401, a malformed POST's 400) stays a failure, because rewritten to 200 it
  would correlate with nothing and the SDK would drop it.
- **Negative — `OTEL_*` becomes machine-wide, decided by whichever client starts the backend
  first.** The spawned `serve` inherits the proxy's environment, and the proxy is spawned by every
  MCP client. So one project's `.mcp.json` `env` block fixes the OTLP configuration for every other
  client's traffic, and a malformed endpoint that kills `serve` at boot takes memory down for all
  of them rather than for one session.

## Non-Goals

- **Authorizing callers.** The token proves the caller can read a file in the data root; every
  holder still gets the full tool surface, and the `ro`/`rw`/`full` access mode remains the only
  privilege split.
- **Transport security.** Traffic stays plaintext on loopback; TLS on 127.0.0.1 would protect
  against nothing that can already read the token file.
- **An explicit Windows ACL on the token file.** `UnixFileMode` is POSIX-only; on Windows the file
  inherits the data-root directory's ACL. Recorded as a real limitation, not papered over.
- **Gating `ai-raccoon --transport http` when started directly.** The token guards `serve`, which
  is the path the proxy autostarts and therefore the one this decision makes always-on. A manual
  `--transport http` launch stays ungated: it is opt-in and deliberate, i.e. exactly the posture
  `SECURITY.md` accepted before this ADR, so it is not a regression from this change — and closing
  it would require the E2E `McpServerFactory` to read the token. It is a genuine hole for anyone
  who runs that command, and `SECURITY.md` names it rather than implying every listener is
  authenticated.
- **Detaching the daemon from the client's process group.** No pid file, no orphan reaping, no
  SIGTERM forwarding. Reconnect replaces detachment.
- **Keeping the backend alive with proxy-side pings.** Rejected: it would make `IdleTimeout`
  unreachable on any machine with a client open and silently invalidate `IdleWatchdogTests`.
- **Fixing the watch digestion race by collapsing to one server.** Collapsing makes it unreachable,
  not fixed; `--transport stdio` still reaches it. The underlying unguarded read-modify-write in
  `WatchDigestExecutor.cs:39-53` is fixed on its own merits in the same change as this ADR, not by
  this ADR.

## The loopback token

Originally drafted as a deferred follow-up; **folded into this decision on owner instruction,
2026-08-09**, on the grounds that shipping the transport change without it is a real regression
against `SECURITY.md:50-51` rather than a small one.

The full flow — mint-before-bind ordering, the exclusive-create race, the 401 status that keeps
`ServeRunner`'s probe working across data roots, and the failure-mode table — is
[the token flow](../plans/2026-08-09-mcp-loopback-token-flow.md). It ships in the same change as
the proxy.

## Alternatives considered

### An HTTP `url` entry in every client config (no proxy)

Rejected: it cannot meet the requirement. No MCP client launches a process for a `url` entry, so
nothing starts the server, and the entry simply fails if the server is down when the client boots.
`McpEntryRenderer.cs:6-10` already renders the exact JSON for anyone who wants this manually.
Secondary blocker at the time: the ai-badger scaffolder could not emit a `type`/`url` entry at all.

### `--transport proxy` as an opt-in, or a separate `ai-raccoon proxy` verb

Rejected against the stated constraint. Every installed client invokes the binary bare
(`server.json:14`, `stack-mcp.json`), so an opt-in value or a new verb reaches nothing already
installed and requires editing every client config on every machine.

### Falling back to the in-process stdio server when the backend cannot start

Rejected. A fallback here cannot be made noticeable: stdio stderr is discarded by clients and there
is no server log under `~/.ai-raccoon`, so the one channel a fallback could complain on is already
proven to be swallowed. It would be unobservable by construction, permanently reinstating the
fan-out with no signal. Further, of the ways autostart can fail, only "a foreign process owns the
port" would not also have failed the in-process server — and in that case the fallback is actively
wrong, because it hides a condition the operator must fix. Failing loudly with a documented escape
hatch turns silent degradation into an operator decision.

### A hand-maintained table of forwarded methods

Rejected under "derive the list, or delete it". A method table drifts the moment the backend gains a
tool, and the failure is invisible: the proxy would answer `tools/list` from its own stale list and
the tool surface would silently diverge from the server's on every release. The message-kind switch
has no list to drift.

### A raw stdin/stdout frame pump instead of the SDK filter hook

Held in reserve, not adopted. It is immune to both risks in the filter approach (no request-id
rewriting, `initialize` is just another frame) and is legitimate because `Stateless = true` and
SEP-2567 removed `Mcp-Session-Id`, so every frame is independent and no state machine is needed. Not
adopted first because it re-implements stdio framing and SSE unwrapping that `HttpClientTransport`
and `StdioServerTransport` already do correctly. It is the documented exit if the filter hook cannot
intercept `initialize`.

**Evidence:** `docs/work/2026-08-09-mcp-transport-analysis.md` (the research record, with file:line
for every claim above); `ModelContextProtocol.Core 2.1.0` public surface verified by reflecting
`lib/net10.0/ModelContextProtocol.Core.dll` — `McpServerOptions.Filters.Message.IncomingFilters`
(`IList<McpMessageFilter>`), `McpMessageFilter(McpMessageHandler next)`,
`McpMessageHandler(MessageContext, CancellationToken)`, `McpClient : McpSession`,
`McpSession.SendRequestAsync(JsonRpcRequest, CancellationToken)`,
`McpSession.SendMessageAsync(JsonRpcMessage, CancellationToken)`;
`src/AiRaccoon/Setup/McpServerSetup.cs:53,80,95,126-130,196`;
`src/AiRaccoon/Setup/Serve/ServeRunner.cs:47-50,69-81,87,148-178`;
`src/AiRaccoon/Setup/Serve/McpActivityMiddleware.cs:15`; `src/AiRaccoon/Setup/DefaultOptions.cs:8`;
`src/AiRaccoon/Program.cs:33,41-51`; `src/AiRaccoon/.mcp/server.json:11-14`;
`SECURITY.md:37-38,50-51`; [ADR 0009](0009-otlp-export.md) §"Which host paths get the exporter";
[ADR 0019](0019-forward-version-write-guard.md):68-77;
`docs/work/archive/2026-08-06-http-serve-design.md:137-146`;
`docs/plans/2026-08-06-http-serve-mode-plan.md:33-34` (R15, R16);
`docs/work/2026-08-07-cross-session-agent-coordination.md` §"never reap a shared daemon".
