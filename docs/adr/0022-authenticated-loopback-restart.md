# 0022 — `serve --restart` over an authenticated loopback shutdown

Date: 2026-08-09

Status: Accepted. Extends [ADR 0020](0020-always-on-http-stdio-proxy.md) (the always-on backend)
and closes the operational half of the "mixed-binary version lockout" that ADR-0020:36-37 names as
a motivation and [ADR 0019](0019-forward-version-write-guard.md) leaves recoverable only by "a
version update".

## Context

`serve` probe-attaches. If anything already answers on the port, `ServeRunner.cs:47-50` logs
`attached to the server already listening on …`, prints the URL and exits 0 — without touching the
bank, arming the watchdog or honouring `--idle-timeout`. That was the right call when a stale
server died with the client that started it.

ADR-0020 removed that property. There is now one always-on backend, started on first memory use and
retired only by `IdleWatchdog` after four idle hours. Nothing else ever cycles it. So after
`dotnet tool update -g ai-raccoon`:

- the running backend keeps the **old** assembly loaded — that is the exact mechanism ADR-0020:36-37
  names for mixed-binary lockout;
- every proxy that starts afterwards probes, finds it live, and attaches to the old binary;
- `serve` by hand attaches too and reports success;
- ADR-0019's recovery ("update the binary") is therefore incomplete: the binary on disk is new and
  the process serving memory is not.

The gap the owner reported: *"we need option to serve: --restart, right now there is no option to
restart running server on update"*. There is no `stop` verb and no `--restart` flag anywhere
(`CliCommandTree.cs:20` lists the verb families).

Two things already shipped that decide most of the design. `serve` mints a 32-byte secret into
`<data-root>/mcp-token` (0600) before it binds and guards `/mcp` with
`CryptographicOperations.FixedTimeEquals` (`McpTokenGate.cs`). And the host already knows how to
stop itself gracefully — `IdleWatchdog` calls `IHostApplicationLifetime.StopApplication()`
(`IdleWatchdog.cs:47`), the same path a restart needs.

## Decision

**`serve --restart` asks the running server to stop over an authenticated loopback endpoint, waits
for the port to free, and then serves in its place. When nothing is listening it is a plain
`serve`.**

- **`POST /shutdown`.** Answers `202 Accepted`, and only once that response is flushed
  (`Response.OnCompleted`) calls `StopApplication()`. Answering first is the point: the caller has
  to learn the request landed rather than infer it from a dropped connection.
- **Guarded by the token that already exists.** `/shutdown` joins `/mcp` in `McpTokenGate`'s guarded
  set — same header, same `FixedTimeEquals`, same 401 body. No new secret, no new comparison, no new
  crypto (`no-hand-rolled-crypto`).
- **Mapped only on a token-guarded host.** A direct `ai-raccoon --transport http` launch mints no
  token and is ungated by ADR-0020's non-goal; it therefore gets **no `/shutdown` at all**, rather
  than an unauthenticated one. `serve` always mints, so `--restart` always has an endpoint to call.
- **POST only.** A `GET` answers 405. A cross-origin `POST` carrying a custom header is preflighted
  and blocked, so no page in a browser on the same machine can trip it.
- **Never a PID kill.** The tool signals no process. It resolves the listener over `/observability`,
  and if the shutdown does not work it says so and stops — it does not escalate.
- **`ServerInfo` gains `Version`.** `/observability` already reported name, PID and OTLP state, and
  nothing on the wire said *which build* was answering. It does now — that is the discriminator this
  decision needs and the one an operator needs to confirm an update actually took.
- **The drain window is stated, not inherited.** The serve host sets
  `HostOptions.ShutdownTimeout = 10s` (`ShutdownEndpoint.DrainWindow`) instead of taking the 30s
  framework default, because a restart waits on that number. It is the budget for the whole host
  stop — every hosted service's `StopAsync` shares it with the in-flight requests — after which
  Kestrel aborts what is left. The port is then given `2 × DrainWindow` to free, so a full drain
  is never mistaken for a hang. See the Consequences for what the reduction costs.
- **Every failure is loud and non-zero (`ExitCode.RestartFailed = 8`), and `--restart` never
  attaches.** Falling through to "attached to the existing server" would report success for exactly
  the process the operator asked to replace.

### What each outcome does

