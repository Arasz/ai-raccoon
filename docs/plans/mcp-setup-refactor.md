# Plan: MCP setup refactor — separate host paths, non-5000 HTTP port

Status: proposed · Task: ai-raccoon-mcp-setup-refactor · Plan review: APPROVE-WITH-NITS (architect/opus, deleg_56247e3e) — findings F1-F9 folded in below.

> **Partially superseded by [ADR-0020](../adr/0020-always-on-http-stdio-proxy.md).** The
> "two/three host paths" shape below (stdio-only plain host, HTTP/S web host, combined
> web host) still describes `McpServerSetup.CreateServerHost` accurately for the
> `stdio`/`http`/`https` transports. ADR-0020 adds a fourth, structurally different path
> for the new `proxy` transport (now the default): `ProxyRunner` runs before any `IHost`
> is built at all — no `CreateServerHost` call, no bank, no key, no embedding model. It
> is not a fourth branch inside this plan's host factory; it is a pre-branch in
> `Program.cs` that bypasses the factory entirely. Kept as-is below as the design record
> for the three paths that do still go through `CreateServerHost`.

## Scope (user spec + e: extension)

1. **Two host paths** in `McpServerSetup`:
   - **stdio-only** → a plain app host (`Host.CreateApplicationBuilder`) — no web server, no HTTP bind at all. (Spike proven: `AddMcpServer().WithStdioServerTransport()` starts on a plain host; `IServer` is null.)
   - **HTTP/S** → a web application host (as today).
   - **both (stdio + http)** → web app with stdio, as today.
2. **HTTP port**: the web host must not bind the ASP.NET default 5000 (collides with macOS Control Center and other ai-raccoon instances). New `--port` CLI flag: default 7721, `0` = random/ephemeral. Bound URL printed at startup (discoverability for random).

## Design

- `ServerConfig` gains `Port` (int, default 7721). `CliOptions` record gains a defaulted 4th field (existing 3-arg constructions keep compiling). `CliArgs` parses `--port`.
- `McpServerSetup`:
  - New `CreateServerHost(ServerConfig)` → `IHost` factory: transport set `{Stdio}` only → plain host; otherwise → web host.
  - Plain-host path: `Host.CreateApplicationBuilder` + `AddMcpServer().WithStdioServerTransport()` + tools/prompts + stderr console logging.
  - Web-host path: existing `ConfigureMcpServer(transport)` + `ConfigureMcpEndpoints(transport)` + `ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port))`. Explicit endpoints override the default address (proven in the stdio-bind spike).
  - **Remove** the ephemeral-bind hack (`Listen(IPAddress.Loopback, 0)`) from `ConfigureMcpServer` — superseded.
  - Print the bound URL (`app.Urls` + `/mcp`) to stderr for the web path.
- `Program.cs`: `var app = McpServerSetup.CreateServerHost(config);` — the shared startup (bank probe, encryption mismatch, embedding bootstrap) and `app.RunAsync()` are host-agnostic (`IHost`).
- `McpServerFactory` (E2E): pass `--port 0` so each WebApplicationFactory instance binds its own random port — keeps E2E parallel-safe and WAF-compatible with explicit endpoints. Verify WAF's client address resolution against explicit endpoints empirically (run the E2E subset).
- The `{Stdio, Http}` "both" set is structurally handled by the factory (web host + stdio) but not CLI-selectable — same as today (`--transport` is single-valued).

## TDD (failing tests first, per work package)

- WP1 — host factory + plain host: tests call `McpServerSetup.CreateServerHost(...)` (new API — RED by absence).
  - stdio-only: `IServer` null; starts with 127.0.0.1:5000 held by another listener.
  - stdio-only: full host lifecycle (start/stop) with tools+prompts registered.
- WP2 — web path port: `--port 7721` → `app.Urls` contains 7721, not 5000; `--port 0` → ephemeral (not 5000/7721); https → web host + warning unchanged.
- WP3 — both: `{Stdio, Http}` → web host (IServer present) + stdio transport starts.
- WP4 — config: `ServerConfig` port default 7721; `CliArgs` parses `--port` (incl. 0 and garbage).
- Adapt existing `McpServerSetupHostTests` (the two stdio tests) to the new factory.

## Gates

- `dotnet build` clean; targeted tests green; full `dotnet test` — failures classified against the known pre-existing flaky set (observed 43/1/0 across three runs on unmodified main; includes a watch rename test).
- Live binary probes: `--transport http --port 7721` serves while another instance holds 5000; stdio binary completes initialize + tools/list while 5000 held.

## Out of scope

- Version bump (repo uses separate `release(version)` commits — precedent 1ad5625 → ec3d15d).
- `--transport both` CLI support.

## PR

- Supersedes #28 (ephemeral-bind fix): close #28 when this lands.
