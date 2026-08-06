---
name: dotnet-mcp-server
description: Implement MCP (Model Context Protocol) servers in .NET using the ModelContextProtocol NuGet package — tool registration, DI wiring, HttpClient for REST-based tools, and unit testing with mock HTTP handlers. Use when adding MCP tools to a .NET project, wiring up a stdio MCP server host, or testing MCP server tool classes.
tags: [dotnet, mcp, csharp, server, tools, model-context-protocol]
related_skills: [dotnet-domain-modeling]
---

# dotnet-mcp-server

Implement MCP servers in .NET using the `ModelContextProtocol` NuGet package (SDK v1.4+).

## Prerequisites

- .NET 10+ (the SDK targets `net10.0` but works on `net8.0`+)
- NuGet packages: `ModelContextProtocol`, `Microsoft.Extensions.Hosting`
- For HTTP-calling tools: `Microsoft.Extensions.Http`

## 1. Project setup

```xml
<!-- .csproj -->
<PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <PackageType>McpServer</PackageType>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Http" />   <!-- for REST-calling tools -->
    <PackageReference Include="ModelContextProtocol" />
</ItemGroup>

<!-- Let tests access internal tool classes -->
<ItemGroup>
    <InternalsVisibleTo Include="YourProject.Mcp.Tests" />
</ItemGroup>
```

## 2. Tool class pattern

Tools are **plain C# classes** annotated with `[McpServerTool]` and `[Description]`. The MCP SDK discovers them via DI.

```csharp
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace YourProject.Mcp.Tools;

internal sealed class MyTools(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [McpServerTool]
    [Description("Does something useful with the given input.")]
    public async Task<string> DoSomething(
        [Description("The resource ID.")] string resourceId,
        [Description("Amount value.")] decimal amount,
        [Description("Optional period.")] string period = "Monthly")
    {
        var request = new { Amount = amount, Period = period };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/resources/{resourceId}/action", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
```

### Key design choices

- **Return `Task<string>`** (JSON-serialized response body), not `Task<object>`. Returning `object` causes boxing issues — `JsonElement` loses `ValueKind` when boxed, making tests impossible to write without unsafe casts.
- **Keep tool classes `internal sealed`** and use `InternalsVisibleTo` for test access. This prevents external consumers from depending on tool implementation details.
- **Tools are thin clients** — no business logic. The tool makes an HTTP call, validates the response, and returns the JSON. Domain logic lives in the API/domain layer.
- **Use private DTO records** inside the tool class for request serialization. Don't share them with the API — the MCP tool's serialization contract is independent.

## 3. Program.cs (host wiring)

```csharp
using YourProject.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Configure API base URL (defaults to local func host)
var apiBaseUrl = builder.Configuration["JSAA_API_BASE_URL"] ?? "http://localhost:7071";

// Named HttpClient per tool class
builder.Services.AddHttpClient<MyTools>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RandomNumberTools>()  // existing sample
    .WithTools<MyTools>();           // your tools

await builder.Build().RunAsync();
```

- Use **`AddHttpClient<T>()`** (named typed clients) — one per tool class. This gives you typed DI, testability, and Polly integration out of the box.
- The `WithStdioServerTransport()` call makes it a stdio MCP server. Other transports (SSE, HTTP) are available in the SDK.

## 3b. HTTP (Streamable) transport and dual-mode servers

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
HTTP transports.** MCP clients recycle stdio subprocesses aggressively (the hermes
gateway re-spawns per connection on a ~5-min cadence; verified live: 1048
ai-raccoon registrations in one day, each instance cleanly stdin-EOF'd), so a
`BackgroundService` whose first `PeriodicTimer` tick is 30-60 min out dies before
it fires in a stdio process — "configured but not executing". Ship the gate as a
registration flag: `RegisterMemoryServices(options, registerExtractionHostedService: false)`
in the stdio-only host path, default on in any HTTP/S host, pinned by host-shape
tests (`host.Services.GetServices<IHostedService>()` ShouldNotContain/ShouldContain).
An idle-watchdog / serve-mode service is the same class of component: HTTP-gated,
and its "activity" signal must count MCP requests only — the watchdog's own
background passes must NOT reset the idle timer.

**Before building a "switch to HTTP" CLI feature, check the client side first.**
The tool may already serve HTTP (`--transport http --port N` → `MapMcp("/mcp")`,
bound URL reported via a LoggerMessage after `StartAsync`), and MCP clients often
accept URL servers directly — Hermes: `hermes mcp add <name> --url http://127.0.0.1:<port>/mcp`;
Claude Code: `.mcp.json` entry with `"type": "http"`. When both hold, the feature
collapses to: a launch verb that prints the bound URL, an `--mcp-entry` renderer,
and (optionally) an idle watchdog. The client-config change is a one-liner; do not
design a transport-switch protocol nobody's client needs. (Verified 2026-08-06 on
ai-raccoon: the whole "serve" feature shrank from a transport overhaul to three
small pieces.)

The canonical artifact for that feature — `serve` verb shape, foreground semantics
(URI to stdout after bind, busy-port fail-fast, shell backgrounding recipe), the
idle watchdog (one BackgroundService + Interlocked timestamp + middleware on `/mcp`,
HTTP-gated, `--idle-timeout 0` disables), exact `--mcp-entry` JSON for Hermes
(`{"ai-raccoon":{"url":…}}`) and Claude Code
(`{"mcpServers":{"ai-raccoon":{"type":"http","url":…}}}`), plus an acceptance-criteria
table with named gates and a parallel WP split — is `docs/work/2026-08-06-http-serve-design.md`
in the ai-raccoon repo (branch `task/switch-from-stdio-to-http`). Read it before
implementing any serve-mode work package.

