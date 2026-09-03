## 4. HTTP (Streamable) transport and dual-mode servers

The base `ModelContextProtocol` package only ships stdio. Streamable HTTP needs
`ModelContextProtocol.AspNetCore` plus the **Web SDK** (`Microsoft.NET.Sdk.Web`):

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>                     <!-- Web SDK defaults this to FALSE -->
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>  <!-- no static assets; silences pack-time manifest errors -->
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <PackageType>McpServer</PackageType>
  </PropertyGroup>
</Project>
```

HTTP host (from the official template's `remote/Program.cs`):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)  // Stateless: no server→client sampling/elicitation
    .WithTools<RandomNumberTools>();
var app = builder.Build();
app.MapMcp("/mcp");   // default pattern is "" (root) — pass "/mcp" explicitly
app.Run();
```

**Dual-mode via env var** — one project serves both transports; the launch profile picks:

```csharp
if (Environment.GetEnvironmentVariable("MCP_TRANSPORT") == "http")
{ /* WebApplication + WithHttpTransport + MapMcp("/mcp") */ }
else
{ /* Host.CreateApplicationBuilder + WithStdioServerTransport() */ }
```

Don't leave the comparison inline — extract it into a small internal seam so the
transport selection is unit-testable (a review will flag an untested transport
branch as a TDD violation) AND case-insensitive. The bare `== "http"` silently
falls back to stdio for `HTTP`/`Http`:

```csharp
internal static class McpTransportSelector
{
    public static bool UseHttp(string? transport) =>
        string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
}
```

Cover it with a `[Theory]` — `"http"`, `"HTTP"`, `"Http"` → true; `"stdio"`, `""`,
`null` → false.

**Stdio-only servers can skip the web host entirely.** `Host.CreateApplicationBuilder` +
`WithStdioServerTransport()` serves stdio with no Kestrel and no port — a plain
`WebApplication` binds the default `http://localhost:5000` on every launch, so a second
instance (another client, a host's MCP watchdog) aborts after `initialize` with
"address already in use" and the client sees a server that connects then disappears. Full
traps (`ListenLocalhost(0)` throws on port 0, `UseUrls()` throws after
`Configuration.Sources.Clear()`), the host-factory split, the WAF/TestServer resolution
(no real socket in E2E — port assertions need a real host), and the machine-independent TDD
shape: `references/stdio-host-port-bind.md`. HTTP-port default is a user preference — the
owner rejected 5001 ("not used often") and picked 7721; make it a `--port` flag (0 = random)
and report the bound URL through a LoggerMessage after `StartAsync`, not `Console.Error`.

`Properties/launchSettings.json`:

```json
{
  "profiles": {
    "stdio": { "commandName": "Project", "environmentVariables": { "MCP_TRANSPORT": "stdio" } },
    "http":  { "commandName": "Project", "applicationUrl": "http://localhost:8080",
               "environmentVariables": { "MCP_TRANSPORT": "http" } }
  }
}
```

**Long-interval BackgroundServices never fire in stdio-only hosts — gate them to
HTTP transports.** MCP clients recycle stdio subprocesses aggressively (re-spawned
per connection on a ~5-min cadence), so a `BackgroundService` whose first
`PeriodicTimer` tick is 30-60 min out dies before it fires — "configured but not
executing". Ship the gate as a registration flag
(`RegisterMemoryServices(options, registerExtractionHostedService: false)` in the
stdio-only path, default on in HTTP/S hosts), pinned by host-shape tests
(`host.Services.GetServices<IHostedService>()` ShouldNotContain/ShouldContain).
An idle-watchdog / serve-mode service is the same class: HTTP-gated, and its
"activity" signal must count MCP requests only — its own background passes must
NOT reset the idle timer.

**Before building a "switch to HTTP" CLI feature, check the client side first.**
MCP clients often accept URL servers directly — Hermes: `hermes mcp add <name> --url http://127.0.0.1:<port>/mcp`;
Claude Code: `.mcp.json` entry with `"type": "http"`. When both hold, the feature
collapses to: a launch verb that prints the bound URL, an `--mcp-entry` renderer,
and (optionally) an idle watchdog. Do not design a transport-switch protocol
nobody's client needs (verified: a "serve" feature shrank from a transport
overhaul to three small pieces).

The canonical artifact for that feature — `serve` verb shape, foreground semantics
(URI to stdout after bind, busy-port fail-fast, shell backgrounding recipe), the
idle watchdog (one BackgroundService + Interlocked timestamp + middleware on `/mcp`,
HTTP-gated, `--idle-timeout 0` disables), exact `--mcp-entry` JSON for Hermes
(`{"<server>":{"url":…}}`) and Claude Code
(`{"mcpServers":{"<server>":{"type":"http","url":…}}}`), plus an acceptance-criteria
table with named gates — is the shape to follow when a server gains a serve mode.
See `references/serve-mode-client-contract-and-di.md` and
`references/serve-probe-attach-verified.md` for the verified deep dives.

