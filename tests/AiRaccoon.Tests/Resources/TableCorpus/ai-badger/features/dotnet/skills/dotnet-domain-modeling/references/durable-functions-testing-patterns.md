# Testing .NET Durable Functions Pipelines

Assumes Azure Durable Functions.

Patterns for testing orchestrations, activities, HTTP-triggered functions, and LLM-calling generators in a Durable Functions codebase.

## Test Stack

- xUnit v3 (`Fact`, `Theory`, `InlineData`)
- Shouldly assertions
- NSubstitute for mocks
- `FakeLlmClient` / `FakeLlmClientFactory` for LLM-calling generator tests
- `InMemoryApplicationRepository` for function/orchestration tests with real optimistic concurrency
- `DefaultHttpContext` for HTTP response assertions

## 1. Generator Tests (LLM-calling classes)

Generators (e.g. `PracticeQuestionsGenerator`, `InterviewPrepGenerator`, `PracticeSessionScorer`, `PrepFeedbackSummaryGenerator`) call the LLM via `ILlmClientFactory` → `ILlmClient`.

### Setup Pattern

```csharp
private static (FakeLlmClient LlmClient, FakeLlmClientFactory LlmClientFactory, MyGenerator Generator) NewSut()
{
    var llmClient = new FakeLlmClient();
    var llmClientFactory = new FakeLlmClientFactory(llmClient);
    var generator = new MyGenerator(llmClientFactory, new DefaultPromptProvider());
    return (llmClient, llmClientFactory, generator);
}
```

### Assertions to Include

| Assertion | What it proves |
|---|---|
| `sentRequest.Tier.ShouldBe(ModelTier.Strong)` | Correct model tier |
| `sentRequest.JsonSchema.ShouldBe(MySchemas.X)` | Correct schema contract |
| `sentRequest.StepType.ShouldBe(MyStepTypes.X)` | Correct step type |
| `sentContent.ShouldContain(...)` on each content part label | All inputs reach the LLM |
| `sentRequest.ContentParts[i].ShouldContain(JsonSerializer.Serialize(input))` | Inputs are compact JSON |
| `llmClientFactory.RequestedUserIds.ShouldBe([UserId])` | UserId threading |
| `Should.ThrowAsync<LlmResponseValidationException>(...)` | Invalid LLM output propagates |

### Fixture Convention

Fixtures create the domain objects, then configure `FakeLlmClient` with a contract object matching what the LLM would return. Use `SetJsonResult(stepType, contract)` for success and `SetJsonFailure(stepType, exception)` for failure.

### Content Parts Convention

Each content part is labeled with `## Source N — description` and contains compact JSON. Tests verify:
1. The total count matches expectations
2. Each part contains the relevant input data
3. Optional parts (e.g. prior scores) are omitted when empty/null

## 2. HTTP Function Tests

Functions (e.g. `PracticeSessionFunctions`, `InterviewPrepFunctions`) are Azure Function HTTP triggers.

### Setup Pattern

```csharp
private static FunctionContext NewFunctionContext(string userId = UserId)
{
    var context = Substitute.For<FunctionContext>();
    context.Items.Returns(new Dictionary<object, object>());
    context.UserId = userId;
    return context;
}

private static DefaultHttpContext NewHttpContext() =>
    new() { Response = { Body = new MemoryStream() } };

private static DurableTaskClient NewDurableTaskClient(string instanceId = "instance-1")
{
    var client = Substitute.For<DurableTaskClient>("test-client");
    client.ScheduleNewOrchestrationInstanceAsync(
        Arg.Any<TaskName>(), Arg.Any<object?>(),
        Arg.Any<StartOrchestrationOptions?>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(instanceId));
    return client;
}
```

### Seed Helper

```csharp
private static async Task<(InMemoryApplicationRepository, Application)> SeedApplicationAsync(...)
{
    var repository = new InMemoryApplicationRepository();
    var application = Application.Create(...) with { ... };
    await repository.SaveAsync(application, null, ct);
    return (repository, application);
}
```

### Test Categories

