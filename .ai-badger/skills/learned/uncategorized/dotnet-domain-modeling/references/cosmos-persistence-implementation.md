# Cosmos Persistence Implementation Pattern

When adding a new Cosmos-backed entity to this project, follow this exact multi-step sequence. Skipping any step causes test or build failures.

## Step-by-Step Checklist

### 1. Domain Layer (pure C#, no Azure deps)

Ensure these exist under `src/JobSearchAiAssistant.Domain/<Feature>/`:
- **Entity** — `sealed record` with `required string Id`, `required string UserId` (partition key), `required DateTimeOffset CreatedAt`, and domain behavior methods
- **Repository interface** — `I<Feature>Repository` with `CancellationToken ct` as last param, `string userId` for partition scoping, `IReadOnlyList<T>` for collections, nullable return for single-entity lookups
- **Enum/value objects** — any status enums, classification records, etc.

### 2. Contract Test Suite (TDD — write FIRST)

File: `tests/JobSearchAiAssistant.Infrastructure.Tests/Contracts/<Feature>RepositoryContract.cs`

```csharp
public abstract class <Feature>RepositoryContract
{
    private static readonly DateTimeOffset SomeInstant = new(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);

    protected abstract Task<I<Feature>Repository> CreateSubjectAsync();

    private static string NewUserId() => $"user-{Guid.CreateVersion7():N}";

    private static <Entity> New<Entity>(string userId, ...) => new() { ... };

    [Fact] public async Task Get_missing_returns_null() { ... }
    [Fact] public async Task Upsert_then_get_round_trips() { ... }
    [Fact] public async Task Get_by_<field>_returns_matching() { ... }
    [Fact] public async Task Data_saved_for_one_user_is_invisible_to_another() { ... }
    // Add feature-specific tests: GetPending, filters, ordering, etc.
}
```

**Conventions:**
- Use `Guid.CreateVersion7().ToString()` for IDs (matches ADR-0004)
- Use `TestContext.Current.CancellationToken` (xUnit v3)
- Use Shouldly assertions
- Test user isolation: GetById for other user returns null, query for other user returns empty
- For ordering tests: scrambled insertion order, verify final order matches expected sort

### 3. InMemory Fake

File: `tests/JobSearchAiAssistant.Testing/Fakes/InMemory<Feature>Repository.cs`

```csharp
public sealed class InMemory<Feature>Repository : I<Feature>Repository
{
    private readonly ConcurrentDictionary<(string UserId, string Id), <Entity>> _byKey = new();

    // GetByIdAsync: _byKey.GetValueOrDefault((userId, id))
    // QueryAsync: filter → OrderBy(CreatedAt).ThenBy(Id, StringComparer.Ordinal)
    // UpsertAsync: _byKey[(entity.UserId, entity.Id)] = entity; return entity;
}
```

**Key:** Ordering must match Cosmos implementation: `CreatedAt` ascending, then `Id` ascending with `StringComparer.Ordinal`.

### 4. Wire Contract into InMemoryContractTests

File: `tests/JobSearchAiAssistant.Infrastructure.Tests/InMemory/InMemoryContractTests.cs`

Add:
```csharp
public sealed class InMemory<Feature>RepositoryContractTests : <Feature>RepositoryContract
{
    protected override Task<I<Feature>Repository> CreateSubjectAsync() =>
        Task.FromResult<I<Feature>Repository>(new InMemory<Feature>Repository());
}
```

Add `using JobSearchAiAssistant.Domain.<Feature>;` to the imports.

### 5. CosmosOptions Property

File: `src/JobSearchAiAssistant.Infrastructure/Cosmos/CosmosOptions.cs`

Add property following the existing pattern:
```csharp
public string <Feature>Container { get; init; } = "<camelCaseName>";
```

### 6. Cosmos Repository Implementation

File: `src/JobSearchAiAssistant.Infrastructure/Cosmos/Cosmos<Feature>Repository.cs`

Pattern (follow `CosmosAdjustmentRuleRepository.cs`):

