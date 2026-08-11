---
name: ai-raccoon-new-service
description: >-
  Use when adding a new service or port to AiRaccoon.
version: 1.0.0
author: hermes-agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [ai-raccoon, dotnet, mcp, service-integration, di, testing]
    related_skills: [ai-raccoon-development, dotnet-mcp-server, test-driven-development]
---

# Adding a new service to AiRaccoon

Patterns for adding a new injectable service (port + adapter) without rippling
existing interfaces, fakes, or the schema version ladder.

## When to use

- Adding a new `IXxxService` port in Core + `SqliteXxxService` in Infrastructure
- Adding observability / quality-tracking alongside existing tools
- Adding a new MCP tool class (QualityTools, WatchTools, etc.)
- Adding new DDL tables to the bank schema

## Core principle: don't grow existing store ports for observability

`IMemoryStore` has 2 implementations + 15+ fake stores. Adding a method to it
ripples into every fake — measured at 18 test files. Instead, create a SEPARATE
service interface:

```csharp
// Core/SearchQuality/ISearchQualityService.cs
public interface ISearchQualityService
{
    Task RecordSearchAsync(...);
    Task RecordFollowThroughAsync(...);
    Task<SearchQualityMetrics> GetMetricsAsync(...);
}
```

The tool layer calls BOTH `store.SearchAsync` AND `quality.RecordSearchAsync`
alongside each other — the quality service never sits inside the store.

**This applies to any observability, telemetry, or quality concern.** If it
doesn't affect retrieval results, it doesn't belong in `IMemoryStore`.

## Design principle: signals collected together

When designing a quality/telemetry service, model it as a **single record that
accumulates multiple signals** keyed by a correlation-id, not as independent
writes. Example: search creates the record, follow-through updates it, grade
updates it. All three land on the same `search_quality` row.

The user enforced this explicitly (2026-08-11): "we want multiple signals,
correlation id and follow through must be collected together."

## Correlation-id at tool layer, not in Core records

`SearchQuery` is a Core retrieval parameter — adding an observability field
contaminates the port. Instead:

1. Generate `correlationId = Guid.NewGuid().ToString("N")` in `MemoryTools.Search()`
2. Pass to `ISearchQualityService.RecordSearchAsync` alongside the results
3. Surface in `ApiEnvelope.Meta.CorrelationId` (add as optional property on `PromotionMeta`)

The agent receives the correlation-id in the search response and passes it back
on follow-through and grade calls.

## Fire-and-forget for observability in tool methods

Observability calls MUST NOT block or fail the primary tool response:

```csharp
try
{
    await qualityService.RecordSearchAsync(correlationId, query, scope, projectId,
        null, results.Count, topSourceFiles, ct).ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.QualityRecordFailed(logger, ex);
}
```

Log at Warning level. The search response always returns regardless.

## Additive DDL for new tables

New tables go in `MemorySchema.Ddl` as `CREATE TABLE IF NOT EXISTS`. This runs
on every bank open via `MemorySchema.EnsureAsync`. No version bump needed —
`CurrentVersion` stays unchanged. Only ALTER TABLE or data migration needs
`MigrateAsync` and a version bump.

Precedent: `watches`, `watch_files`, `promotion_queue`, `search_quality` all
use this pattern. See ADR-0023, ADR-0025.

**SQLite gotcha**: UNIQUE on a nullable column treats NULLs as distinct. If the
column can be NULL, use an expression index with `COALESCE(col, '')`.

**Redundant index gotcha**: UNIQUE constraint creates an implicit index. Don't
add a separate `CREATE INDEX` on the same column — double write cost, zero
query benefit.

## New MCP tool class pattern

Don't append to `MemoryTools` — it maps 1:1 to `IMemoryStore`. Create a
separate tool class (QualityTools, WatchTools, PromotionTools, etc.):

```csharp
public sealed class QualityTools(
    ISearchQualityService qualityService,
    ToolGate gate,
    ToolCallMetrics observability)
{
    public const string TN_RecordFollowThrough = "memory_record_followthrough";

    [McpServerTool(Name = TN_RecordFollowThrough)]
    public async Task<ApiEnvelope<FollowThroughResult>> RecordFollowThrough(
        string projectId, string correlationId, string filePath,
        CancellationToken ct = default)
    {
        using var activity = new ToolExecutionActivity(observability, TN_RecordFollowThrough, projectId);
        try
        {
            RequireProjectId(projectId);
            await gate.RequireAsync(projectId, AccessRequirement.Write, TN_RecordFollowThrough, ct);
            await qualityService.RecordFollowThroughAsync(correlationId, filePath, ct);
            activity.RecordInvocation();
            return gate.WrapAsync(projectId, new FollowThroughResult(true));
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }
}
```

Register in `McpServerSetup.cs` — `.WithTools<QualityTools>()` in BOTH
`CreateAppHost` and `CreateWebHost` methods. Update `ToolInventoryTests`
(count + name). Update `README.md` Tools heading count.

**Every tool needs**:
- `TN_*` const (PascalCase after prefix)
- `ToolExecutionActivity` wrapper (mandatory since 2026-08-06)
- `AccessRequirement` tier (Write for data writes, Read for queries)
- `RequireProjectId` guard (unless the tool operates globally)
- Result record (`SettingResult`-style)

## DI registration pattern

```csharp
// In Dependencies.cs RegisterMemoryServices
services.AddSingleton<SqliteSearchQualityService>();
services.AddSingleton<ISearchQualityService>(sp => sp.GetRequiredService<SqliteSearchQualityService>());
```

Register concrete first, then interface → concrete resolution. Same pattern as
`SqliteMemoryStore` / `IMemoryStore`, `SqliteWorkspaceStore` / `IWorkspaceStore`.

## Integration test pattern

```csharp
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class MyServiceTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly MyService _sut;

    public MyServiceTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _sut = new MyService(_factory);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task EnsureSchemaAsync()
    {
        await using var conn = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await MemorySchema.EnsureAsync(conn, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MyTest()
    {
        await EnsureSchemaAsync();
        // ... use _sut ...
    }
}
```

Key points:
- `NullKeyProvider.Resolver(options)` for unencrypted test banks
- `MemorySchema.EnsureAsync(conn, ct)` to create schema (not `SqliteMemoryStore.EnsureAsync`)
- `TestContext.Current.CancellationToken` on every await (xunit v3 + TreatWarningsAsErrors)
- `IDisposable` for cleanup (not `IAsyncLifetime` unless you need async teardown)
- `TestData.CreateTempRoot()` for temp directories

## Pitfalls

- **ToolInventoryTests count drift**: the test pins an exact tool count literal.
  Every new tool requires updating it. Also update `README.md` Tools heading.
- **McpServerSetup has TWO host creation methods**: `.WithTools<T>()` must be
  added to BOTH `CreateAppHost` and `CreateWebHost`. Forgetting one means the
  tool works in stdio but not HTTP (or vice versa).
- **MemoryTools constructor ripple**: adding a parameter to MemoryTools ripples
  into ~3 test files that construct it directly. Use `null!` or a mock.
- **TreatWarningsAsErrors**: unused primary-ctor params in skeletons fire CS9113.
  Nullable reference warnings are errors (CS8604). Every `?` matters.
- **ToolTelemetryCoverageTests** and **McpServerSetupHostTests** may need
  updating for new tools — check both.