| Category | Pattern |
|---|---|
| Happy path (2xx) | Assert status code + orchestration scheduling call |
| Unknown resource | Assert domain exception thrown with correct properties |
| Invalid state transition | Assert `InvalidXxxTransitionException` with `From`/`To` |
| Missing prerequisite | Assert specific exception type and ID properties |
| Concurrency guard | Setup `GetInstanceAsync` to return active instance, assert exception |

### Orchestration Scheduling Assertion

```csharp
await durableTaskClient.Received(1)
    .ScheduleNewOrchestrationInstanceAsync(
        Arg.Is<TaskName>(n => n.Name == nameof(MyOrchestration)),
        Arg.Is<object>(input => ((MyOrchestrationInput)input!).UserId == UserId),
        Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == expectedInstanceId),
        Arg.Any<CancellationToken>());
```

Instance ID format comes from `ApplicationOrchestrationInstanceIds.ForXxx(...)`.

## 3. Orchestration Tests

Orchestrations (e.g. `GeneratePracticeQuestionsOrchestration`, `ScorePracticeSessionOrchestration`) are the hardest to test — every `context.CallActivityAsync` must be stubbed.

### Context Setup

```csharp
private static TaskOrchestrationContext NewContext(TInput input)
{
    var context = Substitute.For<TaskOrchestrationContext>();
    context.GetInput<TInput>().Returns(input);
    context.CurrentUtcDateTime.Returns(Now);
    context.InstanceId.Returns("instance-1");
    context.NewGuid().Returns(guid1, guid2, ...); // sequence for IDs
    return context;
}
```

### Standard Activity Stubs

```csharp
// Load: returns VersionedDocument<Application>
private static void SetupLoad(ctx, app, etag = "etag-0") { ... }

// Save: captures each save, increments etag
private static List<Application> SetupSave(ctx) { ... }

// Interview prep inputs (offer analysis + CV)
private static void SetupInterviewPrepInputs(ctx, offer?, cv?) { ... }

// The step's own activity
private static void SetupGenerateSuccess(ctx, result) { ... }
```

### Test Scenarios (minimum)

| Scenario | What to assert |
|---|---|
| Happy path | Outcome fields, final application state, intervention cleared |
| Idempotent early return | No save/load calls for skipped scenarios |
| Failure → park | `Intervention.IsRequired == true`, `StepStatus.AwaitingUser` |
| Retry → complete | Activity called N+1 times, final state correct |
| Skip | No side effects, outcome `Generated/Prepared == false` |
| Outer catch | Intervention set, exception re-thrown |
| Max retries exceeded | Step ends `Failed`, not left `AwaitingUser` |

### Non-Step Orchestrations

Some orchestrations (scoring, feedback summary) don't use `ExecuteStepAsync` — they call activities directly and have a simpler try/catch + `TryMarkOrchestrationAsFailedAsync` pattern. For these:

| Scenario | What to assert |
|---|---|
| Happy path | Outcome, saved application state |
| Early return (wrong status) | No activity calls, no save |
| Failure | Intervention marked, exception re-thrown |

## 4. Exception Mapping Tests

Every new domain exception needs a test proving `DomainExceptionProblemMapper.Map(exception)` returns the correct `ProblemDetails`:

```csharp
[Fact]
public void MyException_maps_to_correct_status_code()
{
    var problem = DomainExceptionProblemMapper.Map(new MyException(...));
    problem.Status.ShouldBe(StatusCodes.Status409Conflict); // or 404
    problem.Title.ShouldBe("My Exception Title");
    problem.Extensions["id"].ShouldBe("expected-id");
}
```

Build one test class per feature grouping (e.g. `PracticeSessionExceptionMappingTests`) that covers all new exceptions in that feature.

## 5. Build & Run

```bash
# Build
dotnet build

# Run only unit tests (no infra)
dotnet test --filter "RequiresInfra!=true"

# Run tests for a specific feature
dotnet test --filter "FullyQualifiedName~PracticeSession"

# Full suite
dotnet test
```

## Pitfalls