| Outcome | Trigger | Result |
|---|---|---|
| `Nothing` | probe finds no server | plain `serve` — bind and serve |
| `Stopped` | 202, then the port freed | bind and serve |
| `Foreign` | listening, but `/observability` does not identify an ai-raccoon | `PortInUse` (3), refused before any bind is attempted and named as an unidentified listener. Nothing unidentified is ever sent a shutdown |
| `NoToken` | our data root holds no token | `RestartFailed`; **no shutdown is attempted** |
| `Refused` | `/shutdown` answered 401 | `RestartFailed` — it serves another data root |
| `Unsupported` | `/shutdown` answered 404/405 | `RestartFailed` — an ai-raccoon too old to be asked to stop |
| `TimedOut` | 202 accepted, port still held at the bound | `RestartFailed` naming the PID and the bound |

A transport failure on the `POST` counts as accepted: the server may have gone before it could
flush. The port poll, not the status code, is the verdict.

### Races, and what each one does

- **Two `serve --restart` at once.** Both may get a 202 (or one a dropped connection, which reads
  the same). Both wait for the port, both try to bind, one wins. The loser's bind fails with
  `AddressInUse`, it re-probes, finds a server, and — because `--restart` was asked for — reports
  `restart on port N did not take — another server took the port while this one was starting` and
  exits non-zero instead of attaching. One process serves; the other says plainly that it is not it.
- **The proxy re-acquiring inside the window.** A proxy whose forward fails during the drain
  re-acquires, and `BackendLauncher` will *start* a backend if nothing answers — so it can win the
  port between the old server letting go and the restart binding. The restart then loses the bind
  race and takes the branch above. Memory keeps working; the restart is honest about not being the
  one serving. Not solved here: closing it needs a lease the proxy and `serve` share, which is a
  larger change than the gap warrants.
- **The old process refuses to die.** Bounded at `2 × DrainWindow` and reported with the PID, so the
  operator has the one fact needed to deal with it. The tool does not escalate to a signal.
- **In-flight work.** A backend mid-request drains for up to 10s. What a client sees: a call that
  finishes inside the window completes normally; one that does not gets a connection-level failure,
  which the proxy answers with its documented at-least-once reconnect-and-retry
  (ADR-0020 §"The retry is at-least-once"). So a restart inherits that ADR's known residual —
  `memory_workspace_consolidate` and `memory_share` can report a false negative for work that
  happened — rather than adding a new one.

## Consequences

- **Positive.** `dotnet tool update -g ai-raccoon && ai-raccoon serve --restart` is now a complete
  recovery for ADR-0019's version lockout, and `/observability` can prove it took.
- **Positive.** `/observability` reporting a version is useful well beyond restart: "which build is
  actually serving my memory" was previously unanswerable without finding the PID and inspecting it.
- **Mixed — this is a new remote-triggerable shutdown.** Any local process that can read the 0600
  token file can now stop the server, where before it could only call tools. That is a smaller step
  than it sounds: the same file already authorises `memory_delete` and `memory_sweep` on the whole
  bank, so a holder could already do far worse than stop a daemon the proxy restarts on next use.
  The bar is unchanged — "any process that can read a 0600 file in the data root" — and on Windows
  that bar is the data-root ACL, as ADR-0020 already records.
- **Mixed — `--restart` presents the token to whatever answers the port.** `ServerRestart` decides
  the listener is ours from that listener's own unauthenticated `name: "ai-raccoon"` claim on
  `/observability` (`ServerRestart.cs:81`), then POSTs the long-lived token to it
  (`ServerRestart.cs:143-146`). Identity here is a heuristic, not a security control.
  `BackendLauncher`/`ProxyRunner.OpenBackendAsync` already do the same, so this is not a
  regression, but it does cross the boundary SECURITY.md draws: a *different* local user cannot
  read the 0600 token file, yet can bind a free loopback port, claim the name, and be handed the
  token — which never rotates. Narrowed only by not following redirects (a 3xx `/observability` is
  `Foreign`, and .NET would otherwise carry the custom header across the hop, unlike
  `Authorization`) and by treating any non-2xx as `Foreign`. Rotating the token, or binding it to
  the process that minted it, is the real fix and is not attempted here.

- **Negative — a second failure surface on the update path.** `serve --restart` can now fail in
  ways plain `serve` could not (refused, unsupported, timed out). Each has its own stderr line
  naming the port, the PID and the manual escape, and none of them leaves a half-restarted state:
  either this process serves the port or it exits non-zero having changed nothing but possibly
  having stopped the old server.
- **Negative — `--restart` against an older server cannot work at all.** A server predating this
  change has no `/shutdown`, so the first update *onto* this version still needs the old process
  stopped by hand. It is reported as exactly that, not as a mysterious failure.
- **Neutral — the 401 body wording changed.** It said "`/mcp` needs the header"; it now says "this
  endpoint needs the header", because one body serves both guarded paths. `ServerProbe`'s
  discriminator is the presence of `jsonrpc` in the body, which is untouched.
