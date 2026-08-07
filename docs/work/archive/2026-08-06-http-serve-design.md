# HTTP serve mode — design

Date: 2026-08-06 · Branch: `task/switch-from-stdio-to-http` · Author: architect

Status: **reviewed — MoE rulings folded (4 lanes, 2026-08-06)**. Rulings record with
finding→ruling table: `docs/plans/2026-08-06-http-serve-mode-plan.md`. Lane reports:
`docs/work/2026-08-06-http-serve-moe-{code-reviewer,test-engineer,dotnet-engineer,protocol-switch}.md`.

This document is the build contract for the "HTTP serve mode" feature. Every feature
point names its acceptance criteria and the gate (test run / manual run) that proves it.
Implementation happens in parallel work packages (see "Implementation split"), one PR
per package, TDD-mandatory, in this worktree only.

---

## 1. Context & motivation

The user wants three things, in order:

1. **A CLI verb that launches the tool as an HTTP MCP server** — minimal config:
   `ai-raccoon serve` → HTTP MCP server launches, opens a port, returns the URI.
2. **A `--mcp-entry` mode** on that command that prints the MCP client config entry
   (JSON) to paste into a client config — Hermes `config.yaml` `mcpServers` section or
   Claude Code `.mcp.json`.
3. **A global idle watchdog** — every MCP tool call signals the watchdog; after 4
   hours without activity the server shuts itself down.

Why this matters now — the **stdio-recycle fact**: the stdio transport is per-connection.
The Hermes gateway recycles stdio MCP servers every ~5 minutes (measured live: 1048
server registrations/day, each ending in a clean stdin-EOF shutdown). A stdio process is
therefore *ephemeral by construction*: it lives minutes, not days. Anything that needs a
long-lived process — persistent HTTP endpoint, background hosted services — must run on
the HTTP transport, which today is reachable only via the undocumented
`ai-raccoon --transport http` flag. Serve mode makes that a first-class, discoverable,
safe-to-leave-running operation: the watchdog guarantees a forgotten server cannot leak
forever.

---

## 2. Current state (verified against code)

- **HTTP serving already exists.** `ai-raccoon --transport http --port <n>` builds a
  web host (Kestrel, `IPAddress.Loopback`, never the ASP.NET default 5000), maps MCP at
  `/mcp`, default port 7721, `--port 0` = random. `McpTransport` enum:
  `Stdio=0, Http=1, Https=2` (https declared, unsupported — warning only).
  (`src/AiRaccoon/Setup/McpServerSetup.cs`, `Setup/Cli/CliCommandTree.cs`)
- **Bound-URL discoverability is a stderr log only.** `Log.HttpTransportListening`
  (EventId 2, "ai-raccoon: http transport listening on {Urls}") is emitted in
  `HostExtensions.RunAsync` after `StartAsync` — the only channel that reveals a random
  port. Note it is duplicated verbatim in `McpServerSetup.Log` (same EventId 2); serve
  mode must not add a third copy.