```csharp
public sealed class Cosmos<Feature>Repository(CosmosClient client, CosmosOptions options) : I<Feature>Repository
{
    private Container Container => client.GetContainer(options.DatabaseName, options.<Feature>Container);

    // Point reads: ReadItemAsync<T>(id, new PartitionKey(userId))
    //   catch CosmosException when StatusCode == NotFound → return null
    // Queries: GetItemLinqQueryable<T>(requestOptions: new QueryRequestOptions { PartitionKey = new(...) })
    //   → .Where(...).ToFeedIterator() → while HasMoreResults → ReadNextAsync → AddRange
    //   → Client-side re-sort: .OrderBy(CreatedAt).ThenBy(Id, CosmosQueryOrdering.Ordinal)
    // Upserts: UpsertItemAsync(entity, new PartitionKey(entity.UserId))
}
```

**Key patterns:**
- All queries partitioned by userId
- Client-side sort is authoritative (Cosmos offset-string ordering is unreliable)
- `CosmosQueryOrdering.Ordinal` for string tie-breaking
- Use LINQ expressions in `Where()` for Cosmos SDK translation

### 7. DI Registration

File: `src/JobSearchAiAssistant.Infrastructure/InfrastructureDependencies.cs`

Add after existing repository registrations:
```csharp
using JobSearchAiAssistant.Domain.<Feature>;  // if not already imported
services.AddSingleton<I<Feature>Repository, Cosmos<Feature>Repository>();
```

### 8. Terraform Container

File: `infra/cosmos.tf`

Add to `local.cosmos_containers` map:
```hcl
<camelCaseName> = ["/largeField1/*", "/largeField2/?"]
```

**Excluded paths:** List large embedded collections/strings that are NOT query predicates:
- `/*` for embedded object collections (e.g., `/classification/*`)
- `/?` for large string fields (e.g., `/rawExcerpt/?`, `/redactedBody/?`)

### 9. ProvisionCosmosEmulator Container List ← PITFALL

File: `src/tools/ProvisionCosmosEmulator/CosmosContainers.cs`

**You MUST add the new container name to `CosmosContainers.Names`.** If you skip this, the test `ProvisionerContainers_MatchTerraformCosmosContainers` will fail with:

```
Shouldly.ShouldAssertException : missing should be empty but had 1 item and was ["<newContainer>"]
Additional Info: ProvisionCosmosEmulator does not create: <newContainer>.
```

This is the most commonly forgotten step — the Terraform change and the provisioner list must stay in sync.

## Build & Test

```bash
dotnet build                                              # 0 errors
dotnet test --filter "RequiresInfra!=true"                # all pass
dotnet test --filter "FullyQualifiedName~<Feature>"       # contract tests pass
```

## Encrypted Document Sub-Pattern (ADR-0012)

When an entity contains sensitive data (API keys, compensation, credentials), encrypt the entire document before persisting. The domain entity stays clean; encryption is transparent in the repository.

### Cosmos Document Wrapper

```csharp
internal sealed record CosmosEncrypted<Feature>Document
{
    public required string Id { get; init; }           // application/entity id
    public required string UserId { get; init; }       // partition key
    public required EncryptedSecret EncryptedPayload { get; init; }
}
```

Mark `internal` — the Infrastructure project has `InternalsVisibleTo` for tests.

### Repository Constructor

```csharp
public sealed class CosmosEncrypted<Feature>Repository(
    CosmosClient client, CosmosOptions options, ISecretCipher secretCipher)
    : I<Feature>Repository
```

### Save Path
```csharp
var json = JsonSerializer.Serialize(entity, JsonOptions);
var encrypted = await secretCipher.EncryptAsync(json, userId, ct);
var doc = new CosmosEncrypted<Feature>Document { Id = entityId, UserId = userId, EncryptedPayload = encrypted };
await Container.UpsertItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
```

### Read Path
```csharp
var response = await Container.ReadItemAsync<CosmosEncrypted<Feature>Document>(id, new PartitionKey(userId), ...);
var json = await secretCipher.DecryptAsync(response.Resource.EncryptedPayload, userId, ct);
var entity = JsonSerializer.Deserialize<Entity>(json, JsonOptions)!;
```

### Encryption Test (Cosmos Emulator)