- **Mixed — the shutdown budget drops from 30s to 10s, and it is shared.** `new
  HostOptions().ShutdownTimeout` is 30 seconds (measured on .NET 10.0.302), so this is a 3x
  reduction, not a neutral restatement. It is also not a per-request drain: it is the budget for
  the whole host stop, spent by every hosted service's `StopAsync` as well as by in-flight
  requests. `BankMaintenanceHostedService.StopAsync`
  (`src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs:89-108`) runs a WAL
  checkpoint there and swallows a cut-off into a `ShutdownCheckpointFailed` log, so a slow
  checkpoint under a busy bank is now likelier to be abandoned. The bound applies to Ctrl-C and
  idle shutdown too, not just a restart. Accepted because a restart has to wait on this number and
  an unbounded wait turns one stuck call into a permanently un-restartable server.

## Non-Goals

- **A `stop` verb.** `--restart` covers the reported gap. A bare `stop` would be a second entry
  point to the same endpoint with no caller asking for it; the endpoint is there if one appears.
- **Killing anything by PID.** Not as a fallback, not on timeout. This repo has already been bitten
  by an unscoped `pkill` taking out another session's work, and a PID resolved over HTTP is still a
  PID that can be reused between the read and the signal. A shutdown that does not work is reported,
  not forced.
- **Restarting a server on another data root.** Cycling it would need its token, which we do not
  have by construction. Reported as `Refused`/`NoToken` with the token path named.
- **Making the restart atomic against a concurrent start.** See the two races above: the loser is
  loud, not correct-by-construction. A shared lease is the fix if it ever becomes worth one.
- **Draining indefinitely.** A request still running after 10s loses; unbounded draining would turn
  one stuck tool call into a permanently un-restartable server.
- **Authorising callers.** Unchanged from ADR-0020: the token proves file access, not identity, and
  every holder gets the full surface.

## Alternatives considered

### Kill the PID from `/observability`

Rejected. `/observability` already reports a PID and the E2E suite already kills spawned backends
that way, so it was the shortest path. Against it: it cannot drain — an in-flight `memory_write` is
lost mid-call rather than finished; PIDs are reusable, so the read and the signal are not the same
instant; and it needs kill rights and platform-specific escalation to be reliable. The authenticated
endpoint reuses a token, a comparison, a bind and a graceful-shutdown path that all already exist,
and costs one endpoint. The PID stays what it is today: an identifier for reporting.

### A `stop` verb plus "run `serve` again"

Rejected as the primary shape. It is two commands where the gap is one, and the window between them
is exactly when a proxy will start a fresh backend on the old binary — turning a restart into a
race the operator has to win by hand.

### Delete the token file to make the running server unusable

Rejected. It does not stop anything — the server holds the token in memory and keeps serving — and
it breaks every proxy that reads the file afterwards. It replaces a live old server with a live old
server nobody can talk to.

### A pid file written by `serve`

Rejected. ADR-0020 explicitly declines a pid file and orphan reaping ("Detaching the daemon…: no
pid file"), and it would add a second, staler source of truth beside `/observability`, which is
live by construction.

### Have the proxy restart the backend when it notices a version mismatch

Rejected. ADR-0020 is emphatic that the proxy never kills or signals anything and that daemon
lifetime belongs to `IdleWatchdog` alone. Making the proxy an executor of restarts would put a
shutdown on the path of every client's first call, where a bug takes memory down for every agent on
the machine at once. A restart is an operator action and stays one.

**Evidence:** `src/AiRaccoon/Setup/Serve/ServeRunner.cs:40-50` (the probe-attach this changes);
`src/AiRaccoon/Setup/Serve/McpTokenGate.cs` (the guarded set, `FixedTimeEquals`);
`src/AiRaccoon/Setup/Serve/McpTokenFile.cs` (mint-before-bind, 0600);
`src/AiRaccoon/Setup/Serve/IdleWatchdog.cs:47` (the graceful-shutdown path reused);
`src/AiRaccoon/Setup/Serve/BackendLauncher.cs:45-102` (the proxy's acquire, i.e. the re-acquire race);
`src/AiRaccoon/Observability/ServerInfo.cs` (the version discriminator);
`src/AiRaccoon/Setup/Cli/CliCommandTree.cs:20` (the verb families, showing no `stop`);
[ADR 0019](0019-forward-version-write-guard.md) §Consequences ("Recovery is a version update");
[ADR 0020](0020-always-on-http-stdio-proxy.md):36-37, :92, :106-141, :238-239;
[the token flow](../plans/2026-08-09-mcp-loopback-token-flow.md); `SECURITY.md:37-41`.
