# MCP C# SDK 2.0.0 — verified API surface (reflection + v2.0.0 sources)

All facts verified against the installed packages (`ModelContextProtocol` 2.0.0 meta-package →
`ModelContextProtocol.Core` 2.0.0, `ModelContextProtocol.AspNetCore` 2.0.0) and the v2.0.0 tag of github.com/modelcontextprotocol/csharp-sdk. Do not trust the API-reference website — its default docs version is v1.4.1 and the XML docs in
the package are incomplete for attribute members.

## Package layout

- `ModelContextProtocol` 2.0.0 is a **meta-package** over `ModelContextProtocol.Core` (+
  `Microsoft.Extensions.Caching.Abstractions`). All server types live in `ModelContextProtocol.Core.dll`.
- Key type locations:
    - `ModelContextProtocol.Server.McpServerToolAttribute` — ctor `()`, settable `Name`, `Title`,
      `Destructive`, `Idempotent`, `OpenWorld`, `ReadOnly`, `UseStructuredContent`,
      `OutputSchemaType`, `IconSource`.
    - `ModelContextProtocol.Server.McpServerPromptAttribute` — ctor `()`, settable `Name`, `Title`, `IconSource`.
    - `ModelContextProtocol.Protocol.CallToolResult` — parameterless ctor; settable `Content`
      (`IList<ContentBlock>`), `StructuredContent` (`JsonElement?`), `IsError` (`bool?`), `Meta`.
    - `ModelContextProtocol.Protocol.TextContentBlock` — parameterless ctor; settable `Text`, `Type`, `Annotations`, `Meta`.
    - `ModelContextProtocol.McpException` — ctors `()`, `(string message)`, `(string, Exception)`.
- There is NO `McpServerPromptResult` and no `McpServerPromptArgument` type in 2.0.0 — prompts return plain values (see constraint below).

## Reflection probe recipe (use when the next SDK version lands)

XML docs don't cover attribute members. Probe the installed package directly:

```bash
mkdir -p /tmp/mcp-reflect && cd /tmp/mcp-reflect
# reflect.csproj: net10.0 console, <PackageReference Include="ModelContextProtocol" Version="<ver>" />
cat > Program.cs <<'EOF'
using System.Reflection;
using ModelContextProtocol.Server;
var core = typeof(McpServerToolAttribute).Assembly;   // forces ModelContextProtocol.Core load
foreach (var t in new[] { typeof(McpServerToolAttribute), typeof(McpServerPromptAttribute) })
{
    foreach (var c in t.GetConstructors())
        Console.WriteLine($"ctor: ({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"prop: {p.PropertyType.Name} {p.Name}");
}
// find any type by name: core.GetTypes().First(x => x.Name == "CallToolResult")
EOF
dotnet run
```

For behavior (result conversion, error handling, name derivation), read the v2.0.0 sources:

- `src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs` — result→`CallToolResult`
  conversion switch, `DeriveName` (snake_case), `CreateToolCallErrorResult` behavior lives in McpServerImpl.
- `src/ModelContextProtocol.Core/Server/McpServerImpl.cs` — `CreateToolCallErrorResult` (message surfaced ONLY for `McpException`), tool-invocation catch path.
- `src/ModelContextProtocol/McpServerBuilderExtensions.cs` — `WithTools<T>`/`WithPrompts<T>` →
  `CreateTarget` = `ActivatorUtilities.CreateInstance` (per-invocation ctor injection);
  `WithToolsFromAssembly` requires `[McpServerToolType]`.
- `src/ModelContextProtocol.Core/Server/AIFunctionMcpServerPrompt.cs` — prompt options derivation, return-type constraint.

## Verified behaviors

- **Tool name**: `DeriveName` strips `Async` suffix, replaces non-ASCII-alnum with `_`, then
  `JsonNamingPolicy.SnakeCaseLower`. `[McpServerTool(Name=…)]` wins.