```csharp
[Collection(CosmosEmulatorCollection.Name)]
[Trait("Category", "CosmosEmulator")]
[RequiresInfra]
public class <Feature>EncryptionTests(CosmosEmulatorFixture fixture)
{
    private static ISecretCipher CreateCipher()
    {
        var provider = DataProtectionProvider.Create("TestScope");
        return new DataProtectionSecretCipher(provider);
    }

    [Fact]
    public async Task Entity_is_stored_encrypted_in_Cosmos()
    {
        var repo = new CosmosEncrypted<Feature>Repository(fixture.RequireClient(), fixture.Options, CreateCipher());
        await repo.SaveAsync(userId, entityId, entity, null, ct);

        // Read raw document — bypass decrypt
        var container = client.GetContainer(options.DatabaseName, options.<Feature>Container);
        var raw = await container.ReadItemAsync<CosmosEncrypted<Feature>Document>(entityId, new PartitionKey(userId), ...);

        // Ciphertext must not contain plaintext markers
        raw.Resource.EncryptedPayload.Ciphertext.ShouldNotContain(entity.SomeField.ToString());
        raw.Resource.EncryptedPayload.Algorithm.ShouldBe(EncryptedSecretAlgorithms.AspNetCoreDataProtection);
    }
}
```

**Key:** For test-only ISecretCipher instances, use ephemeral `DataProtectionProvider.Create("scope")` — no blob/key-vault setup needed. The encrypt/decrypt round-trips within the test process.

## Optimistic Concurrency Sub-Pattern (VersionedDocument)

When the repository contract uses `VersionedDocument<T>` (entity + ETag) instead of plain entity returns, the Cosmos implementation must use CreateItemAsync/ReplaceItemAsync with ETag guards.

### Contract Shape

```csharp
Task<VersionedDocument<T>?> GetAsync(string userId, string entityId, CancellationToken ct);
Task<string> SaveAsync(string userId, string entityId, T entity, string? etag, CancellationToken ct);
```

- `etag=null` on Save → **create** (CreateItemAsync). Conflict (409) → throw `ConcurrencyConflictException(id, "<new>")`.
- `etag` non-null on Save → **replace** (ReplaceItemAsync with `IfMatchEtag`). PreconditionFailed/NotFound → throw `ConcurrencyConflictException(id, etag)`.
- Returns the new ETag from Cosmos response.

### Cosmos Implementation

```csharp
public async Task<string> SaveAsync(string userId, string entityId, T entity, string? etag, CancellationToken ct)
{
    var partitionKey = new PartitionKey(userId);

    if (etag is null)
    {
        try
        {
            var response = await Container.CreateItemAsync(entity, partitionKey, cancellationToken: ct);
            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ConcurrencyConflictException(entityId, "<new>");
        }
    }

    try
    {
        var response = await Container.ReplaceItemAsync(entity, entityId, partitionKey,
            new ItemRequestOptions { IfMatchEtag = etag }, ct);
        return response.ETag;
    }
    catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
    {
        throw new ConcurrencyConflictException(entityId, etag);
    }
}
```

### Contract Test Pattern

Following `UserConfigurationRepositoryContract.cs`:
- `Get_returns_null_when_unconfigured`
- `Save_then_get_round_trips_with_the_returned_etag`
- `Save_with_null_etag_on_already_exists_throws_ConcurrencyConflictException`
- `Save_with_stale_etag_throws_ConcurrencyConflictException`
- `Save_with_current_etag_updates_and_returns_fresh_etag`
- Plus user-isolation and feature-specific tests

## Simple Config Document Sub-Pattern

When the entity is a single per-user configuration document (no separate Id field — userId IS the id and partition key), use the simplest possible Cosmos pattern:

```csharp
public sealed class CosmosMonitoringConfigRepository(CosmosClient client, CosmosOptions options) : IMonitoringConfigRepository
{
    private Container Container => client.GetContainer(options.DatabaseName, options.MonitoringConfigsContainer);

    public async Task<MonitoringConfig?> GetAsync(string userId, CancellationToken ct)
    {
        try
        {
            var response = await Container.ReadItemAsync<MonitoringConfig>(userId, new PartitionKey(userId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveAsync(MonitoringConfig config, CancellationToken ct)
    {
        await Container.UpsertItemAsync(config, new PartitionKey(config.UserId), cancellationToken: ct);
    }
}
```

**When to use:** The domain entity has no separate `Id` — the `UserId` serves as both the document id and partition key. There's exactly one document per user. No ordering, no listing, no filtering — just get-or-null and save.

**Conventions:**
- Use `ReadItemAsync` with `userId` as both the item id and partition key
- Use `UpsertItemAsync` for save (idempotent create-or-replace)
- No ETag concurrency needed — last-write-wins is acceptable for single-user config
- Terraform container entry: `monitoringConfigs = []` (no excluded paths for small documents)

