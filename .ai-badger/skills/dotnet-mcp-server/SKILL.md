---
name: dotnet-mcp-server
description: "Use when adding MCP (Model Context Protocol) tools or servers to a .NET project: tool/prompt registration with [McpServerTool]/[McpServerPrompt], stdio or Streamable-HTTP host wiring (dual-mode, port traps), DI + typed HttpClient for REST-backed tools, unit tests with mock HTTP handlers, tool-inventory tests that assert the REGISTERED surface, and SDK 2.x specifics (McpException error signaling, request filters, tool-name derivation)."
description: Implement MCP (Model Context Protocol) servers in .NET using the ModelContextProtocol NuGet package — tool registration, DI wiring, HttpClient for REST-based tools, and unit testing with mock HTTP handlers. Use when adding MCP tools to a .NET project, wiring up a stdio MCP server host, or testing MCP server tool classes.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, mcp, csharp, server, tools, model-context-protocol]
    related_skills: [dotnet-domain-modeling, dotnet-tool-publishing]
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


> HTTP transport and dual-mode servers: read `references/http-transport.md` when wiring a Streamable HTTP transport or a dual-mode stdio+HTTP server.

## 5. Giving a stdio MCP server a CLI (System.CommandLine, parse-first)

For a stdio server, every byte of CLI output must go to stderr: System.CommandLine's default
help renders to stdout (`InvocationConfiguration.Output`) and parse errors print to stderr
THEN render help to stdout — either would corrupt the protocol stream. Parse first, never
invoke actions:

```csharp
var result = new CommandLineBuilder(BuildRootCommand()).UseDefaults().Build().Parse(args);
if (result.Errors.Count > 0 || result.Action is HelpAction || result.Action is VersionOptionAction)
    return Render(result, Console.Error);   // own renderer; stdout untouched; 0 help/version, 1 errors
```

Key facts (full detail + evidence: read `references/cli-args-for-stdio-mcp.md` when adding CLI args to a stdio MCP server):
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
- **Tool command name defaults to the ASSEMBLY name** (`<AssemblyName>`), not PackageId — set
  `<ToolCommandName><tool></ToolCommandName>` for a kebab-case command that works on
  case-sensitive filesystems (`.mcp.json` `"command"`).
- `--version` prints `AssemblyInformationalVersion` (defaults to `1.0.0`), not `PackageVersion`.
- `WebApplicationFactory<T>` passes EMPTY args to the entry point — CLI behavior is
  unit-test + manual-smoke territory; env behavior stays E2E-able (factory env mutation).

## 6. Tool inventory tests — assert the REGISTERED surface, not the class

A tool-inventory test that reflects over the tool CLASS (`typeof(Tools).GetMethods()`
filtered by `[McpServerTool]`) proves the class carries tools — it does NOT prove the
server registers them. A dropped `.WithTools<T>()` passes every class-level test and ships.