- **Result conversion** (`AIFunctionMcpServerTool.InvokeAsync` switch, runtime type):
  `AIContent` → content block (ErrorContent ⇒ IsError); `null` → empty content; `string` →
  `TextContentBlock`; `ContentBlock` → single; `IEnumerable<AIContent>`/`IEnumerable<ContentBlock>`;
  `CallToolResult` → **pass-through as-is**; anything else → JSON-serialized `TextContentBlock`.
  `StructuredContent` is only emitted when the tool has an output schema (set via
  `UseStructuredContent=true` + `OutputSchemaType`).
- **Thrown exceptions** → `CallToolResult { IsError = true }` with text
  `"An error occurred invoking '<name>': <msg>"` for `McpException`, else
  `"An error occurred invoking '<name>'."` (message dropped).
- **Prompt return constraint**: `string` | `PromptMessage` | `IEnumerable<PromptMessage>` |
  `ChatMessage` | `IEnumerable<ChatMessage>`; anything else → `InvalidOperationException`.
- **Param binding**: params that are DI services (`IServiceProviderIsService.IsService`) or
  `[FromKeyedServices]` are excluded from the schema and bound from DI; `CancellationToken` bound automatically; `[Description]` on params feeds the JSON schema; serializer defaults are
  `JsonSerializerDefaults.Web` (camelCase property names).
- **Ctor injection**: `WithTools<T>()`/`WithPrompts<T>()` (no-target overload) create the target per invocation via `ActivatorUtilities.CreateInstance(services, type)`.

## Applied example — the project "agent memory server" Wave 3 (repo state snapshot)

Repo: `~/RiderProjects/ai-raccon/the project`, worktree `.ai-badger/worktrees/agent-memory-server`, branch `task/agent-memory-server`. `TreatWarningsAsErrors`, `Directory.Packages.props` central versions,
`InternalsVisibleTo the project.Tests` per project. Store integration tests are NOT run against real sqlite-memory extensions — they assert SQL strings/pure logic (so new port methods are tested at the SQL-string + fake level only).

- **Port-gap analysis** (spec `docs/features/agent-memory/spec-issue-1.md` §4.1, 17 tools):
  `IMemoryStore` (Core) covers write/search/share/delete/delete-context/stats only. Tools needing NEW port methods (TDD) or documented stubs: `memory_list`, `memory_ingest_file`,
  `memory_ingest_directory`, `memory_configure`, `memory_embed_pending`, `memory_workspace_begin`,
  `memory_workspace_status`, `memory_workspace_consolidate`, `memory_workspace_discard`,
  `memory_sweep`. `DegradationPolicy` exists in Core but has no service surface → `memory_sweep`
  stubs unless a sweep service is added. Task brief claims NSubstitute is already a testing dep — **it is not** (`Directory.Packages.props` has no NSubstitute); existing tests use hand-written fakes (see
  `tests/the project.Tests/Domain/MemoryStorePortTests.cs` `RecordingStore`).
- **Tool error codes** (spec §7): `invalid-params: project_id is required`, `workspace-not-found`,
  `embedding-api-key-missing`, `sync-not-configured` — surface via `McpException` throws or
  `CallToolResult { IsError = true }` returns (message-drop trap above).
- **Dual-transport Program.cs**: `McpTransportSelector.UseHttp(MCP_TRANSPORT)` unchanged; both branches call a shared `ConfigureServices(IServiceCollection)`; register
  `InfrastructureOptions` (from `AIRACCON_DATA_ROOT`/`AIRACCON_INSTALL_SCOPE`),
  `SyncOptions` (`AIRACCON_SQLITECLOUD_DB_ID`/`_API_KEY`), `SqliteConnectionFactory`,
  `IMemoryStore → SqliteMemoryStore`, sync factory with `loadCloudSync: true` via factory lambda,
  `SyncService`; then `.WithTools<MemoryTools>().WithPrompts<MemoryPrompts>()`.