**Transport-switch research (verified 2026-08-06): no stdio→HTTP upgrade exists in
the MCP spec.** Through the 2026-07-28 revision, stdio and Streamable HTTP are
SEPARATE transports with no upgrade/handoff mechanism; no SDK (Python/TS/.NET)
implements one; prior art is reverse-direction only (mcp-remote-style wrappers
exposing a remote HTTP server as local stdio). If a WebSocket-style upgrade is
requested, the honest shape is a bespoke, OPT-IN banner: server prints one magic line
(`MCP-UPGRADE: http://127.0.0.1:<port>/mcp`) as its FIRST stdout byte then serves
HTTP; a capable client peeks the first line before handing the pipe to its stdio
transport, falling back to stdio on `{` or timeout (bare launch must stay pure
JSON-RPC — the banner can never replace it by default). The one thing the handshake
buys that a `url:` client config cannot is RANDOM-PORT self-discovery (`--port 0` +
banner tells the client the ephemeral URL); a fixed-port url entry achieves the same
end state with zero protocol invention. Client integration point for any such
handshake: Hermes runs from `~/.hermes/hermes-agent/` (venv, official Python `mcp`
SDK's `stdio_client`; the peek must precede pipe handover in `tools/mcp_tool.py`).

**Idempotent "already serving → attach" semantics** are the answer to per-client
spawn collisions when a client config (e.g. Claude Code `.mcp.json`, spawned per
client) launches a fixed-port HTTP serve: probe TCP + `GET /mcp` before binding →
recognized server on the port ⇒ print the URL, exit 0, start NO host (the attached
instance must NOT own the watchdog — the owning process does); on bind failure
(concurrent-start race) re-probe once and attach instead of erroring. Busy-port
fail-fast stays reserved for non-ai-raccoon listeners. A TCP connect alone cannot
distinguish an ai-raccoon server from any other listener — the `GET /mcp` probe is
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
server (default `dotnet run --project src/AiRaccon --no-launch-profile --no-build`),
writes a real `initialize` handshake, and reports whether the JSON-RPC result came back
on a clean stdout (no launch-settings notice) — the exact check a strict MCP client runs.
Pure Python (macOS has no `timeout(1)`), kills the server's process group itself. Pitfalls
it encodes: never `communicate(input=…)` — closing stdin makes a stdio server exit on EOF
before it replies; write the request, keep stdin open, read with `select`.

Shell one-liner variant (verified empirically against an installed tool): 

```bash
{ printf '%s\n' '<initialize frame>'; sleep 3; } | ai-raccoon 2>/tmp/err | head -c 400
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
`dotnet-tool-publishing` → `references/fresh-install-verification.md`.

## 3f. Giving a stdio MCP server a CLI (System.CommandLine, parse-first)

For a stdio server, every byte of CLI output must go to stderr: System.CommandLine's default
help renders to stdout (`InvocationConfiguration.Output`) and parse errors print to stderr
THEN render help to stdout — either would corrupt the protocol stream. Parse first, never
invoke actions:

```csharp
var result = new CommandLineBuilder(BuildRootCommand()).UseDefaults().Build().Parse(args);
if (result.Errors.Count > 0 || result.Action is HelpAction || result.Action is VersionOptionAction)
    return Render(result, Console.Error);   // own renderer; stdout untouched; 0 help/version, 1 errors
```

Key facts (full detail + evidence: `references/cli-args-for-stdio-mcp.md`):
- Help detection idiom: `parseResult.Action is HelpAction` / `VersionOptionAction` (2.0.x GA).
- **Enum options accept EVERY enum member** — `Option<McpTransport>` parses `--transport https`
  even if your spec says `stdio|http`; restrict (string option + `FromAmong`) or document the member.
- Merging CLI > env > default must preserve the original env reads' `IsNullOrWhiteSpace` gating —
  `cli.X ?? env ?? default` regresses `X=""` (`--data-root ""` → `Directory.CreateDirectory("")` throws).
- **Default-valued options materialize in the parse result even when absent** — with
  `new Option<int>("--port") { DefaultValueFactory = _ => 7721 }`, `parseResult.GetResult("--port")`
  returns a NON-null `OptionResult` for an invocation that never passed `--port`. A
  "return null options when nothing was given" shortcut keyed on `GetResult(...) is null`
  silently breaks (no-args invocations stop shortcutting). Detect explicit presence by the
  result's token count: `portResult is OptionResult { Tokens.Count: > 0 }` (verified against
  System.CommandLine 2.0.10).
- **System.CommandLine 2.0.10 has NO `TimeSpan` option type** (verified: no TimeSpan
  members in the package's API surface) — a duration flag like `--idle-timeout 4h`
  must be an `Option<string>` plus a small pure parser accepting suffixes
  (`90s`/`30m`/`4h`/`1d`; `0` = disabled). Pin the parse matrix with a unit test; the
  parser is a pure function so a static class is sanctioned.
- **Tool command name defaults to the ASSEMBLY name** (`AiRaccoon`), not PackageId — set
  `<ToolCommandName>ai-raccoon</ToolCommandName>` for a kebab-case command that works on
  case-sensitive filesystems (`.mcp.json` `"command"`).
- `--version` prints `AssemblyInformationalVersion` (defaults to `1.0.0`), not `PackageVersion`.
- `WebApplicationFactory<T>` passes EMPTY args to the entry point — CLI behavior is
  unit-test + manual-smoke territory; env behavior stays E2E-able (factory env mutation).

## 3g. Tool inventory tests — assert the REGISTERED surface, not the class

A tool-inventory test that reflects over the tool CLASS (`typeof(Tools).GetMethods()`
filtered by `[McpServerTool]`) proves the class carries tools — it does NOT prove the
server registers them. A dropped `.WithTools<T>()` passes every class-level test and ships.

**Real incident (ai-raccoon 1.0.6, 2026-08-06):** a host-refactor PR (01f0f63, "separate
host paths") removed `.WithTools<WatchTools>()` from BOTH `McpServerSetup` host paths
(stdio app host + web host). The class-level `WatchToolsInventoryTests` stayed green; the
shipped binary exposed **16 tools instead of 19** — MCP clients lost
`memory_watch_add/status/remove` while docs and prompts still advertised them. Only a live
`tools/list` probe caught it. `McpServerSetupHostTests` pinned transport shape only, never
the registered tool set — that was the gap.

Two guards against this class of regression:
1. **Host-level test**: boot the host (or resolve the built MCP server services) and
   enumerate the ACTUAL registered tool names; assert the full expected set. Registration
   drops then fail the suite at review time, not at release time.
2. **Live binary probe** (release gate): run `tools/list` against the published binary and
   diff the tool set. **Bare `tools/list` over stdio returns NOTHING without a prior
   `initialize` handshake** — the probe must send `initialize` → `notifications/initialized`
   → `tools/list` and parse newline-delimited JSON. Full recipe + probe script:
   `references/tool-registration-surface-test.md`.

Fast negative filter before either guard: `strings <server.dll> | grep -o "memory_watch_[a-z]*"`
— tool-name strings missing from the binary means registration is moot (but strings
presence does NOT prove registration; only the host test / live probe does).

## 3c. MCP SDK 2.0 API specifics (verified against 2.0.0)

Facts below were verified by reflection on the installed `ModelContextProtocol.Core` 2.0.0
assembly and by reading the v2.0.0 sources (`AIFunctionMcpServerTool.cs`, `McpServerImpl.cs`,
`McpServerBuilderExtensions.cs`). Full notes + a reusable probe recipe:
`references/mcp-csharp-sdk-2.0-apis.md`.

**Backing the server with the sqliteai native extensions (sqlite-memory/vector/sync)?**
Store-layer traps — module basename ↔ `sqlite3_<name>_init` entry point, `memory_add_text`
returning 1 not the hash, deferred-embeddings requirement, real-extension integration-test
pattern — are in `references/sqlite-ai-store-integration.md` (verified against the real
binaries). NOTE: that file documents the SUPERSEDED extension-backed store; the current
managed `memory.db` design (idempotent schema init, FTS5 external-content search, sqlite-vec
from NuGet, Dapper record-vs-class DTO traps, `...`-spread CS8635) is
`references/managed-sqlite-store-patterns.md`. Only the cloudsync extension still loads, until
the own-sync wave removes it.

**Local GGUF embedding model for the llama.cpp engine?** Model pick (all-MiniLM-L6-v2
Q5_K_M ~21 MB Apache-2.0 verified; nomic-embed-text-v1.5 Q8_0 as the documented reference),
pinned download recipe + SHA-256, `AIRACCOON_TEST_GGUF` test-gating, the verified engine
matrix (local vs vectors.space ONLY — LM Studio/Ollama are not configurable, the remote URL
is hardcoded), and the `memory_search` Dapper blob-affinity/record-ctor trap:
`references/local-gguf-embedding-model.md`.

### Prompts: `[McpServerPrompt]` + `WithPrompts<T>()`

2.0.0 adds attribute-based prompts, same shape as tools (class with methods, ctor injection works):

```csharp
internal sealed class MemoryPrompts
{
    [McpServerPrompt(Name = "memory-usage-guide")]   // else the name is the snake_cased method name
    [Description("Protocol for the calling agent: always pass project_id ...")]
    public string MemoryUsageGuide(
        [Description("The project id to scope memory operations to.")] string? projectId = null)
        => """...guide text...""";
}
```

- Register `.WithPrompts<MemoryPrompts>()` chained onto `AddMcpServer()` — works on both transports.
- **Return type is constrained**: `string` | `PromptMessage` | `IEnumerable<PromptMessage>` |
  `ChatMessage` | `IEnumerable<ChatMessage>`. ANY other return type throws
  `InvalidOperationException` at invoke time — returning `string` is the simple choice.
- `[McpServerPrompt(Name=…)]` overrides the derived name; `[Description]` on the method becomes
  the prompt description; `[Description]` on parameters becomes the agent-facing arg description.
- **Prompt copy is C# string content — watch the placeholders.** Inside an interpolated raw
  string (`$"""…"""`), `{project-id|*}` is parsed as an interpolation hole and fails to
  compile (CS1733 "Expected expression", CS9006 "does not start with enough '$' characters").
  Use `<project-id|*>` angle-bracket placeholders in prompt text (hit when adding watch-CLI
  examples to a guide).
- **Agent-facing prompts are the product, not boilerplate — design and pin them.** Review
  the distributed prompts for what a fresh agent actually needs: (1) a SEARCH-FIRST retrieval
  ladder — search the memory store first with 2-3 query formulations (exact phrase → keywords
  → plain-English restatement), escalate to the host's web/code search only by result
  (decisive hit → use and cite its source; partial → combine with one targeted external
  search; none → search externally, then write the finding back so the next lookup is
  answered from memory); (2) the full tool map with WHEN to use each (ingest/stats/sync
  included, not just search/write); (3) setup PREREQUISITES for CLI-configured features —
  e.g. the watch scope allowlist + enablement are CLI-only (`watch scope add` + `watch
  enable`), and `memory_watch_add` fails with `watching-disabled`/`path-outside-scope` until
  they exist, so the guide must say so. Pin the guide's load-bearing sentences with
  content-assertion tests (`guide.ShouldContain("memory_watch_add")`) — prompt copy is the
  agent contract and TDD applies to it like any behavior (2 new facts pinned a memory-first
  ladder + watch usage; the old guide said only "search before asking").

### Tool error signaling — the message-drop trap

`McpServerImpl` catches exceptions from tool invocation and returns `CallToolResult { IsError = true }` —
but `CreateToolCallErrorResult` includes the exception message in the error text **only when the
exception is `McpException`** (namespace `ModelContextProtocol`, ctor `(string message)`). A plain
`ArgumentException`/`InvalidOperationException` surfaces as the generic
`"An error occurred invoking '<tool>'."` with NO reason — useless for agent-facing coded errors
(spec-style `invalid-params: …`, `sync-not-configured`, …).

Two reliable options:
1. `throw new McpException("invalid-params: project_id is required");` — message preserved.
2. Return a structured error directly (the SDK has an explicit pass-through branch for it):

```csharp
private static CallToolResult Error(string message) => new()
{
    IsError = true,
    Content = [new TextContentBlock { Text = message }],
};
```

Option 2 literally "returns a structured tool error" and gives exact control of the text. Combined
with record success returns, the method returns `Task<object>` and the SDK's result switch dispatches
on the RUNTIME type (`CallToolResult` → pass-through honoring `IsError`; record → JSON text content +
structured content; `string` → `TextContentBlock`; `null` → empty content). The union pattern is
verified working — but keep the §6 `Task<object>`/`JsonElement` boxing caveat for tools that return
`JsonElement` directly.

### Request filters — per-tool-call interception (SDK 2.1.0)

`ModelContextProtocol.Server.McpRequestFilters` (verified by reflection probe on
the installed 2.1.0 assembly; the §3c 2.0.0 notes predate it) exposes per-method
filter lists — the hook for activity signals (idle watchdog), auth, or per-call
logging without touching tool bodies or ASP.NET middleware: `ListToolsFilters`,
`CallToolFilters`, `CallToolWithAlternateFilters` (`IList<McpRequestFilter<TParams,TResult>>`).
Verified delegate shape (reflection probe, 2.1.0):
`McpRequestHandler<TParams,TResult> Invoke(McpRequestHandler<TParams,TResult>)` —
a filter is a WRAPPER/COMPOSER: given the next handler it returns a handler, so
registration is `filter => next => async (ctx, req, ct) => { …; return await
next(ctx, req, ct); }` (onion layers; the last filter's `next` is the real
dispatch). Contrast: `WithCallToolHandler(builder, handler)` REPLACES the default
dispatch — the SDK docs pair it with `WithListToolsHandler` to build a "complete
tools implementation" from scratch, so a custom CallTool handler must implement
dispatch itself (it does NOT chain onto `.WithTools<T>()`). Filters compose;
handlers replace. A `CallToolFilters` hook fires ONLY on `tools/call` — the
precise "any tool call" signal — whereas an ASP.NET middleware on `/mcp` sees every
request (initialize/list included) and keeps counting during long-lived connections.
Pick the filter when the signal must mean user tool traffic, middleware when any
protocol traffic counts.

### Tool name derivation

Method name → tool name: `Async` suffix stripped, then `JsonNamingPolicy.SnakeCaseLower`
(`MemoryWrite` → `memory_write`; `WriteAsync` → `write`). Pin the contract with
`[McpServerTool(Name = "…")]` so renames can't silently change the agent-facing name.

### Constructor injection (verified)

`WithTools<T>()`/`WithPrompts<T>()` register each method via
`McpServerTool.Create(method, r => CreateTarget(r.Services, typeof(T)), …)` where
`CreateTarget` = `ActivatorUtilities.CreateInstance(services, type)` — the tool/prompt class is
constructed **per invocation** from DI. Any ctor dependency must be registered in `Program.cs`
(`AddSingleton<IMemoryStore, SqliteMemoryStore>()`, …); unit tests construct the class directly
(`NullLogger<T>.Instance` for `ILogger<T>` params).

### `WithToolsFromAssembly` ≠ `WithTools<T>()`

`WithToolsFromAssembly` only picks types marked `[McpServerToolType]`. Prefer explicit
`.WithTools<MemoryTools>()` — compile-checked, no extra attribute.

### Dual-transport shared DI

Both the stdio and HTTP branches of `Program.cs` need the same service registrations — extract a
top-level `static void ConfigureServices(IServiceCollection)` local function and call it from both
branches (transport wiring stays per-branch). Pitfall: registering the SAME type twice with different
options (e.g. one `SqliteConnectionFactory` with `loadCloudSync: true` for sync, one without for the
store) — the LAST `AddSingleton<T>` wins for plain resolution; construct the special instance via a
factory lambda instead of relying on two registrations of one type.

## 3d. E2E-testing the full server over the HTTP transport

The unit-test pattern in §4 tests tool classes in isolation. To prove the WHOLE stack — tools, DI,
store, native extensions, JSON-RPC transport — boot the real server in-process and drive it with a
real MCP client. Full recipe (factory class, client wiring, provisioning, assertion strategy):
`references/e2e-http-transport-testing.md`. Key facts verified against MCP SDK 2.0.0:

- **`ModelContextProtocol.Core` is the client package** (separate from `ModelContextProtocol`).
  `HttpClientTransport` accepts an EXISTING `HttpClient` — pass `WebApplicationFactory.CreateClient()`
  with `ownsHttpClient: true`. `McpClient.CreateAsync(transport)` connects (the `McpClientFactory`
  shown in older blog posts is a different package/era).
- **`CallToolResult.IsError` is NULL on success** — the MCP protocol omits `isError` unless true.
  Assert `result.IsError.ShouldNotBe(true)`, never `ShouldBe(false)`.
- **Server env vars are read BEFORE the host builds** — a server that picks transport from
  `MCP_TRANSPORT` / data root from an env var reads them in `Program.cs` top-level code,
  so `ConfigureWebHost`/`ConfigureAppConfiguration` are too late. Set real env vars in the factory
  ctor (restore in Dispose); because that mutates the process, E2E tests MUST live in a serial
  xunit collection (`[CollectionDefinition(DisableParallelization = true)]`).
- **`HttpTransportMode.StreamableHttp`** is the enum value (not `Streamable`).
- **E2E catches DI bugs unit tests can't** — a service registered with a ctor dependency that is
  never registered (e.g. `SyncService(SyncOptions)` while only the containing options record is
  registered) passes tool-class unit tests but fails `builder.Build()` with
  "Unable to resolve service for type 'X' while attempting to activate 'Y'". Fix: register the
  inner options object too. This is the strongest argument for an E2E layer: it proved a real
  startup bug in one run.
- **Test tiers**: xunit traits `[Trait("Category", "Unit"|"Integration"|"E2E")]` +
  `[Trait("Speed", "Fast"|"Slow")]` make the suite filterable —
  `dotnet test --filter "Category=Unit&Speed=Fast"`. Use `Assert.Skip(...)` (xunit.v3) to skip
  honestly when native extensions / a model are unavailable — never a false green.

## 3e. Access control at the tool boundary (per-project/global modes)

Verified while landing ai-raccoon P2 (FR-NM-2, ro/rw/full modes). When tools must be gated by
a per-project policy, keep the pattern split across layers:

- **Pure policy in the Core/domain project** (MCP-free): `enum AccessMode { Ro, Rw, Full }`,
  `enum AccessRequirement { Read, Write, Destructive }`, and a static policy type with
  `Resolve(AccessMode? global, AccessMode? perProject)` → `perProject ?? global ?? Rw`,
  `Allows(mode, requirement)` (Read always; Write needs Rw|Full; Destructive needs Full),
  `RequiredFor(requirement)` (Write→rw, Destructive→full), plus case-insensitive
  `Parse`/`Serialize` for settings values.
- **Enforcement impl lives in the server project** — it throws `McpException`, which Core
  must not reference. Injected `IMemoryAccessGuard` into the tools class:
  `EnsureAsync(projectId, requirement, toolName, ct)` throws
  `McpException("access-denied: <tool> requires mode <ro|rw> (current <mode>)")`. **Short-circuit
  `Read` before any settings lookup** — reads are allowed in every mode and a read tool shouldn't
  pay a settings round-trip. Register in DI: `AddSingleton<IMemoryAccessGuard>(sp => new
  MemoryAccessGuard(sp.GetRequiredService<IMemoryStore>()))`.
- **Settings storage**: bank settings table keys `access.mode.global` + `access.mode.project:<id>`
  (per-project overrides global; unset = rw). Seed the global once at bank open from an env var
  (`AIRACCOON_ACCESS_MODE`) with `INSERT ... ON CONFLICT(key) DO NOTHING` so an operator-set row
  wins; the guard reads lazily per call. Don't read env in the guard itself — it makes unit tests
  environment-dependent.
- **Tool classification**: reads (search/list/stats/workspace_status/sweep dry_run=true) un-gated;
  writes (write/ingest/share/configure/embed_pending/workspace_begin/sync) rw+; destructive
  (delete/delete_context/workspace_consolidate/workspace_discard/sweep dry_run=false) full.
  For a `dryRun` bool tool, gate on `dryRun ? Read : Destructive`.
- **Gating tests use the REAL guard + a fake store with a `Settings` dictionary** — no fake guard
  needed; the whole store→resolve→enforce→McpException path is exercised. The fake store implements
  `GetSettingAsync`/`SetSettingAsync` from the dictionary.
- **Gate-filter naming**: `dotnet test --filter 'AccessMode'` is a bare-value filter = substring
  match on `FullyQualifiedName`. Name the test CLASSES with the filter token
  (`AccessModePolicyTests`, `AccessModeGuardTests`, `MemoryToolsAccessModeTests`) so the targeted
  gate actually runs them.
- **When gating lands on existing tools, existing tests break by design**: unit tests that called
  destructive tools (e.g. workspace consolidate) must seed the permissive mode in the fake first;
  E2E tests over the real server hit access-denied because the fresh bank defaults to rw — seed the
  bank by setting the env var in the test factory ctor (restore in Dispose), which also exercises
  the seed-at-open path.

## 4. Testing pattern

```csharp
using System.Net;
using System.Text.Json;

public class MyToolsTests
{
    private static (MyTools tools, MockHttpHandler handler) CreateTools(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseJson = null)
    {
        var handler = new MockHttpHandler(statusCode, responseJson ?? """{"ok":true}""");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:7071")
        };
        return (new MyTools(httpClient), handler);
    }

    [Fact]
    public async Task DoSomething_PostsToCorrectEndpoint()
    {
        var (tools, handler) = CreateTools(responseJson: """{"result":"done"}""");
        var result = await tools.DoSomething("res-1", 100m);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/resources/res-1/action", handler.LastRequestUri!.AbsolutePath);
        Assert.Contains("done", result);
    }

    [Fact]
    public async Task DoSomething_SendsCorrectBody()
    {
        var (tools, handler) = CreateTools();
        await tools.DoSomething("res-1", 200m, "Yearly");
        var body = handler.LastRequestBody!;
        Assert.Equal(200, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("Yearly", body.RootElement.GetProperty("period").GetString());
    }

    [Fact]
    public async Task DoSomething_ThrowsOnNonSuccessStatusCode()
    {
        var (tools, _) = CreateTools(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => tools.DoSomething("x", 1m));
    }

    // MockHttpMessageHandler that captures request details for assertions
    private sealed class MockHttpHandler(HttpStatusCode statusCode, string responseJson)
        : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public JsonDocument? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                var bodyString = await request.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(bodyString))
                    LastRequestBody = JsonDocument.Parse(bodyString);
            }
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
```

### What to test per tool

1. **Endpoint routing**: correct HTTP method + URL path
2. **Request body**: all parameters serialized correctly with expected defaults
3. **Response handling**: tool returns the API's JSON string
4. **Error propagation**: `EnsureSuccessStatusCode()` throws on non-2xx

## 5. Adding tools to an existing MCP server

When the MCP server project already has tool classes (e.g. `SalaryTools`), adding new tools follows a tighter sequence:

1. **Read the spec/requirements** to understand REST endpoint mapping (method, path, body, query params).
2. **Study the existing tool** as a template — match its style, DTO patterns, and `EnsureSuccessStatusCode()` usage.
3. **Write tests first** (TDD RED) — create the test file with `MockHttpHandler`, write all test cases from the spec's test matrix. Run `dotnet test` to confirm they fail (compilation error or method-not-found).
4. **Write the implementation** — create the tool class, register in `Program.cs`.
5. **Build and test** (TDD GREEN) — `dotnet build && dotnet test`.
6. **Update docs** — `docs/functional-specification.md` §3 tool list + counts.

### Program.cs registration for a new tool class

```csharp
// Add typed HttpClient (same apiBaseUrl as existing tools)
builder.Services.AddHttpClient<SignalTools>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// Chain WithTools<>() onto the existing registration
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RandomNumberTools>()
    .WithTools<SalaryTools>()
    .WithTools<SignalTools>();  // new