## Wildcard-ETag Upsert Sub-Pattern

When the repository contract uses `UpsertAsync(entity, etag)` where `etag` can be `"*"` (blind upsert) or a real ETag (conditional replace), use `UpsertItemAsync` with optional `IfMatchEtag`:

```csharp
public async Task UpsertAsync(ProfileUpdateProposal proposal, string etag, CancellationToken ct)
{
    var options = etag == "*"
        ? null
        : new ItemRequestOptions { IfMatchEtag = etag };

    try
    {
        await Container.UpsertItemAsync(proposal, new PartitionKey(proposal.UserId), requestOptions: options, cancellationToken: ct);
    }
    catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
    {
        throw new ConcurrencyConflictException(proposal.Id, etag);
    }
}
```

**When to use:** The caller needs both blind upserts (first save, no existing document) and optimistic concurrency (subsequent saves with a known ETag). The `"*"` sentinel means "don't check ETag."

**Key distinctions from VersionedDocument pattern:**
- Uses `UpsertItemAsync` (not Create/Replace) — single code path for insert and update
- ETag is a plain `string` parameter, not wrapped in `VersionedDocument<T>`
- Catch both `PreconditionFailed` (stale ETag) and `Conflict` (duplicate create race)
- The repository does NOT return the new ETag — caller must re-read if it needs the fresh ETag for a subsequent update

### Contract Test Pattern

```csharp
[Fact] public async Task Upsert_with_wildcard_creates_new_document() { ... }
[Fact] public async Task Upsert_with_wildcard_updates_existing_document() { ... }
[Fact] public async Task Upsert_with_matching_etag_succeeds() { ... }
[Fact] public async Task Upsert_with_stale_etag_throws_ConcurrencyConflictException() { ... }
```

## DI Registration for Options and Extension Points

Beyond repository registrations, the Infrastructure DI setup also registers:

### Options Records
```csharp
// Options with no configuration section — direct instantiation with defaults
services.AddSingleton(new ChannelMonitoringOptions());

// Options bound from configuration (existing pattern)
var cosmosOptions = configuration.GetSection(CosmosOptions.Section).Get<CosmosOptions>() ?? ThrowHelper.Throw...;
services.AddSingleton(cosmosOptions);
```

### Extension-Point Implementations
```csharp
// Register the primary MVP implementation for an extension-point interface
services.AddSingleton<IProfileSink, ExportSink>();

// Guard optional dependencies — only register when the transport is available
if (services.Any(d => d.ServiceType == typeof(IGmailTransport)))
{
    services.AddSingleton<IChannelMonitor, LinkedInChannelMonitor>();
}
```

**When to use `new OptionsRecord()` vs config binding:**
- Config section exists and is required → `configuration.GetSection(...).Get<T>() ?? ThrowHelper.Throw...`
- Config section is optional with sensible defaults → `configuration.GetSection(...).Get<T>() ?? new T()`
- No config section needed (pure tuning knobs) → `new ChannelMonitoringOptions()`

## Global Container Sub-Pattern (Non-userId Partition Key)

When a container's data is **not per-user** (e.g., a global allowlist, a shared lookup table), the partition key differs from the standard `/userId` pattern. This means it **cannot** be added to the `local.cosmos_containers` map in `cosmos.tf` — that map hardcodes `partition_key_paths = ["/userId"]` via the `for_each` resource.

### Terraform: Separate Resource

```hcl
# In infra/cosmos.tf — OUTSIDE the for_each block
resource "azurerm_cosmosdb_sql_container" "beta_allowlist" {
  name                = "betaAllowlist"
  resource_group_name = azurerm_resource_group.main.name
  account_name        = azurerm_cosmosdb_account.main.name
  database_name       = azurerm_cosmosdb_sql_database.main.name
  partition_key_paths = ["/id"]  # NOT /userId

  indexing_policy {
    included_path { path = "/*" }
    excluded_path { path = "/_etag/?" }
  }
}
```

### App Settings: Reference the Separate Resource

```hcl
# In infra/functions.tf
Cosmos__BetaAllowlistContainer = azurerm_cosmosdb_sql_container.beta_allowlist.name
```

### Cosmos Repository: Partition Key = Id

```csharp
// Point read: id and partition key are the same
var response = await Container.ReadItemAsync<T>(id, new PartitionKey(id), cancellationToken: ct);

// Upsert: partition key is the document's id
await Container.UpsertItemAsync(entry, new PartitionKey(entry.Id), cancellationToken: ct);
```

