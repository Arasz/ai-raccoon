# HTTP Endpoint Testing Patterns (Azure Functions Isolated Worker)

## Test Harness Setup

Standard pattern for testing Azure Functions HTTP endpoints directly (no HTTP server, no WebApplicationFactory):

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using NSubstitute;

public class MyFunctionsTests
{
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";

    // 1. Fake FunctionContext with UserId (auth middleware simulation)
    private static FunctionContext NewExecutionContext(string userId = UserId)
    {
        var context = Substitute.For<FunctionContext>();
        context.Items.Returns(new Dictionary<object, object> { ["UserId"] = userId });
        return context;
    }

    // 2. DefaultHttpContext with optional JSON body
    private static DefaultHttpContext NewHttpContext(object? body = null)
    {
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }  // ← CRITICAL: NullStream by default
        };
        if (body is { })
        {
            var json = JsonSerializer.Serialize(body, ApiJsonOptions.Pinned);
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            httpContext.Request.ContentType = "application/json";
        }
        return httpContext;
    }

    // 3. Read response body as JsonElement
    private static async Task<JsonElement> ReadBodyAsync(DefaultHttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;  // ← CRITICAL: reset position
        using var reader = new StreamReader(httpContext.Response.Body);
        var json = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json).RootElement;
    }
}
```

## Calling Functions Directly

```csharp
[Fact]
public async Task MyEndpoint_returns_200_with_expected_data()
{
    var repository = new InMemoryMyRepository();
    var sut = NewSut(repository);
    var httpContext = NewHttpContext(new { field = "value" });

    var response = await sut.MyEndpoint(
        httpContext.Request, routeParam, NewExecutionContext(), TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    var body = await ReadBodyAsync(httpContext);
    body.GetProperty("field").GetString().ShouldBe("value");
}
```

## camelCase Enum Serialization Pitfall

When `JsonStringEnumConverter` is configured with a camelCase naming policy:

```csharp
// In ApiJsonOptions or WorkerOptions.Serializer:
options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
```

**All enum values serialize as camelCase (first letter lowercase):**

| C# Enum Value | JSON String |
|---|---|
| `TakeHomeOutcome.Computed` | `"computed"` |
| `TakeHomeOutcome.UnsupportedTaxYear` | `"unsupportedTaxYear"` |
| `RecommendationSource.Deterministic` | `"deterministic"` |
| `CompensationContractType.Employment` | `"employment"` |
| `CaseState.Ready` | `"ready"` |

**In tests, assert lowercase:**

```csharp
// ❌ WRONG — fails with single-char case diff
body.GetProperty("outcome").GetString().ShouldBe("Computed");

// ✅ CORRECT
body.GetProperty("outcome").GetString().ShouldBe("computed");
```

**Detection:** If 5+ tests fail simultaneously showing single-character case differences (C vs c), this is the cause. Fix is mechanical: find-replace all PascalCase enum assertions to camelCase.

## userId-Scoping Test Pattern

Always verify that endpoints are scoped to the authenticated user:

```csharp
[Fact]
public async Task Endpoint_throws_ResourceNotFoundException_for_other_user()
{
    var repository = await SeedDataAsync();
    var sut = NewSut(repository);
    var httpContext = NewHttpContext();

    await Should.ThrowAsync<ResourceNotFoundException>(() =>
        sut.MyEndpoint(httpContext.Request, id, NewExecutionContext(OtherUserId), TestContext.Current.CancellationToken));
}
```

## In-Memory Repository Pattern

For repositories with optimistic concurrency:

```csharp
public sealed class InMemoryMyRepository : IMyRepository
{
    private readonly ConcurrentDictionary<(string UserId, string Id), (MyEntity Doc, string ETag)> _byKey = new();
    private readonly Lock _lock = new();

    public Task<VersionedDocument<MyEntity>?> GetAsync(string userId, string id, CancellationToken ct)
    {
        if (_byKey.TryGetValue((userId, id), out var entry))
            return Task.FromResult<VersionedDocument<MyEntity>?>(new(entry.Doc, entry.ETag));
        return Task.FromResult<VersionedDocument<MyEntity>?>(null);
    }

    public Task<string> SaveAsync(string userId, string id, MyEntity entity, string? etag, CancellationToken ct)
    {
        lock (_lock)
        {
            var key = (userId, id);
            var exists = _byKey.TryGetValue(key, out var current);
            if (etag is null && exists)
                throw new ConcurrencyConflictException(id, "<new>");
            if (etag is not null && (!exists || current.ETag != etag))
                throw new ConcurrencyConflictException(id, etag);
            var newETag = Guid.NewGuid().ToString();
            _byKey[key] = (entity, newETag);
            return Task.FromResult(newETag);
        }
    }
}
```

## Required NuGet Packages (Test Project)

```xml
<PackageReference Include="NSubstitute" />
<PackageReference Include="Shouldly" />
<PackageReference Include="xunit.v3" />
```

## Status Endpoint Testing (DurableTaskClient.GetInstanceAsync)

When testing `GET /status` endpoints that read orchestration metadata, inject `DurableTaskClient` via constructor (not `[DurableClient]` method attribute) for testability. Mock `GetInstanceAsync` with different `OrchestrationMetadata` states:

```csharp
// Constructor injection pattern for status endpoints:
public sealed partial class MonitoringStatusFunctions(
    DurableTaskClient durableTaskClient,
    ILogger<MonitoringStatusFunctions> logger)

// Test: running orchestration
var client = Substitute.For<DurableTaskClient>("test-client");
client.GetInstanceAsync(instanceId, Arg.Any<CancellationToken>()).Returns(
    Task.FromResult<OrchestrationMetadata?>(
        new OrchestrationMetadata("MonitorChannels", instanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Running
        }));

// Test: completed with serialized output
client.GetInstanceAsync(instanceId, Arg.Any<CancellationToken>()).Returns(
    Task.FromResult<OrchestrationMetadata?>(
        new OrchestrationMetadata("MonitorChannels", instanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Completed,
            CompletedAt = completedAt,
            SerializedOutput = """{"checkedCaseIds":["case-1","case-2"]}"""
        }));

// Test: no orchestration ever run
client.GetInstanceAsync(instanceId, Arg.Any<CancellationToken>()).Returns(
    Task.FromResult<OrchestrationMetadata?>(null));
```

Test all three states: running, completed (with output deserialization), and null (never run).

## PUT Round-Trip with FluentValidation

When testing PUT endpoints that deserialize + validate + save + return:

```csharp
private static DefaultHttpContext NewHttpContextWithJsonBody(string json)
{
    var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    var bytes = Encoding.UTF8.GetBytes(json);
    context.Request.Body = new MemoryStream(bytes);
    context.Request.ContentType = "application/json";
    return context;
}

// Happy path: save and verify round-trip
[Fact]
public async Task PutConfig_saves_and_returns()
{
    var repo = new InMemoryMonitoringConfigRepository();
    var subject = NewSubject(repo);
    var httpContext = NewHttpContextWithJsonBody("""{"activeChannels":["gmail"],"schedule":"0 */30 * * * *"}""");

    var response = await subject.PutConfig(httpContext.Request, NewFunctionContext(), ct);

    response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    var body = await ReadBodyAsync(httpContext);
    body.GetProperty("activeChannels")[0].GetString().ShouldBe("gmail");

    // Verify persistence
    var saved = await repo.GetAsync(UserId, ct);
    saved.ShouldNotBeNull();
}

// Validation failure: throws FluentValidation.ValidationException
[Fact]
public async Task PutConfig_validates_threshold_range()
{
    var httpContext = NewHttpContextWithJsonBody("""{"autoApplyConfidenceThreshold":1.1}""");
    await Should.ThrowAsync<ValidationException>(() =>
        subject.PutConfig(httpContext.Request, NewFunctionContext(), ct));
}
```

Key pattern: use raw JSON strings (not anonymous objects) for `NewHttpContextWithJsonBody` to control exact field presence/absence including `null` values.

## Optional Body Parameters

Azure Functions methods can accept optional request bodies:

```csharp
[Function(nameof(ConfirmProposal))]
public async Task<HttpResponse> ConfirmProposal(
    [HttpTrigger(...)] HttpRequest req,
    string signalId,
    FunctionContext executionContext,
    CancellationToken cancellationToken,
    ConfirmProposalRequest? body = null)  // optional, defaults to null
```

In tests, pass `null` (or omit) for no-body requests, and pass the DTO for body requests. The `RequestBodyReader.DeserializeAsync<T>` returns null for empty bodies, which maps to the default.

## Middleware Testing (AuditMiddleware, PrincipalResolutionMiddleware, etc.)

When testing `IFunctionsWorkerMiddleware` that calls `context.GetHttpContext()`, the mock
`FunctionContext` needs the `HttpContext` in its `Items` dictionary under `"HttpRequestContext"`.
NSubstitute cannot intercept extension methods — see pitfall #28 in `refactoring-fix`.

```csharp
// HTTP trigger context — GetHttpContext() returns the HttpContext
context.Items.Returns(new Dictionary<object, object>
{
    ["UserId"] = userId,
    ["HttpRequestContext"] = httpContext
});

// Non-HTTP context — GetHttpContext() returns null (key absent)
context.Items.Returns(new Dictionary<object, object>());
```

Binding metadata for `IsHttpTrigger()`:
```csharp
var binding = Substitute.For<BindingMetadata>();
binding.Type.Returns("httpTrigger");
binding.Name.Returns("req");
var bindings = ImmutableDictionary<string, BindingMetadata>.Empty.Add("req", binding);
var definition = Substitute.For<FunctionDefinition>();
definition.InputBindings.Returns(bindings);
definition.Name.Returns("MyFunction");
context.FunctionDefinition.Returns(definition);
```

## Common Pitfalls

| Pitfall | Fix |
| --- | --- |
|---|---|
| Response body is empty | `DefaultHttpContext.Response.Body` defaults to `NullStream`; set to `new MemoryStream()` |
| Can't read response body | Reset `Position = 0` before reading |
| Enum assertion case mismatch | Use camelCase string values (see table above) |
| Missing CancellationToken in tests | Use `TestContext.Current.CancellationToken` (xunit v3) |
| Wrong JSON options in request body | Use `ApiJsonOptions.Pinned` (or project equivalent) for request serialization |