- **Host dispatch.** `McpServerSetup.CreateServerHost(ServerConfig, transports)`:
  stdio-only → plain app host (no web server); any HTTP/S → web host; both → web host
  with stdio attached. Long-lived hosted services are gated to HTTP hosts:
  `RegisterMemoryServices(options, registerExtractionHostedService: true)` registers
  `ExtractionHostedService` only for HTTP hosts (PR #58 precedent) — a
  `BackgroundService` in a stdio-only host never fires because the process is recycled
  in minutes.
- **CLI layer.** `CliArgs.TryParse` (System.CommandLine 2.0.10) + `CliCommandTree`
  verb tree (verbs: `access, model, retrieval, sweep, sync, watch, encryption, extract`).
  No verb → launch server. Verb path → `CliCommandRunner.RunAsync` →
  `ConfigCommands` static dispatcher over injectable command components
  (`Setup/Cli/Commands/`, `IEncryptionCommands` pattern). `CliCommandTreeTests` pins the
  verb list; `CliArgsTests` pins parse behavior. `CliOptions` carries launch identity
  (Transport, DataRoot, InstallScope, Port, IsPortExplicit); `CliOptionsExtensions.ToServerConfig`
  builds `ServerConfig`; `DefaultOptions` holds defaults; `ExitCode` has `Success=0,
  FailedToResolveEncryptionKey=1, FailedToOpenEncryptedBank=2`.
- **stdout contract.** stdout is reserved for the stdio protocol *when stdio transport
  is active* (`mcp.instructions.md`; `CliArgs.TryParse` "never writes anything"; config
  commands print *results* to stdout, diagnostics to stderr). Because `serve` forces
  http-only transport, **stdout is free for serve's URI/JSON output** — verified
  reasoning.
- **Client side.** Hermes accepts URL-based entries (the user's `~/.hermes/config.yaml`
  already has `url:`-only entries, e.g. `rider`, `llmstudio` — no `type`/`transport`
  key). Claude Code `.mcp.json` uses `{"type": "http", "url": ...}` entries.
- **SDK hooks available (new finding).** `ModelContextProtocol.AspNetCore` 2.1.0:
  `app.MapMcp("/mcp")` is plain ASP.NET Core routing, so framework middleware can wrap
  it. The SDK additionally exposes `WithRequestFilters` /
  `AddCallToolFilter(builder, McpRequestFilter<CallToolRequestParams, CallToolResult>)`
  where `McpRequestFilter<TParams,TResult>` is a wrapper delegate
  `McpRequestHandler<TParams,TResult> Invoke(McpRequestHandler<TParams,TResult>)` —
  a composable, non-replacing hook around the tools/call handler only.
- **System.CommandLine 2.0.10 has no `TimeSpan` option type** (new finding) —
  `--idle-timeout 4h` needs a small custom parser (pure static function).
- **Test conventions.** xunit v3 + Shouldly; `FakeTimeProvider`
  (Microsoft.Extensions.Time.Testing) with `PeriodicTimer(period, timeProvider)` is the
  proven hosted-service test pattern (`ExtractionHostedServiceTests`); host-shape gates
  live in `McpServerSetupHostTests` (`ShouldContain/ShouldNotContain` on
  `IHostedService`); E2E boots the real host in-process via `WebApplicationFactory`
  (`McpServerFactory`) with an `McpClient` over `HttpClientTransport`; `E2ETestCollection`
  serializes E2E; `TestCategories` traits mark Unit/Fast and E2E/Slow.
- **LoggerMessage EventIds in use:** 1–5, 100–101, 200–205, 300–301, 310, 320, 330,
  400, 500–506. **The 600-series is free** — new Log classes use it.

---

## 3. Design

### 3.1 Verb shape — new top-level `serve` verb

**Decision: `ai-raccoon serve [--port N] [--idle-timeout <span>] [--mcp-entry [--format hermes|claude|all]]`.**
No separate `mcp-entry` verb.

- The sketch's "switch" (stdio→http) already exists as `--transport http`; what is
  genuinely new is (a) the watchdog, (b) the client-config output, (c) a discoverable
  entry point with the right defaults. A verb packages all three; bolting
  `--mcp-entry`/`--idle-timeout` onto the bare launch flags would clutter the minimal
  launch surface and leave the feature undocumented.
- `--mcp-entry` lives **on** `serve` (per the sketch), not as a separate verb: it needs
  the *bound* URL (random ports), which only exists once the server is listening — see
  3.2. A pure render-and-exit `mcp-entry` verb for a fixed port is a possible later
  addition (out of scope today).
- `serve` owns its launch identity: it defines its own `--port` (default 7721). Root
  pre-verb launch options behave as follows:
  - `--data-root`, `--install-scope` — respected (bank identity; they already flow to
    every verb via `CliOptions`/`ServerConfig`).
  - `--transport` — **forced to http**; if `--transport` was explicitly given and is not
    http, log a one-line warning on stderr (see open question Q5). The command help
    states "always HTTP".
  - root `--port` — ignored; serve's own `--port` wins (documented in help).
- `serve` is added to `CliCommandTree.Verbs` so `CliArgs.ContainsVerb` routes it, and
  its option instances are exposed as `internal static readonly` fields on
  `CliCommandTree` so the runner can read values (`parseResult.GetValueForOption(...)`),
  mirroring how `ConfigCommands` receives the `ParseResult`.
- Program.cs routing: the `["serve"]` path is handled **before** the generic
  `CliCommandRunner` branch (which would otherwise hit its `_ => throw` catch-all).

### 3.2 Serve semantics — foreground, blocking, honest URL

**Decision: `serve` blocks in the foreground.** The user backgrounds it themselves; the
shell recipe is printed in the command help:

```
ai-raccoon serve > serve.log 2>&1 &
```

- **Why not detached daemon (spawn self via Process.Start / nohup-style):** (1) the URL
  must be *actually bound* before it is reported — a detached parent can only relay the
  child's stdout, which means building a pid-file/status/stop/port-liveness machinery
  for zero user benefit; (2) the watchdog already solves the "forgotten server" problem
  that detachment is usually bought for; (3) testability — in-process host tests
  (`WebApplicationFactory`/`CreateServerHost`) match a foreground shape exactly;
  (4) honest semantics on macOS/Linux: the shell already does detachment correctly, and
  re-implementing it in-process (redirects, orphan reaping, SIGTERM forwarding) is
  fragile. "Ask if a simpler shape would do" → foreground is the simpler shape.
- **Sequence** (new `Setup/Serve/ServeRunner.cs`, mirroring the bare-launch flow in
  Program.cs): force `Transport = Http` → build host via
  `McpServerSetup.CreateServerHost` → resolve encryption key → probe bank decryption →
  `EnsureEmbeddingAvailabilityAsync` → `StartAsync` → **print to stdout** (one line
  `http://127.0.0.1:<port>/mcp`, or the `--mcp-entry` JSON — 3.4) → `WaitForShutdownAsync`
  → return exit code. Logs stay on stderr (existing `AddConsole(LogToStandardErrorThreshold)`).
- The existing `HostExtensions.RunAsync` (stderr log) is **unchanged** for the bare
  launch path. Serve's stdout reporting is a serve-only path (ServeRunner), so no new
  duplicate of `Log.HttpTransportListening`.
- **Busy port — probe first, attach if it is already an ai-raccoon server, else fail
  fast** (R14, full detail in §3.7). Probe `POST /mcp` with `Accept: application/json,
  text/event-stream` and a non-JSON body; recognized iff status ∈ {400, 405, 406} AND
  body contains `"jsonrpc"`; 2 attempts, 1s timeout. Probe miss → bind. A bind
  `AddressInUseException` (concurrent-start race) → re-probe once → attach, or return
  `ExitCode.PortInUse = 3` (new const) with the actionable stderr line
  `ai-raccoon: port {Port} is in use — pass --port 0 for a random port, or free the
  port`. Never auto-fallback to random: that would silently diverge from the URL the
  user configured/scripted — surprise beats honesty.
- **`--port 0`:** the bound URL is read from `web.Urls` after `StartAsync` (the pattern
  `HostExtensions.RunAsync` already uses) — that is the only correct source for a
  random port, and it is what gets printed.

### 3.3 Watchdog architecture

**Decision: one `BackgroundService` + one atomic timestamp, signaled from an ASP.NET
Core middleware on `/mcp`. Not two hosted services + Channel.**

The user sketch (2 hosted services + a channel) is the general "producers → consumer"
shape. The requirement is narrower: *a per-request timestamp reset and a periodic
comparison*. What the simple shape buys that the sketch does not:

- **One `Interlocked.Exchange` is the whole signal.** There is no queue, no ordering,
  no backpressure, no loss tolerance question — at localhost request rates the channel
  adds an unbounded buffer, a second service lifecycle, and two failure modes (queue
  growth, consumer crash → stale timestamps) for zero behavioral gain.
- What the sketch's channel shape *would* buy: decoupling signal production from
  consumption under high multi-producer load. That is not a real load profile for an
  MCP memory server on loopback. Declined per "ask if a simpler shape would do".

**Activity hook — Decision: framework middleware on `/mcp` (any request counts), not
the SDK `AddCallToolFilter` (tools/call only).**

- The middleware wraps `app.MapMcp("/mcp")` (plain ASP.NET Core routing, stable
  surface) and calls `IActivitySignaler.NotifyActivity()` per request. No JSON body
  parsing, no per-method branching: any request to `/mcp` (POST initialize/list/call,
  ping notifications, GET session streams) resets the idle timer.
- The SDK filter (`AddCallToolFilter`, wrapper delegate) would count *only* tools/call.
  Trade-off: middleware also counts cheap initialize/list/ping traffic. That is
  deliberate — an actively-connected client that lists tools or pings is *in use*;
  killing its server mid-session is worse than a slightly later reclamation. A client
  that initialized once and went silent stops resetting the timer at that point, so the
  server still dies 4h after real activity ceases. The literal "on any tool call"
  wording is satisfied in spirit (tool calls are the dominant traffic); the filter
  additionally risks SDK-upgrade drift (newer, less battle-tested API surface) and adds
  a hop inside the SDK dispatch pipeline.
- **What is NOT activity:** `ExtractionHostedService` passes (every 30–60 min), watch
  digests, sync runs. If background passes counted, an enabled extraction would reset
  the timer every pass and the server would never die. Only `/mcp` traffic signals. The
  middleware is the *only* caller of `NotifyActivity`.

**Components** (host/Setup layer — never domain; `Setup/Serve/`):

- `IActivitySignaler` — `void NotifyActivity()` (plain name; one method).
- `IdleWatchdog : BackgroundService, IActivitySignaler` — constructor
  `(TimeProvider, TimeSpan timeout, IHostApplicationLifetime, ILogger<IdleWatchdog>)`.
  `NotifyActivity()` does `Interlocked.Exchange(ref _lastActivityTicks,
  _timeProvider.GetUtcNow().UtcTicks)`. `ExecuteAsync` runs a
  `PeriodicTimer(tick, _timeProvider)` loop with `tick = min(60s, IdleTimeout/4)`
  (R2: a fixed 60s tick makes a 2s-timeout host shut down up to 62s late); on each tick, if
  `now - lastActivity > timeout` → `Log.ShuttingDownIdle` (EventId 611) →
  `_lifetime.StopApplication()` → return. Baseline: `_lastActivityTicks` is initialized
  to now at start, so a fresh server lives a full timeout even with zero requests
  (correct — it was just started, not forgotten). Shutdown is graceful: Kestrel stops,
  extraction/watch/sync hosted services receive cancellation, `WaitForShutdownAsync`
  returns, exit code `Success` (an intentional, clean shutdown).
- `McpActivityMiddleware` — resolves `IActivitySignaler` (singleton) and calls
  `NotifyActivity()` for requests with `request.Path == "/mcp"` (R4: 404s on other
  paths must not count), then `await next()`. 1-3 line comment contract.
- Wiring in `McpServerSetup.CreateWebHost`: when
  `config.IdleTimeout > TimeSpan.Zero`, register the watchdog with THREE
  registrations, one instance (R1 — `AddHostedService<T>` registers only
  `IHostedService→T`, so a middleware `GetRequiredService<IdleWatchdog>()` would
  throw on the first request):
  `services.AddSingleton<IdleWatchdog>()`,
  `services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>())`,
  `services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>())`;
  and add the middleware before `MapMcp("/mcp")` in `ConfigureMcpEndpoints`
  (verified: .NET 10 splices post-Build `Use()` between routing and endpoints, so the
  middleware wraps the MCP endpoint).
- **Registration is HTTP-gated by construction** (registered in `CreateWebHost` only,
  never `CreateAppHost`) — same rule as `ExtractionHostedService`, stated here
  explicitly: a stdio-only host never gets the watchdog (and never needs it — the
  gateway recycles it in minutes). The watchdog fires only under HTTP hosting, and
  `serve` forces HTTP, so serve always has it.
- **Scope of the default (R3):** the 4h watchdog default applies to `serve` ONLY, by
  construction: `ServerConfig.IdleTimeout` defaults to `Zero` (bare `--transport http`
  never arms the watchdog — zero behavior change, no silent supervisor death);
  ServeRunner applies the 4h default when `--idle-timeout` is absent and the parsed
  value (0 = disabled) when present. No Program.cs involvement needed.

### 3.4 `--mcp-entry` output

**Decision: `serve --mcp-entry` blocks and serves.** It starts, binds, prints the JSON
config containing the **actually-bound** URL, then keeps serving (the JSON is printed
once listening — the entry is immediately usable, e.g. pasted into a client config in
another terminal; for scripting,
`ai-raccoon serve --mcp-entry --format hermes > entry.json 2> serve.log &` captures the
JSON cleanly — R5: never merge stderr into the entry file, it would corrupt the JSON).

`--format` values: `hermes` (default), `claude`, `all` (`all` prints both documents in
that order, blank line between, each single-line JSON). Renderer is a pure static
component `Setup/Serve/McpEntryRenderer.cs`:

```csharp
// hermes — paste under `mcpServers:` in ~/.hermes/config.yaml (JSON is valid YAML)
{"ai-raccoon":{"url":"http://127.0.0.1:7721/mcp"}}

// claude — paste as ~/.claude/.mcp.json (project) or ~/.mcp.json (user)
{"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:7721/mcp"}}}
```

- Hermes shape matches the URL-only entries already present in the user's config
  (no `type`/`transport` key needed; `hermes mcp add <name> --url <endpoint>` is the
  CLI equivalent).
- The entry key is the fixed server name `ai-raccoon` (matches the stdio registration
  users already have, so switching is a like-for-like edit).
- The URL always carries the `/mcp` path and `127.0.0.1` (Kestrel binds Loopback; print
  the loopback host, not `localhost`, so the entry works without DNS assumptions).

### 3.5 Config surface

- **Idle timeout:** default `DefaultOptions.IdleTimeout = TimeSpan.FromHours(4)`;
  `serve --idle-timeout <span>` overrides; `0` disables the watchdog.
  Parsing: SCL 2.0.10 ships a `TimeSpan` converter but without `4h` sugar, so
  `--idle-timeout` stays an `Option<string>` parsed by a pure static
  `IdleTimeoutParser.TryParse(string, out TimeSpan)` accepting `90s`, `30m`, `4h`, `1d`,
  and `0` (disabled); anything else is a parse error surfaced via `Option.Validators`
  (the only SCL-native parse-error hook in 2.0.10 — no `ParseArgument`).
  The parser is a pure function → static class is sanctioned.
- **Port fallback chain (R7):** serve's own `--port` wins; else the root `--port`;
  else `DefaultOptions.Port` (7721). The root option is never silently ignored. Reads
  are instance-based (`GetValueForOption(servePortOption)`) — name-based
  `GetResult("--port")` resolves the root option (DFS order) when both are present.
- **Settings table: not involved.** The single-channel ruling made the settings table
  the *runtime* configuration channel; idle timeout is launch identity (like `--port`),
  and the sketch demands minimal config. No env var either (env handling was removed by
  the same ruling).
- **Plumbing:** `ServerConfig` gains a fourth positional parameter
  `TimeSpan IdleTimeout = default` (Zero = disabled — the bare launch default).
  ServeRunner applies `DefaultOptions.IdleTimeout` (4h) when `--idle-timeout` is
  absent; `CliOptionsExtensions.ToServerConfig` does NOT set it (Zero for bare
  launches — R3 by construction, no Program.cs change); existing direct `ServerConfig`
  constructions in tests keep compiling with Zero (watchdog off) — no test churn.
  `CliOptions` is unchanged (serve reads its own options from the `ParseResult`).
- **Exit codes:** add `ExitCode.PortInUse = 3`. Watchdog shutdown returns `Success`.

### 3.6 Gating summary (rules this design obeys)

- Watchdog registered iff `config.IdleTimeout > TimeSpan.Zero` **and** HTTP host
  (`CreateWebHost` only). Stdio-only hosts never register it (extraction precedent).
- stdout carries only serve's URI/JSON line (http-only transport ⇒ protocol-free);
  everything else stays on stderr; new LoggerMessage classes use EventIds 601–603, 605
  (`ServeRunner.Log`) and 610–612 (`IdleWatchdog.Log`); grep before landing.
- MCP layer untouched; watchdog + middleware + renderer live in `Setup/Serve/`
  (hosting/CLI layer), never `Tools/` or `Core/`.
- No production code without a failing behavior test (per work package below).

### 3.7 Idempotent attach (R14 — owner f: "already serving → point at it")

`serve` on a busy port that already hosts an ai-raccoon server **attaches** instead of
failing — the WebSocket-style "protocol switch" was evaluated and deferred (R15; the
banner handshake has no standard and forces a ~110-line fork of the client SDK's stdio
transport; attach + url-config covers the need — see the plan doc §1 R15).

1. **Probe first, before any bank/key/embedding work.** `POST http://127.0.0.1:<port>/mcp`
   with `Accept: application/json, text/event-stream` and a non-JSON body. Recognized
   iff `status ∈ {400, 405, 406}` AND body contains `"jsonrpc"` (a TCP connect alone
   cannot distinguish an ai-raccoon server from a foreign listener; the body check is
   the discriminator — verified: the Stateless endpoint leaves GET unmapped, so POST
   with an invalid body is the deterministic probe). 2 attempts, 1s timeout each.
2. Probe miss → bind (Kestrel). A bind `AddressInUseException` (concurrent-start race)
   → **re-probe once** → attach, or return `ExitCode.PortInUse` (3).
3. **Attached mode:** print the same URL line to stdout, an attach-provenance line to
   stderr (EventId 605), exit 0. Never arms the watchdog (the owning process owns the
   4h timer), never touches the bank, never reads settings. `--idle-timeout` in
   attached mode is ignored (documented).
4. `PortInUse` (3) remains reachable only for foreign/non-HTTP listeners (TCP-open but
   unrecognized probe body, or probe timeout).

---

## 4. Acceptance criteria

| # | Point | Acceptance | Gate (the run that proves it) |
|---|-------|-----------|------------------------------|
| A1 | `serve` verb exists | `CliCommandTreeTests.Root_ExposesAllVerbFamilies` expects `serve`; `ai-raccoon serve --help` renders serve options (`--port`, `--idle-timeout`, `--mcp-entry`, `--format`) and the backgrounding recipe | updated `CliCommandTreeTests` + `CliArgsTests`; `dotnet test --filter "Category=Unit"` |
| A2 | `serve` launches HTTP on default 7721 and prints the URI to stdout | `ServeRunner.RunAsync(config, stdout, stderr, ct)` with a free port prints exactly `http://127.0.0.1:{port}/mcp` to `stdout` (StringWriter) after binding, logs to stderr, exits 0 after `StopApplication()` | new `ServeRunnerTests` (unit, in-process host, temp data root); `dotnet test --filter "FullyQualifiedName~ServeRunnerTests"` |
| A3 | `--port 0` reports the bound URL | with `Port = 0`, stdout contains the ephemeral port actually bound (`web.Urls`), and an `McpClient` over `HttpClientTransport` reaches `/mcp` at that URL | `ServeRunnerTests` + `ServeE2ETests` (real host via `McpServerFactory`-style in-process boot) |
| A4 | busy port fails fast | with the port held by a `TcpListener`, `ServeRunner.RunAsync` returns `ExitCode.PortInUse` (3) and stderr contains "in use" + the `--port 0` hint; no stack trace | new `ServeRunnerTests` busy-port case; `dotnet test --filter "FullyQualifiedName~ServeRunnerTests"` |
| A5 | watchdog fires after idle | `IdleWatchdog` with `FakeTimeProvider`: settle real time BEFORE the first `Advance` (the first advance after `StartAsync` is lost — the timer isn't registered yet) and after each `Advance`; advance < timeout → `StopApplication` NOT called; advance past timeout → called exactly once (call counter); a `NotifyActivity()` mid-way resets the timer (invocation counter on the fake signaler or on the watchdog's own state) | new `IdleWatchdogTests` (FakeTimeProvider + NullLogger, `PeriodicTimer` pattern from `ExtractionHostedServiceTests`) — R8 |
| A6 | watchdog wired to real traffic | deterministic fake-clock host test: test host registers `FakeTimeProvider` + short `IdleTimeout`, one MCP tool call resets the timer (advance past timeout → no shutdown; advance again past timeout → `StopApplication`); plus a generous real-time smoke (2s timeout, shutdown observed within ~5s) and a fake-clock `--idle-timeout 0` never-shuts-down case | new `ServeE2ETests` (in-process real host) — R9 |
| A7 | gating: stdio hosts never get the watchdog | `McpServerSetupHostTests`-style: stdio-only host registers no `IdleWatchdog`; HTTP host registers it when `IdleTimeout > 0`, not when 0 (pin `GetServices<IHostedService>().OfType<IdleWatchdog>()` — DI-smoke: a dropped registration must fail the test) | additions to `McpServerSetupHostTests` |
| A8 | background services are not activity | shared-FakeTimeProvider two-service test: advance past the extraction interval with extraction enabled → the extraction pass runs and the watchdog timer is NOT reset; a later advance past the timeout still fires shutdown. Grep-gate (review): only `McpActivityMiddleware` calls `NotifyActivity` | new `IdleWatchdogTests` two-service case + code-review gate on the WP2 diff — R10 |
| A9 | `--mcp-entry --format hermes` JSON | stdout parses as JSON equal to `{"ai-raccoon":{"url":"http://127.0.0.1:{port}/mcp"}}` (JsonDocument deep-equal, order-insensitive) with the bound port | new `McpEntryRendererTests` golden tests + `ServeRunnerTests` integration of renderer |
| A10 | `--mcp-entry --format claude` JSON | equal to `{"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:{port}/mcp"}}}`; `all` prints both documents in order | `McpEntryRendererTests` |
| A11 | idle-timeout parsing | `IdleTimeoutParser` matrix: `90s/30m/4h/1d/0` → expected `TimeSpan`; `4x`, ``, `-1` → invalid | new `IdleTimeoutParserTests` |
| A12 | no regression on bare launch | existing launch paths unchanged: `McpServerSetupHostTests` suite green; bare `--transport http` still logs `HttpTransportListening` on stderr and runs WITHOUT the watchdog (`ServerConfig.IdleTimeout` defaults to Zero — R3) | full `dotnet test` |
| A13 | serve routing | Program.cs routes `["serve"]` to ServeRunner BEFORE the verb branch (the generic `CliCommandRunner` branch); `ai-raccoon serve` with `--data-root` respected | `CliCommandRunnerTests` (no `serve` path leaks into `ConfigCommands`) + A2 gate — R11 |
| A14 | attach on existing server | probe-recognized listener → `serve` exits 0, stdout = the URL line, stderr has the attach provenance, NO second host bound (the first process still owns the port), watchdog count in the first process unchanged | `ServeRunnerTests` attach cases (first ServeRunner owns the port; second attaches) |
| A15 | bind-race recovery | two ServeRunner instances started together on the same port: exactly one owns the port; the other attaches (exit 0) or returns `PortInUse` if the probe is unrecognized; no crash, no stack trace | `ServeRunnerTests` race case (barrier-synchronized double start) |
| A16 | foreign listener → PortInUse | a `TcpListener` (non-HTTP) holds the port → `serve` exits 3 with the "in use" stderr line + `--port 0` hint; the listener is held OPEN for the whole test (no port race) | `ServeRunnerTests` busy-port case — F8 |

---

## 5. Out of scope

- Detached/daemon mode: no `serve --daemon`, no pid files, no `serve stop`, no
  launchd/systemd/windows-service integration. The shell recipe
  (`ai-raccoon serve > serve.log 2>&1 &`) is the supported backgrounding path
  (POSIX-only; Windows guidance out of scope).
- The stdio→http banner "protocol switch" (R15, deferred — see plan doc §1).
- HTTPS, authn/authz, CORS — loopback-only server stays unauthenticated (unchanged
  from today's HTTP transport).
- `.mcp/server.json` registry entry (stays stdio; serve mode does not change the
  packaged registry metadata).
- Changing the stdio default transport or the bare-launch flag surface
  (no new root options; `--idle-timeout` is serve-scoped).
- Settings-table rows for idle timeout; env-var config channels (ruling).
- A standalone `mcp-entry` render-and-exit verb (possible follow-up).
- Client-side tooling changes (no Hermes/Claude Code edits).

---

## 6. Open questions — ruled by the MoE review (2026-08-06)

1. **Q1 — watchdog scope.** RULED (R3): default applies to `serve` only, by
   construction — `ServerConfig.IdleTimeout` defaults to Zero, so the bare
   `--transport http` path never arms the watchdog (no behavior change, no silent
   supervisor death).
2. **Q2 — activity = any `/mcp` request.** RULED (R4): middleware, path-branched to
   `/mcp`. Guarantee restated honestly as "zero HTTP traffic for 4h" — a ping-keepalive
   client keeps the server alive by definition; with `Stateless=true` there are no
   sessions to protect.
3. **Q3 — `serve --mcp-entry` blocks and serves.** RULED: keep blocking (honest for
   `--port 0`); recipe split-streams (R5).
4. **Q4 — one service + atomic timestamp.** RULED (R6): confirmed; the channel shape
   is over-engineered for loopback.
5. **Q5 — http forcing + port ownership.** RULED (R7): `--transport` mismatch → one
   stderr warning (https→stdio precedent); serve's `--port` wins, else root `--port`
   (never silently ignored), else 7721.
6. **Q6 — protocol switch (owner f:).** RULED (R15): deferred — no standard; client
   SDK fork cost; attach + url-config covers the need.
7. **Q7 — ai-badger `.mcp.json` serve default.** RULED (R16): transport-broken for
   stdio-shaped clients; `.mcp.json` stays stdio; the HTTP default lands as Hermes
   `url:` guidance + meta.json fixes.

---

## 7. Implementation split (parallel work packages)

| WP | Contents | Files (new: `+`, touched: `~`) | Depends on | PR |
|----|----------|-------------------------------|-----------|----|
| **WP1 — CLI surface + config plumbing** | `serve` command in tree + static option fields; `Verbs` array; `--idle-timeout` string option + `IdleTimeoutParser`; `ServerConfig.IdleTimeout` (default Zero) + `ToServerConfig` sets `DefaultOptions.IdleTimeout` (4h); `ExitCode.PortInUse`; help text + backgrounding recipe | `~ CliCommandTree.cs`, `~ CliArgs.cs`, `~ CliOptionsExtensions.cs`, `~ ServerConfig.cs`, `~ DefaultOptions.cs`, `~ ExitCode.cs`, `+ Setup/Serve/IdleTimeoutParser.cs` | — | PR 1 |
| **WP2 — watchdog** | `IActivitySignaler`, `IdleWatchdog` (BackgroundService, Interlocked timestamp, PeriodicTimer via TimeProvider, StopApplication), `McpActivityMiddleware`, registration in `CreateWebHost` + middleware before `MapMcp`; EventIds 610–612 | `+ Setup/Serve/IdleWatchdog.cs`, `+ Setup/Serve/McpActivityMiddleware.cs`, `~ McpServerSetup.cs` | WP1 (ServerConfig.IdleTimeout) | PR 2 |
| **WP3 — serve runtime + reporting** | Program.cs `["serve"]` routing ahead of `CliCommandRunner`; `ServeRunner` (force http, bootstrap, StartAsync, stdout URI/JSON, busy-port catch → `PortInUse`, WaitForShutdown); `McpEntryRenderer`; EventIds 601–604 | `~ Program.cs`, `+ Setup/Serve/ServeRunner.cs`, `+ Setup/Serve/McpEntryRenderer.cs` | WP1; parallel with WP2 (no shared files beyond none) | PR 3 |
| **WP4 — docs** | README "serve mode" section, feature doc pointer, design-doc follow-up | `~ README.md` | WP1–WP3 merged | PR 4 |

- **Serialization points:** WP2 and WP3 both touch nothing shared (WP2: McpServerSetup;
  WP3: Program.cs) — they can run in parallel after WP1. WP1 must land first (verb +
  config plumbing). WP4 last.
- **Test-first order within each WP:** write the failing test named in §4's gate column
  first (e.g. WP1: update `CliCommandTreeTests` verb list + new `IdleTimeoutParserTests`;
  WP2: `IdleWatchdogTests` + `McpServerSetupHostTests` additions; WP3: `ServeRunnerTests`
  + `McpEntryRendererTests` + `ServeE2ETests`).
- **Shared-file conflicts to watch:** only `Program.cs` (WP3) and `McpServerSetup.cs`
  (WP2) are touched by a single WP each, so no cross-WP file contention. `CliCommandTree.cs`
  is WP1-only. Branch-per-WP with sequential merges into `task/switch-from-stdio-to-http`.
- **Final gate:** `dotnet build` + `dotnet test` from the repo root green, plus the
  manual smoke: `ai-raccoon serve --mcp-entry --format claude` and a live
  `hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp` against a running server.

---

## Appendix — EventId allocation (600-series)

| Id | Class | Message |
|----|-------|---------|
| 601 | ServeRunner.Log | `ai-raccoon: serve listening on {Url}` (debug; the stdout line is the contract, this is the stderr trace) |
| 602 | ServeRunner.Log | `ai-raccoon: serve ignoring --transport {Transport}; serve always uses http` (warning) |
| 603 | ServeRunner.Log | `ai-raccoon: port {Port} is in use — pass --port 0 for a random port, or free the port` (error; also printed to stderr) |
| 605 | ServeRunner.Log | `ai-raccoon: attached to the server already listening on {Url}` (information; attach provenance, stderr) |
| 610 | IdleWatchdog.Log | `ai-raccoon: idle watchdog armed ({IdleTimeout})` (information) |
| 611 | IdleWatchdog.Log | `ai-raccoon: shutting down after {IdleTimeout} without MCP activity` (information) |
| 612 | IdleWatchdog.Log | `ai-raccoon: idle watchdog tick failed` (error) |