### Pitfall

| Pitfall | Fix |
|---|---|
| Adding a non-userId container to `local.cosmos_containers` map | The `for_each` resource hardcodes `/userId` partition key. Create a separate `azurerm_cosmosdb_sql_container` resource with the correct partition key. |
| ProvisionCosmosEmulator doesn't know about the separate resource | Still add the container name to `CosmosContainers.Names` — the emulator provisioner doesn't care about partition keys, only container names. |

## DI Audit and Remediation

When a runtime `InvalidOperationException: Unable to resolve service for type 'IFoo'` occurs, or when systematically checking for missing registrations across all Function/Activity classes:

### Audit Methodology

1. **Extract all constructor parameters** from every `*Function` and `*Activity` class (grep for `class.*Function\(` and `class.*Activity\(`)
2. **Collect all interface types** from those constructors (filter for `I`-prefixed types)
3. **Cross-reference against** `InfrastructureDependencies.cs` and `ApiDependencies.cs` registrations
4. **Grep for each unregistered interface** to confirm no implementation exists anywhere
5. **Check if the interface is in Domain or Infrastructure** — determines where the implementation goes

### Stub Pattern for Missing Implementations

When an interface has no implementation yet (e.g., `IDmaApiClient` for a future API integration), create a stub that throws a domain exception. This unblocks DI resolution without implementing the real adapter:

```csharp
// src/JobSearchAiAssistant.Infrastructure/LinkedIn/StubDmaApiClient.cs
using JobSearchAiAssistant.Domain.LinkedIn;

namespace JobSearchAiAssistant.Infrastructure.LinkedIn;

/// <summary>
/// Placeholder IDmaApiClient until the real DMA Snapshot API adapter is implemented.
/// Every call throws LinkedInApiUnavailableException so callers get a clear
/// "not yet available" signal rather than a DI resolution failure at startup.
/// </summary>
public sealed class StubDmaApiClient : IDmaApiClient
{
    public Task ValidateTokenAsync(string accessToken, CancellationToken ct)
    {
        throw new LinkedInApiUnavailableException();
    }

    public Task<IReadOnlyList<LinkedInSnapshotRecord>> FetchSnapshotAsync(string accessToken, CancellationToken ct)
    {
        throw new LinkedInApiUnavailableException();
    }
}
```

Register alongside real implementations:
```csharp
services.AddSingleton<ILinkedInConnectionRepository, CosmosLinkedInConnectionRepository>();
services.AddSingleton<IDmaApiClient, StubDmaApiClient>();  // stub until issue #184
```

### Using Directive Pitfall

When adding a registration for an interface in a namespace not already imported in `InfrastructureDependencies.cs`, the `using` directive must be added. Common miss: `Domain.LinkedIn` is a different namespace from `Domain.LinkedInProfileUpdate` — both exist and both are needed.

**Detection:** `CS0246: The type or namespace name 'IFoo' could not be found`

**Fix:** Add the missing `using` to the top of `InfrastructureDependencies.cs`. Verify the namespace is correct by checking the interface's file location.

## Common Pitfalls

| Pitfall | Fix |
|---|---|
| Forgot to update `CosmosContainers.Names` | Add the container name; the contract test catches this |
| Client-side sort missing | Always re-sort after draining Cosmos iterator |
| No user isolation test | Every contract suite must test cross-user visibility |
| `IEnumerable<T>` instead of `IReadOnlyList<T>` in interface | Use `IReadOnlyList<T>` for async repository methods |
| Missing `CancellationToken` | Every async method must accept `CancellationToken ct` as last param |
| Query not partitioned | Always set `PartitionKey` in `QueryRequestOptions` |
| `ShouldNotContain(string, string)` on Shouldly strings | The `(string, string)` overload doesn't exist — second arg matches `Expression<Func<char, bool>>`. Use `ShouldNotContain(marker)` without custom message |
| Encrypted doc test needs `ISecretCipher` but CosmosEmulatorFixture has none | Create ephemeral cipher: `DataProtectionProvider.Create("scope")` + `new DataProtectionSecretCipher(provider)` — no blob/key-vault setup |
| `CosmosCompensationProfileDocument` is `internal` but tests need it | Infrastructure.csproj has `InternalsVisibleTo` for test project — `internal` is fine |
