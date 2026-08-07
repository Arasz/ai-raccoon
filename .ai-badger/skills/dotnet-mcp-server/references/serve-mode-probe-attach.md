# Serve-mode probe-attach + watchdog DI (verified 2026-08-06, the project serve feature)

Supersedes the earlier "GET /mcp probe" claim in SKILL.md — a Stateless MCP endpoint
leaves GET unmapped (404), so GET cannot recognize a running server.

## Deterministic attach probe (POST, not GET)

To detect "a memory server is already listening on this port" (idempotent
`serve` attach semantics), a TCP connect is not enough — any listener passes it. The
probe that works against the Streamable HTTP endpoint in Stateless mode:

- `POST http://127.0.0.1:<port>/mcp` with `Accept: application/json, text/event-stream`
  and a NON-JSON body (e.g. `x`).
- Recognized iff `status ∈ {400, 405, 406}` AND the response body contains `"jsonrpc"`.
- 2 attempts, ~1s timeout each; probe BEFORE any bank/key/embedding work.

Attached mode: print the same URL line the starter prints, exit 0, start NO host;
the attached instance must NOT own the idle watchdog (the owning process does) and
must not touch the bank. On a bind `AddressInUseException` (concurrent-start race):
re-probe once → attach, else `PortInUse` exit code. Busy-port fail-fast stays
reserved for foreign/non-HTTP listeners.

## Watchdog DI — one instance, three registrations

`AddHostedService<T>()` registers ONLY `IHostedService → T`
(decompiled Microsoft.Extensions.Hosting 10.0.10:
`TryAddEnumerable(Singleton<IHostedService, T>)`). A middleware resolving the
concrete watchdog type (`GetRequiredService<IdleWatchdog>()`) throws
`InvalidOperationException` on the first request — unit tests pass; only a live
request catches it.

```csharp
services.AddSingleton<IdleWatchdog>();
services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>());
services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
```

## Watchdog defaults and gating

- Poll tick must scale with the timeout: `min(60s, IdleTimeout/4)` — a fixed 1-min
  tick makes a 2s-timeout test host shut down up to 62s late.
- Watchdog default is serve-only BY CONSTRUCTION: `ServerConfig.IdleTimeout`
  defaults to `Zero` (bare `--transport http` launches unchanged); the serve verb
  applies the 4h default when its `--idle-timeout` option is absent (0 = disabled).
- Activity middleware must branch on `request.Path == "/mcp"` — 404s on other paths
  must not reset the idle timer. Only `/mcp` traffic counts; extraction/watch/sync
  background passes must NOT count.
- `.mcp.json` command entries are stdio-protocol by contract: a serve-form command
  breaks Claude Code even with idempotent attach (started mode never answers
  JSON-RPC; attached mode exits before initialize). Keep `.mcp.json` stdio; HTTP is
  the URL-config story (Hermes `hermes mcp add --url`, Claude Code `"type": "http"`).

## Protocol-switch verdict (no stdio→HTTP upgrade exists)

MCP spec through 2026-07-28: stdio and Streamable HTTP are separate transports with
no upgrade/handoff; no SDK implements one; prior art is reverse-direction only
(mcp-remote-style wrappers). A banner handshake (`MCP-UPGRADE: <url>` as first
stdout byte) is feasible only opt-in, and the Python SDK's `stdio_client` would need
a ~110-line fork to peek the first line (it JSON-parses every stdout line and
accepts no pre-spawned process). The one win — random-port self-discovery — is
neutralized by attach semantics on a fixed default port. Skip the handshake; ship
attach + URL config.
