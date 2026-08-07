# Stdio MCP servers must not bind the default HTTP port

Verified 2026-08-05 against the project 1.0.5 (ModelContextProtocol SDK 2.0, `WebApplication`
host, dual transport). First fixed in `fix/stdio-port-bind` (the project PR #28, ephemeral
bind); superseded by the plain-host refactor below (task the project-mcp-setup-refactor).

## Symptom

A stdio MCP server built on `WebApplication` binds `http://localhost:5000` (the ASP.NET
default) on EVERY launch, stdio included. A second instance — another client's launch, or a
host's MCP watchdog (Hermes `mcp_stdio_watchdog.py` keeps one server alive per configured
entry) — aborts right after `initialize`:

```
System.IO.IOException: Failed to bind to address http://127.0.0.1:5000: address already in use.
```

The client sees a server that connects, answers `initialize`, then dies before `tools/list` —
the classic "server shows up then disappears / still missing" symptom across every client
(`.mcp.json`, `~/.hermes/config.yaml`, Rider, VS Code). On macOS, `ControlCenter` holds
`*:5000` but coexists (SO_REUSEPORT); two the project instances do NOT coexist.

## Fix

In the stdio branch, on the builder BEFORE `Build()`:

```csharp
webApplicationBuilder.WebHost.ConfigureKestrel(static options =>
    options.Listen(IPAddress.Loopback, 0));   // ephemeral port; stdio never uses it
```

Explicit `Listen` endpoints replace the default-address fallback, so the HTTP transport keeps
its fixed-port behavior.

## Better fix: no web host at all for stdio-only

The ephemeral bind makes the stdio branch start cleanly, but the cleaner shape (adopted by
the project setup refactor) is: **stdio-only servers don't need a `WebApplication` at
all.** `Host.CreateApplicationBuilder` + `AddMcpServer().WithStdioServerTransport()` runs the
identical stdio transport as a hosted service with NO web server: `IServer` is null and no
port is bound (verified by spike — host starts/stops with no listener while another instance
holds 5000). The web host is reserved for HTTP/S and the both-transports case.

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<MyTools>();
using var host = builder.Build();
await host.RunAsync();
```

- The MCP SDK's DI extensions (`AddMcpServer`, `WithStdioServerTransport`, `WithTools`,
  `WithPrompts`) live in namespace `Microsoft.Extensions.DependencyInjection` — visible
  implicitly under the Web SDK, but a plain-SDK **test** project needs the explicit
  `using Microsoft.Extensions.DependencyInjection;` (plus `FrameworkReference
  Microsoft.AspNetCore.App` for `IServer`).
- Split the host choice behind one testable factory: `CreateServerHost(transports)` —
  `{Stdio}` only → plain host; contains Http/Https → web host (which may still include the
  stdio transport, the "both" case).
- HTTP-port policy for the web host: pin a NON-5000 default or `--port 0` for random,
  applied via `ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port))` — the same
  mechanism as the ephemeral fix, because `UseUrls` is unusable after
  `Configuration.Sources.Clear()` (trap 2). **Default-port choice is a user preference: the
  owner rejected 5001 ("port that is not used often") and picked 7721.** 5000 collides with
  macOS ControlCenter; pick an uncommon port and make it a `--port` CLI flag (default 7721,
  `0` = random) so it is configurable.
- Post-start URL reporting for the web host: `app.Urls` is only knowable AFTER
  `StartAsync` (port 0 = random), so `RunAsync()` cannot print it. Move the lifecycle into an
  extension: `await host.StartAsync()` → report the bound URL(s) via a source-generated
  `LoggerMessage` (NOT `Console.Error` — the owner's rule) → `await host.WaitForShutdownAsync()`.
  The web host needs an explicit console-logging provider (stderr) added on the builder for
  the logger to be live — the stdio branch adds it inside the transport wiring, the http-only
  branch does not get one by default.
- **WAF/TestServer resolution (verified): `WebApplicationFactory` hosts the app on
  in-process TestServer — no real Kestrel socket is EVER bound in E2E.** Consequences:
  (a) parallel E2E safety comes from TestServer, NOT from the port — the earlier "WAF
  factory must pass --port 0" caution is WRONG and unnecessary; (b) `app.Urls` under
  TestServer does NOT reflect `ConfigureKestrel` Listen endpoints, so any port assertion
  must use a REAL host (`CreateServerHost` + `StartAsync` + read `app.Urls` + `StopAsync`),
  never the factory.

## Two traps (both verified)

1. `options.ListenLocalhost(0)` THROWS:
   `InvalidOperationException: Dynamic port binding is not supported when binding to
   localhost. You must either bind to 127.0.0.1:0 or [::1]:0, or both.`
   Use `Listen(IPAddress.Loopback, 0)` (or `[::1]:0`).
2. `builder.WebHost.UseUrls()` (or any `UseSetting`) THROWS
   `InvalidOperationException: A configuration source is not registered. Please register one
   before setting a value.` when the builder's config sources were cleared first
   (`builder.Configuration.Sources.Clear()` — a common single-channel-config ruling, e.g.
   "settings table is the single runtime channel"). `ConfigureKestrel` defers via services and
   works after the clear; `UseUrls` writes the `urls` config key and does not.

## TDD shape (machine-state-independent)

The test must stay RED on any machine, whether or not the environment already holds port 5000:

- `StdioHost_DoesNotBindTheFixedPort`: build the stdio host (same setup extensions as
  `Program.cs`), `StartAsync`, assert `app.Urls` contains no `:5000`.
- `StdioHost_StartsEvenWhenAnotherInstanceHoldsTheDefaultPort`: pre-hold 127.0.0.1:5000 with a
  `TcpListener` wrapped in try/catch — when the environment already holds it, skip the
  pre-hold; either way the pre-fix host fails on its own bind, so the test fails before the
  fix and passes after.
- xunit.v3: pass `TestContext.Current.CancellationToken` to `StartAsync`/`StopAsync`
  (xUnit1051 is a build error under TreatWarningsAsErrors).
- The test project needs `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`.

## Live verification

Build the binary, hold 5000 with a running instance (or the host's own watchdog), then drive
the real stdio handshake: `initialize` + `tools/list` must both respond (19 tools incl.
`memory_search`). Pre-fix the same probe dies after `initialize`. Probe shape: write both
frames to stdin with ~2-3 s sleeps between them; keep stdin open until the response flushes
(EOF closes the transport before the frame is written).
