# Serve mode: client-config contract + watchdog DI triple (2026-08-06)

Verified while shipping an MCP server's HTTP serve mode (design in the project's
work docs, branch `task/switch-from-stdio-to-http`). The probe-attach HTTP facts live in
`siblings/serve-probe-attach-verified.md`; this file covers the two findings that
survive OUTSIDE the probe.

## A .mcp.json COMMAND entry can never launch an HTTP serve — the stdio-protocol contract

`.mcp.json` command entries (Claude Code, VS Code, Copilot) are stdio-protocol by
definition: the client spawns the process and speaks newline-delimited JSON-RPC on
stdout. An HTTP serve verb breaks that contract in BOTH modes:

- started mode: the process serves HTTP and never answers JSON-RPC on stdout;
- attached mode (idempotent "already serving"): the process prints the URL and
  exits 0 BEFORE the client's initialize — the client sees a dead server.

So the "HTTP default" for a scaffold/catalog that emits `.mcp.json` declarations
must be expressed as guidance + metadata, NOT as a command swap: the command entry
stays stdio (zero args), and the HTTP route is a `url:`-type entry — Hermes
`config.yaml` `mcpServers` (`{"the project": {"url": "http://127.0.0.1:7721/mcp"}}`)
or Claude Code `.mcp.json` `{"type": "http", "url": ...}`. A per-client spawn on a
fixed port additionally collides; idempotent attach solves the collision, not the
protocol mismatch. (Verified 2026-08-06: ai-badger PR #316 kept the `.mcp.json`
command declaration stdio for exactly this reason.)

## AddHostedService<T> does NOT register T — the DI triple for middleware↔service sharing

`AddHostedService<T>()` registers only `IHostedService → T` (decompiled
Microsoft.Extensions.Hosting 10.0.10: `TryAddEnumerable(Singleton<IHostedService,
T>)`). A middleware that needs to signal the SAME BackgroundService instance
(e.g. an idle-watchdog activity stamp) cannot `GetRequiredService<T>()` — it
throws `InvalidOperationException` on the first request, and no test catches it
until a live boot or an E2E tool call. The design-time assumption "AddHostedService
also registers the class as a singleton" is the classic trap.

One instance, three registrations:

```csharp
builder.Services.AddSingleton<IdleWatchdog>();                                  // concrete T
builder.Services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>()); // interface
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>()); // IHostedService
```

Plus, when the service ctor takes a `TimeSpan` (the idle timeout): the generic
`AddSingleton<TService>` overloads are `where TService : class` since .NET 8, so a
struct TimeSpan only compiles via the non-generic instance form
`builder.Services.AddSingleton(typeof(TimeSpan), config.IdleTimeout)` — which is
also what lets DI resolve the TimeSpan constructor parameter at all.

Pin with a host-shape DI-smoke test (`GetServices<IHostedService>().OfType<T>()`
ShouldHaveSingleItem / ShouldNotContain) — a dropped registration silently never
runs and every other test still passes (same discipline as
dotnet-hosted-service-testing's DI-smoke rule).