```

### POST with empty body

For tools that POST without a request body (e.g. confirming a proposal, triggering a check), use `PostAsync` with `null` content — not `PostAsJsonAsync`:

```csharp
var response = await httpClient.PostAsync($"/api/resource/{id}/action", null);
response.EnsureSuccessStatusCode();
return await response.Content.ReadAsStringAsync();
```

### Query parameter construction

For tools with optional filter parameters, build query strings from non-null args:

```csharp
var queryParams = new List<string>();
if (applicationId is { }) queryParams.Add($"applicationId={Uri.EscapeDataString(applicationId)}");
if (disposition is { }) queryParams.Add($"disposition={Uri.EscapeDataString(disposition)}");
if (source is { }) queryParams.Add($"source={Uri.EscapeDataString(source)}");
if (since is { }) queryParams.Add($"since={Uri.EscapeDataString(since)}");

var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
var response = await httpClient.GetAsync($"/api/resource{query}");
```

Always use `Uri.EscapeDataString()` for values going into URLs — never raw string interpolation.

### Error handling test pattern

A single `[Theory]` with `[InlineData]` covers all non-success status codes for any tool:

```csharp
[Theory]
[InlineData(HttpStatusCode.NotFound)]
[InlineData(HttpStatusCode.Conflict)]
[InlineData(HttpStatusCode.BadRequest)]
public async Task AnyTool_ThrowsOnNonSuccessStatusCode(HttpStatusCode errorStatus)
{
    var (tools, _) = CreateTools(errorStatus);
    await Assert.ThrowsAsync<HttpRequestException>(() => tools.GetSignal("sig-x"));
}
```

## 6. Pitfalls

- **`Task<object>` return type** — causes boxing of `JsonElement`, losing `ValueKind`. Always use `Task<string>` and serialize/deserialize explicitly.
- **Missing `using System.Net.Http.Json`** — `PostAsJsonAsync` and `PutAsJsonAsync` are extension methods in this namespace. Without the `using`, you get `CS1061: 'HttpClient' does not contain a definition for 'PostAsJsonAsync'`. The NuGet package `Microsoft.Extensions.Http` is necessary but not sufficient — the source file also needs the `using` directive.
- **Missing `Microsoft.Extensions.Http`** — `AddHttpClient<T>()` lives in this package. Without it you get `CS1061: 'IServiceCollection' does not contain a definition for 'AddHttpClient'`.
- **Missing `InternalsVisibleTo`** — if tool classes are `internal`, the test project can't reference them without this attribute in the `.csproj`.
- **`using System.Text.Json.Serialization` vs `System.Text.Json`** — `JsonElement` lives in `System.Text.Json`, not `System.Text.Json.Serialization`. The latter is for `[JsonConverter]` attributes.
- **Central package management** — if the repo uses `Directory.Packages.props`, add new package versions there, not in individual `.csproj` files.
- **Forgetting to register the new tool class** — after creating a tool class, you must add both `AddHttpClient<T>()` and `.WithTools<T>()` in `Program.cs`. Missing either causes a runtime DI failure, not a compile error.
- **xUnit1051: CancellationToken calls must pass `TestContext.Current.CancellationToken`** — the xunit.v3 analyzer flags any call to a method accepting a CancellationToken that doesn't pass `TestContext.Current.CancellationToken`, and under `TreatWarningsAsErrors` it's a build error, not a warning. Every `await _tools.X(...)` in a test needs the token argument (`cancellationToken: TestContext.Current.CancellationToken`) — including inside `Should.ThrowAsync<T>(() => ...)` lambdas AND `Task.Delay(...)` (it has a token overload; the analyzer flags it the same way).
- **Shouldly lambdas are expression trees — no `is` patterns.** `collection.ShouldContain(x => x.Field is null)` fails to compile with `CS8122: An expression tree may not contain an 'is' pattern-matching operator`. Use `ShouldHaveSingleItem()` + `.ShouldBeNull()`/`.ShouldBe(...)` on the result, or a plain `==` comparison in the predicate.
- **`virtual` members require non-sealed classes** — a test fake that overrides a method (`override Task<...> GetEntryAsync`) fails with `CS0549: 'new virtual member in sealed type` when the class is `sealed`. Either unseal the class or extract an interface; unsealing is the smaller change.
- **Namespace shadows type name** — a folder named after a domain type (`Infrastructure/Workspace/` holding a service) makes `using AiRaccon.Core.Workspace;` ambiguous: `Workspace` resolves to the namespace, not the type (`CS0118: 'Workspace' is a namespace but is used like a type`). Fix with a using alias: `using WorkspaceRecord = AiRaccon.Core.Workspace.Workspace;`.
- **`Enum.TryParse<T>(string, out T)` is case-sensitive in .NET 10** — the parameterless overload does NOT ignore case, so `MCP_TRANSPORT=http`/`HTTP` silently fall back to the default (stdio) and the server comes up on the wrong transport, with tests only catching it if they pin the case-insensitive contract. Pass `ignoreCase: true` explicitly: `Enum.TryParse<T>(value, ignoreCase: true, out var result)`. This is the enum-based sibling of the `McpTransportSelector.UseHttp` lesson in §3b — if you move transport selection from a string compare to an enum, keep the case-insensitivity.
- **`CommunityToolkit.Diagnostics.Guard.IsNotNull(x)` returns VOID, not the value** — you cannot write `_field = Guard.IsNotNull(x);` or chain `.ToList()` off it (`CS0023`/`CS0029`). And with `Nullable enable` + DI-injected constructors, the `x ?? throw new ArgumentNullException(nameof(x))` guards on non-nullable reference params are redundant — the compiler and container guarantee non-null. The clean move is to DELETE those ctor guards outright (simpler than converting them) and keep CommunityToolkit guards only for real value validation (whitespace/range) on domain records. A reviewer will ask "do we need those null checks given NRT?" — the answer is no for DI ctor params.
- **Anonymous-type spread `...` does not exist in C#** — `new { a = 1, ...rest }` is `CS8635: Unexpected character sequence '...'` even on .NET 10 with `LangVersion latest` (verified in a scratch project). The spread was proposed but never shipped; only collection expressions support `..`. Write the anonymous type out explicitly or use a named DTO.
- **Dapper record-ctor materialization breaks on SQLite INTEGER → int** — Dapper reads SQLite INTEGER as `long`, so `record Row(long CreatedAt, int AccessCount)` fails with "A parameterless default constructor or one matching signature ... is required for ... materialization". Use mutable class DTOs for anything Dapper materializes (full pattern + FTS5/vec0 store-layer traps: `references/managed-sqlite-store-patterns.md`).
- **Extending a widely-implemented Core interface breaks every fake AND the decorator host in one compile** — adding a member to `IMemoryStore` produced CS0535 in 5 test fakes plus missing forwarders on `MemoryExtensionHost` (the `IMemoryStore` decorator that runs extension hooks). Add the new members to ALL fakes (trivial defaults are fine) + forwarding members on the decorator in the SAME commit as the interface change — the build is red until every implementer is updated.
- **Asserting `dotnet build` output in gate scripts**: "0 Warning(s)" and "0 Error(s)" print on SEPARATE lines — a single-line regex like `grep -qE "0 Warning\\(s\\).*0 Error\\(s\\)"` always fails (false FAIL). Check each line separately (or `grep -z`).
- **`IHttpClientFactory` needs an explicit `services.AddHttpClient()`** — the
  `Microsoft.Extensions.Http` package reference registers nothing; any DI service taking
  `IHttpClientFactory` fails at first resolution ("Unable to resolve service for type
  'System.Net.Http.IHttpClientFactory' ..."). Likewise the NON-generic `ILogger` is NOT
  registered by default hosts (`Host.CreateApplicationBuilder`/`WebApplication.CreateBuilder`
  register `ILoggerFactory` + `ILogger<T>` only) — `GetRequiredService<ILogger>()` crashes
  the boot; use `ILoggerFactory.CreateLogger("Program")`. And System.CommandLine's
  `OptionResult.GetValueOrDefault<T>()` THROWS on invalid option values (`--transport ftp`)
  — a parse-first facade that reads options on failed parses must try/catch → defaults
  (errors live in the Errors list, never thrown). Only a live boot / E2E factory catches
  these (Program top-level statements are invisible to unit tests). Full detail + the
  diagnosis path: `references/di-host-pitfalls.md`.

## Web SDK / packaging pitfalls (stdio + HTTP dual-mode)

- **NETSDK1151: "referenced project is a self-contained executable"** — a test project cannot reference an Exe whose csproj sets `SelfContained=true`/`PublishSelfContained=true`/`PublishSingleFile=true`. Fix: move those three properties OUT of the csproj into `Properties/PublishProfiles/<name>.pubxml` (set `RuntimeIdentifier` + the three props there). The project stays referenceable; `dotnet publish -p:PublishProfile=<name>` still produces the self-contained single-file.
- **Web SDK (`Microsoft.NET.Sdk.Web`) defaults `IsPackable=false`** — `dotnet pack` silently produces nothing ("cannot be packaged because packaging has been disabled"). Add `<IsPackable>true</IsPackable>` explicitly when the MCP server also ships as a tool/package.
- **`RuntimeIdentifiers` (multi-RID list) breaks `dotnet pack`** — pack tries to publish for every RID and fails on missing per-RID outputs (`MSB3030` on `bin/Debug/net10.0/<rid>/...`), especially with `--no-build`. Fix: pack only the host RID — `dotnet pack -p:RuntimeIdentifiers=$(NETCoreSdkRuntimeIdentifier) --no-build`. The RID-suffixed + plain nupkgs both get produced.

- **RID pack emits a binary-free SHELL package + a RID companion — push BOTH or the local feed is unusable.** With `PackAsTool=true`, `dotnet pack -p:RuntimeIdentifiers=<host-rid>` produces `Id.Version.nupkg` (nuspec + README + `.mcp/` + `tools/net10.0/any/DotnetToolSettings.xml` ONLY, no binaries, ~4 KB) plus `Id.<rid>.Version.nupkg` (the real payload under `tools/net10.0/<rid>/`). The shell's `DotnetToolSettings.xml` declares `<RuntimeIdentifierPackage RuntimeIdentifier="<rid>" Id="Id.<rid>" />`, so `dotnet tool install` MUST resolve the companion from the SAME feed. Pushing only the shell makes install fail with "Version X of package <id>.<rid> is not found in NuGet feeds". Fix: push everything produced — `dotnet nuget push "$(PackageOutputPath)*.nupkg" --source <feed> --skip-duplicate` — and PROVE the feed serves an install: `dotnet tool install <id> --add-source <feed> --tool-path /tmp/x` (a file landing in the feed is not the same as the feed working). See `references/rid-tool-package-deploy.md` for evidence and probes.
- **NU1510: `Microsoft.Extensions.Hosting` "will not be pruned"** — under `Microsoft.NET.Sdk.Web`, Hosting comes from the ASP.NET Core shared framework; the direct `PackageReference` is redundant (and TreatWarningsAsErrors turns the NU1510 warning into an error). Remove the package reference; keep the `using`s.
- **`StaticWebAssetsEnabled`** — under the Web SDK, pack/publish runs StaticWebAssets targets that error on missing `staticwebassets.build.json` per RID. Set `<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>` in an MCP server that has no static assets.
- **`dotnet run` applies launchSettings env vars OVER the shell env** — `MCP_TRANSPORT=http dotnet run` silently uses the launch profile's `environmentVariables`, not your exported var (the server came up in stdio mode). Add `--no-launch-profile` when you need the shell env to win (e.g. smoke-testing transport selection from a script).
- **`dotnet run` prints "Using launch settings from …" to STDOUT before the first protocol message** — this corrupts the newline-delimited JSON-RPC stream for stdio MCP *clients* that launch the server via `dotnet run` in their client config (`.vscode/mcp.json`, `.mcp.json`). Any client config must add `--no-launch-profile` to `args`; without it strict clients fail to parse the stream (verified empirically: the notice is absent with `--no-launch-profile`). Document this in the README's client-config example — a how-to that omits it is a real bug. Note the HTTP-side pairing: `--launch-profile http` (with the profile's `applicationUrl`) is the reliable way to select the HTTP transport, since the profile sets both the env var and the port.
- **`dotnet run` with no `--launch-profile` uses the FIRST profile in launchSettings.json** — list `stdio` first so plain `dotnet run` serves stdio. Verify which branch came up by checking the port: `lsof -nP -iTCP:8080 -sTCP:LISTEN` (nothing listening + StdioServerTransport in the log = stdio branch). From a src/ layout repo root, `dotnet run` fails with "Couldn't find a project to run" — always pass `--project src/<Proj>`.
- **`MapMcp()` default pattern is `""` (root)** in MCP SDK 2.x — endpoints land at `/`, not `/mcp`. Call `app.MapMcp("/mcp")` explicitly so clients use the conventional path.
- **Local NuGet feed deploy gated on env var** — an MSBuild target can gate on a lowercase env var because MSBuild imports env vars case-insensitively: `Condition="'$(DOTNET_ENV)' == 'local'"` fires when the shell has `dotnet_env=local`. Pattern: `MakeDir` the feed dir, `dotnet pack ... -p:RuntimeIdentifiers=$(NETCoreSdkRuntimeIdentifier) -o "$(PackageOutputPath)"`, then push BOTH produced nupkgs — `dotnet nuget push "$(PackageOutputPath)*.nupkg" --source <feed> --skip-duplicate` (pushing only `$(PackageId).$(PackageVersion).nupkg` lands the binary-free shell and breaks install — see the RID companion bullet above); register `<add key="local" value="./.nupkg-local/" />` in nuget.config and gitignore the feed. Set `PackageOutputPath` in `Directory.Build.props` (repo-root `$(MSBuildThisFileDirectory).nupkg/`) so it applies repo-wide. **Add an up-to-date guard** so plain `dotnet build`/`dotnet test` (which builds the src project via ProjectReference) doesn't re-pack + re-push on every run when the env var is exported: `Inputs="$(MSBuildProjectFullPath);$(TargetPath)" Outputs="$(PackageOutputPath)$(PackageId).$(NETCoreSdkRuntimeIdentifier).$(PackageVersion).nupkg"` on the target — MSBuild skips the whole target when the output nupkg is newer than the project file and built dll.

## 7. Docs fold-in checklist

When shipping MCP tools alongside a feature, update these docs:

| Doc | What to add |
|---|---|
| `docs/functional-specification.md` §2 | New REST endpoint rows in the API table |
| `docs/functional-specification.md` §3 | New MCP tool names in the tool list; update tool count |
| `docs/features/README.md` | Mark feature dossier as `Shipped` |
| `docs/features/<feature>/requirements.md` | Update status line to `Shipped` |
| `docs/architecture.md` §5 | Extension-point table rows (if new interfaces added) |
| `docs/data-model.md` | New entities/records (if any) |
| `docs/flows.md` | New flow diagrams (if any) |

If architecture.md, data-model.md, and flows.md already have the content from a prior task, only update functional-specification.md and the status markers.