| Pitfall | Fix |
|---|---|
| `FakeLlmClient` throws "no fixture configured" | Forgetting `SetJsonResult` — configure the fixture before exercising |
| `InMemoryApplicationRepository.SaveAsync` throws `ConcurrencyConflictException` | First save uses `etag: null`; subsequent saves must pass the returned etag |
| Orchestration test passes but wrong `context.NewGuid()` sequence | Use explicit Guid sequence: `context.NewGuid().Returns(guid1, guid2)` — the order matters for session IDs vs step IDs |
| `DefaultHttpContext.Response.Body` is null | Initialize with `Body = new MemoryStream()` in the helper |
| `FunctionContext.UserId` not set | Extension property sets `context.Items["UserId"]`; substitute needs `Items.Returns(new Dictionary<object, object>())` |
| Testing wrong orchestration instance ID | Use `ApplicationOrchestrationInstanceIds.ForXxx(...)` to derive expected ID |
| `ThrowsAsync` on abstract method returning `Task<T>` | NSubstitute can't intercept `.ThrowsAsync()` on the return value of an abstract method stub. Use `.Returns(Task.FromException<T>(exception))` instead |
| `Arg.Any<string>()` for `TaskName` parameter | `ScheduleNewOrchestrationInstanceAsync` takes `TaskName` (not `string`). Use `Arg.Any<TaskName>()`. The implicit conversion from `string` doesn't help NSubstitute matchers |
| `Arg.Any<object>()` for nullable parameter | When the method signature has `object?`, use `Arg.Any<object?>()` — NSubstitute respects nullable annotations and `Arg.Any<object>()` won't match `null` |
| `Arg.Is<T>(o => o?.Prop == val)` — CS8072 | Expression tree lambdas can't contain null-propagulating operators. Use `o != null && o.Prop == val` instead |
| `StartOrchestrationOptions` not found | It's in `Microsoft.DurableTask` (Abstractions), NOT `Microsoft.DurableTask.Client`. Add `using Microsoft.DurableTask;` alongside `using Microsoft.DurableTask.Client;` |
| `OrchestrationAlreadyExistsException` not found | It's in `DurableTask.Core.Exceptions`, not in `Microsoft.DurableTask.Client`. Add `using DurableTask.Core.Exceptions;` |
| CS8619/CS8604 on NSubstitute `.Returns()` with nullable args | When `Task.FromResult(callInfo.Arg<T>())` fails nullability because `T` is `T?`, use `Task.FromResult<T>(callInfo.Arg<T>()!)` — explicit generic type argument + null-forgiving operator |

## NSubstitute + DurableTaskClient Reference

### Making an abstract method throw

```csharp
// ❌ CS1061: 'Task<string>' does not contain a definition for 'ThrowsAsync'
client.ScheduleNewOrchestrationInstanceAsync(...)
    .ThrowsAsync(new OrchestrationAlreadyExistsException(id));

// ✅ Use Returns with Task.FromException
client.ScheduleNewOrchestrationInstanceAsync(
    Arg.Any<TaskName>(),
    Arg.Any<object?>(),
    Arg.Any<StartOrchestrationOptions?>(),
    Arg.Any<CancellationToken>())
    .Returns(Task.FromException<string>(new OrchestrationAlreadyExistsException(id)));
```

### Required usings for DurableTaskClient test doubles

```csharp
using DurableTask.Core.Exceptions;   // OrchestrationAlreadyExistsException
using Microsoft.DurableTask;          // StartOrchestrationOptions, TaskName
using Microsoft.DurableTask.Client;   // DurableTaskClient, OrchestrationRuntimeStatus
```

### C# 14 `extension` members in tests

When the project uses C# 14's `extension(Type receiver) { ... }` syntax for extension methods, they appear as instance methods on the receiver type in tests. Call them directly on the instance:

```csharp
// Extension defined as:
// extension(IApplicationRepository repo) { public Task<...> SaveWithConflictRetryAsync(...) { ... } }

// Called in tests as:
await repository.SaveWithConflictRetryAsync(document, mutation, ct);
// NOT as: ApplicationRepositoryExtensions.SaveWithConflictRetryAsync(repository, ...)
