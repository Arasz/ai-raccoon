## 7. MCP SDK 2.0 API specifics (verified against 2.0.0)

Facts below were verified by reflection on the installed `ModelContextProtocol.Core` 2.0.0 assembly and by reading the v2.0.0 sources (`AIFunctionMcpServerTool.cs`, `McpServerImpl.cs`,
`McpServerBuilderExtensions.cs`). Full notes + a reusable probe recipe:
`references/mcp-csharp-sdk-2.0-apis.md`.

**Backing the server with the sqliteai native extensions (sqlite-memory/vector/sync)?**
Store-layer traps — module basename ↔ `sqlite3_<name>_init` entry point, `memory_add_text`
returning 1 not the hash, deferred-embeddings requirement, real-extension integration-test pattern — are in `references/sqlite-ai-store-integration.md` (verified against the real binaries). NOTE: that file documents the SUPERSEDED
extension-backed store; the current managed `memory.db` design (idempotent schema init, FTS5 external-content search, sqlite-vec from NuGet, Dapper record-vs-class DTO traps, `...`-spread CS8635) is
`references/managed-sqlite-store-patterns.md`. Only the cloudsync extension still loads, until the own-sync wave removes it.

**Local GGUF embedding model for the llama.cpp engine?** Model pick (all-MiniLM-L6-v2 Q5_K_M ~21 MB Apache-2.0 verified; nomic-embed-text-v1.5 Q8_0 as the documented reference), pinned download recipe + SHA-256, `<APP>_TEST_GGUF`
test-gating, the verified engine matrix (local vs vectors.space ONLY — LM Studio/Ollama are not configurable, the remote URL is hardcoded), and the `memory_search` Dapper blob-affinity/record-ctor trap:
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
- `[McpServerPrompt(Name=…)]` overrides the derived name; `[Description]` on the method becomes the prompt description; `[Description]` on parameters becomes the agent-facing arg description.
- **Prompt copy is C# string content — watch the placeholders.** Inside an interpolated raw string (`$"""…"""`), `{project-id|*}` is parsed as an interpolation hole and fails to compile (CS1733 "Expected expression", CS9006 "does not start
  with enough '$' characters"). Use `<project-id|*>` angle-bracket placeholders in prompt text (hit when adding watch-CLI examples to a guide).
- **Agent-facing prompts are the product, not boilerplate — design and pin them.** Review the distributed prompts for what a fresh agent actually needs: (1) a SEARCH-FIRST retrieval ladder — search the memory store first with 2-3 query
  formulations (exact phrase → keywords → plain-English restatement), escalate to the host's web/code search only by result (decisive hit → use and cite its source; partial → combine with one targeted external search; none → search
  externally, then write the finding back so the next lookup is answered from memory); (2) the full tool map with WHEN to use each (ingest/stats/sync included, not just search/write); (3) setup PREREQUISITES for CLI-configured features —
  e.g. the watch scope allowlist + enablement are CLI-only (`watch scope add` + `watch
  enable`), and `memory_watch_add` fails with `watching-disabled`/`path-outside-scope` until they exist, so the guide must say so. Pin the guide's load-bearing sentences with content-assertion tests
  (`guide.ShouldContain("memory_watch_add")`) — prompt copy is the agent contract and TDD applies to it like any behavior (2 new facts pinned a memory-first ladder + watch usage; the old guide said only "search before asking").

### Tool error signaling — the message-drop trap

`McpServerImpl` catches exceptions from tool invocation and returns `CallToolResult { IsError = true }` — but `CreateToolCallErrorResult` includes the exception message in the error text **only when the exception is `McpException`**
(namespace `ModelContextProtocol`, ctor `(string message)`). A plain
`ArgumentException`/`InvalidOperationException` surfaces as the generic
`"An error occurred invoking '<tool>'."` with NO reason — useless for agent-facing coded errors (spec-style `invalid-params: …`, `sync-not-configured`, …).

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

Option 2 literally "returns a structured tool error" and gives exact control of the text. Combined with record success returns, the method returns `Task<object>` and the SDK's result switch dispatches on the RUNTIME type (`CallToolResult` →
pass-through honoring `IsError`; record → JSON text content + structured content; `string` → `TextContentBlock`; `null` → empty content). The union pattern is verified working — but keep the §6 `Task<object>`/`JsonElement` boxing caveat for
tools that return
`JsonElement` directly.

### Request filters — per-tool-call interception (SDK 2.1.0)

`ModelContextProtocol.Server.McpRequestFilters` (verified by reflection probe on the installed 2.1.0 assembly; the §7 2.0.0 notes predate it) exposes per-method filter lists — the hook for activity signals (idle watchdog), auth, or per-call
logging without touching tool bodies or ASP.NET middleware: `ListToolsFilters`,
`CallToolFilters`, `CallToolWithAlternateFilters` (`IList<McpRequestFilter<TParams,TResult>>`). Verified delegate shape (reflection probe, 2.1.0):
`McpRequestHandler<TParams,TResult> Invoke(McpRequestHandler<TParams,TResult>)` — a filter is a WRAPPER/COMPOSER: given the next handler it returns a handler, so registration is `filter => next => async (ctx, req, ct) => { …; return await
next(ctx, req, ct); }` (onion layers; the last filter's `next` is the real dispatch). Contrast: `WithCallToolHandler(builder, handler)` REPLACES the default dispatch — the SDK docs pair it with `WithListToolsHandler` to build a "complete
tools implementation" from scratch, so a custom CallTool handler must implement dispatch itself (it does NOT chain onto `.WithTools<T>()`). Filters compose; handlers replace. A `CallToolFilters` hook fires ONLY on `tools/call` — the precise
"any tool call" signal — whereas an ASP.NET middleware on `/mcp` sees every request (initialize/list included) and keeps counting during long-lived connections. Pick the filter when the signal must mean user tool traffic, middleware when
any protocol traffic counts.

### Tool name derivation

Method name → tool name: `Async` suffix stripped, then `JsonNamingPolicy.SnakeCaseLower`
(`MemoryWrite` → `memory_write`; `WriteAsync` → `write`). Pin the contract with
`[McpServerTool(Name = "…")]` so renames can't silently change the agent-facing name.

### Constructor injection (verified)

`WithTools<T>()`/`WithPrompts<T>()` register each method via
`McpServerTool.Create(method, r => CreateTarget(r.Services, typeof(T)), …)` where
`CreateTarget` = `ActivatorUtilities.CreateInstance(services, type)` — the tool/prompt class is constructed **per invocation** from DI. Any ctor dependency must be registered in `Program.cs`
(`AddSingleton<IMemoryStore, SqliteMemoryStore>()`, …); unit tests construct the class directly (`NullLogger<T>.Instance` for `ILogger<T>` params).

### `WithToolsFromAssembly` ≠ `WithTools<T>()`

`WithToolsFromAssembly` only picks types marked `[McpServerToolType]`. Prefer explicit
`.WithTools<MemoryTools>()` — compile-checked, no extra attribute.

### Dual-transport shared DI

Both the stdio and HTTP branches of `Program.cs` need the same service registrations — extract a top-level `static void ConfigureServices(IServiceCollection)` local function and call it from both branches (transport wiring stays
per-branch). Pitfall: registering the SAME type twice with different options (e.g. one `SqliteConnectionFactory` with `loadCloudSync: true` for sync, one without for the store) — the LAST `AddSingleton<T>` wins for plain resolution;
construct the special instance via a factory lambda instead of relying on two registrations of one type.