**Transport-switch research (verified 2026-08-06): no stdio→HTTP upgrade exists in
the MCP spec.** Through the 2026-07-28 revision, stdio and Streamable HTTP are
SEPARATE transports with no upgrade/handoff mechanism; no SDK (Python/TS/.NET)
implements one; prior art is reverse-direction only (mcp-remote-style wrappers).
A WebSocket-style upgrade would need a bespoke OPT-IN banner (`MCP-UPGRADE: <url>`
as the first stdout byte, peeked by a capable client before pipe handover) — and its
only real win is RANDOM-PORT self-discovery (`--port 0`); a fixed-port `url:` entry
achieves the same end state with zero protocol invention.

**Idempotent "already serving → attach" semantics** are the answer to per-client
spawn collisions when a client config (e.g. Claude Code `.mcp.json`, spawned per
client) launches a fixed-port HTTP serve: probe TCP + `GET /mcp` before binding →
recognized server on the port ⇒ print the URL, exit 0, start NO host (the attached
instance must NOT own the watchdog — the owning process does); on bind failure
(concurrent-start race) re-probe once and attach instead of erroring. Busy-port
fail-fast stays reserved for non-project listeners. A TCP connect alone cannot
distinguish a memory server from any other listener — the `GET /mcp` probe is
what makes "recognized" meaningful.

**Idle-watchdog E2E timing trap:** the watchdog's poll interval must be ≤ the test
timeout, or shutdown fires up to one poll late (a 1-min poll vs a 2s test timeout
never fires on schedule — real-time E2E needs margin ≥ poll + timeout, or an injected
shorter poll). Background passes (extraction/sync/watch) must NOT count as activity —
only `/mcp` traffic resets the idle timer.

**Smoke-test the HTTP endpoint** (MCP is JSON-RPC over POST; responses are SSE):

```bash
curl -s -X POST http://localhost:8080/mcp -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
# then tools/list and tools/call, adding header: MCP-Protocol-Version: 2025-11-25
```

**Smoke-test the stdio endpoint end-to-end** — `scripts/mcp-stdio-probe.py` spawns the
server (default `dotnet run --project src/<Server>/<Server>.csproj --no-launch-profile --no-build`),
writes a real `initialize` handshake, and reports whether the JSON-RPC result came back
on a clean stdout (no launch-settings notice) — the exact check a strict MCP client runs.
Pure Python (macOS has no `timeout(1)`), kills the server's process group itself. Pitfalls
it encodes: never `communicate(input=…)` — closing stdin makes a stdio server exit on EOF
before it replies; write the request, keep stdin open, read with `select`.

Shell one-liner variant (verified empirically against an installed tool): 

```bash
{ printf '%s\n' '<initialize frame>'; sleep 3; } | <tool> 2>/tmp/err | head -c 400
```

The `sleep` matters: `printf '<frame>' | tool | head -c 400` can return ZERO bytes — the
server reads the message, sees stdin-EOF, and shuts the transport down before the response
frame flushes (stderr shows "transport completed reading messages"; exit 0). Holding stdin
open ~3 s lets the frame through; the process then exits 0 on EOF, so no `timeout` is needed.

**Verifying a PUBLISHED tool install over stdio (fresh-install gate):** when the target is
the shipped nupkg rather than the dev build, extend the smoke to a full round trip —
install into a temp `--tool-path` with isolated `NUGET_PACKAGES`, `--data-root` a fresh
dir, run the documented setup verb, then initialize/write/search/stats over stdio and
assert `pending == 0` (proves the bundled engine actually embedded). Two SDK facts
invalidate naive assertions: (1) **tool results arrive WRAPPED** —
`result.content[0].text` holds a JSON STRING of the tool's record; unwrap before asserting
fields; (2) **`serverInfo` reports the ASSEMBLY identity** — `name` = assembly name,
`version` = AssemblyVersion numeric-only (`1.0.6.0`), NOT the `.mcp/server.json` marketing
name/version; assert the version as a prefix. And the trap that voids the whole check: a
config-gated engine (embedding provider) silently degrades instead of failing — writes
skip embedding, search falls back to FTS5 — so "search returns the entry" does NOT prove
the model loaded; assert the engine side-effect (`pending == 0`) and no download lines in
stderr. Full protocol, isolation recipe, and result-shape table:
see the `dotnet-tool-publishing` skill's `references/fresh-install-verification.md`.