**Real incident (a MCP server, 2026-08-06):** a host-refactor PR (01f0f63, "separate
host paths") removed `.WithTools<WatchTools>()` from BOTH `ServerSetup` host paths
(stdio app host + web host). The class-level `WatchToolsInventoryTests` stayed green; the
shipped binary exposed **16 tools instead of 19** — MCP clients lost
`memory_watch_add/status/remove` while docs and prompts still advertised them. Only a live
`tools/list` probe caught it. `ServerSetupHostTests` pinned transport shape only, never
the registered tool set — that was the gap.

Two guards against this class of regression:
1. **Host-level test**: boot the host (or resolve the built MCP server services) and
   enumerate the ACTUAL registered tool names; assert the full expected set. Registration
   drops then fail the suite at review time, not at release time.
2. **Live binary probe** (release gate): run `tools/list` against the published binary and
   diff the tool set. **Bare `tools/list` over stdio returns NOTHING without a prior
   `initialize` handshake** — the probe must send `initialize` → `notifications/initialized`
   → `tools/list` and parse newline-delimited JSON. Full recipe + probe script:
   read `references/tool-registration-surface-test.md` when writing tool-inventory tests.

Fast negative filter before either guard: `strings <server.dll> | grep -o "memory_watch_[a-z]*"`
— tool-name strings missing from the binary means registration is moot (but strings
presence does NOT prove registration; only the host test / live probe does).


> SDK 2.x API specifics: read `references/sdk-2-apis.md` when hitting SDK 2.0/2.1 API questions (McpException error signaling, request filters, tool-name derivation, constructor injection).

## 8. E2E-testing the full server over the HTTP transport

The unit-test pattern in §4 tests tool classes in isolation. To prove the WHOLE stack — tools, DI,
store, native extensions, JSON-RPC transport — boot the real server in-process and drive it with a
real MCP client. Full recipe (factory class, client wiring, provisioning, assertion strategy):
Read `references/e2e-http-transport-testing.md` when E2E-testing over HTTP. Key facts verified against MCP SDK 2.0.0:

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


> Access control: read `references/access-control.md` when setting per-project/global modes at the tool boundary.


> Testing pattern: read `references/testing-pattern.md` when writing tool tests (what to test per tool).


> Adding tools: read `references/adding-tools.md` when adding tools to an existing MCP server.

## 12. Gotchas
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
- **Namespace shadows type name** — a folder named after a domain type (`Infrastructure/Workspace/` holding a service) makes `using <Proj>.Core.Workspace;` ambiguous: `Workspace` resolves to the namespace, not the type (`CS0118: 'Workspace' is a namespace but is used like a type`). Fix with a using alias: `using WorkspaceRecord = <Proj>.Core.Workspace.Workspace;`.
- **`Enum.TryParse<T>(string, out T)` is case-sensitive in .NET 10** — the parameterless overload does NOT ignore case, so `MCP_TRANSPORT=http`/`HTTP` silently fall back to the default (stdio) and the server comes up on the wrong transport, with tests only catching it if they pin the case-insensitive contract. Pass `ignoreCase: true` explicitly: `Enum.TryParse<T>(value, ignoreCase: true, out var result)`. This is the enum-based sibling of the `McpTransportSelector.UseHttp` lesson in §4 — if you move transport selection from a string compare to an enum, keep the case-insensitivity.
- **`CommunityToolkit.Diagnostics.Guard.IsNotNull(x)` returns VOID, not the value** — you cannot write `_field = Guard.IsNotNull(x);` or chain `.ToList()` off it (`CS0023`/`CS0029`). And with `Nullable enable` + DI-injected constructors, the `x ?? throw new ArgumentNullException(nameof(x))` guards on non-nullable reference params are redundant — the compiler and container guarantee non-null. The clean move is to DELETE those ctor guards outright (simpler than converting them) and keep CommunityToolkit guards only for real value validation (whitespace/range) on domain records. A reviewer will ask "do we need those null checks given NRT?" — the answer is no for DI ctor params.
- **Anonymous-type spread `...` does not exist in C#** — `new { a = 1, ...rest }` is `CS8635: Unexpected character sequence '...'` even on .NET 10 with `LangVersion latest` (verified in a scratch project). The spread was proposed but never shipped; only collection expressions support `..`. Write the anonymous type out explicitly or use a named DTO.
- **Dapper record-ctor materialization breaks on SQLite INTEGER → int** — Dapper reads SQLite INTEGER as `long`, so `record Row(long CreatedAt, int AccessCount)` fails with "A parameterless default constructor or one matching signature ... is required for ... materialization". Use mutable class DTOs for anything Dapper materializes (full pattern + FTS5/vec0 store-layer traps: read `references/managed-sqlite-store-patterns.md` when hitting a store-layer trap).
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
  diagnosis path: read `references/di-host-pitfalls.md` when the host fails to start.


> Web SDK / packaging pitfalls: read `references/web-sdk-packaging-pitfalls.md` when packaging a dual-mode server for the Web SDK.


> Docs fold-in checklist: read `references/docs-foldin-checklist.md` when folding docs into an MCP server.

## References

- `references/mcp-csharp-sdk-2.0-apis.md` — SDK 2.x API surface (McpException, request filters, tool-name derivation); read when SDK 2.x API behavior is in question.
- `references/tool-registration-surface-test.md` — inventory tests that assert the REGISTERED surface, not the tool class; read when asserting the registered surface.
- `references/serve-mode-client-contract-and-di.md` — HTTP serve mode: client-config contract + watchdog DI triple; read when wiring serve-mode DI.
- `references/serve-mode-probe-attach.md` / `references/serve-probe-attach-verified.md` — the probe-attach pattern for an already-running server; read when attaching to a running server.
- `references/e2e-http-transport-testing.md` — E2E tests over the HTTP transport; read when E2E-testing over HTTP.
- `references/stdio-host-port-bind.md` — stdio host port-bind facts; read when a stdio host binds ports.
