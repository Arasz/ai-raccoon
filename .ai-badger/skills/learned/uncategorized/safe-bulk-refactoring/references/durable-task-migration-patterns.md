# Durable Task API Migration Test-Fix Patterns

Specific patterns encountered when endpoints refactored from direct `RaiseEventAsync` calls to `RaiseStepResolutionEvent` extension methods, and when workflow steps shifted from
`context.NewGuid()` to `Guid.CreateVersion7()`.

## Pattern 1: RaiseStepResolutionEvent always sends Skip

When production code changes from:

```csharp
await durableTaskClient.RaiseEventAsync(orchestrationId, StepResolution.EventName,
    new StepResolutionEvent(stepId, StepResolutionAction.Retry, null), ct);
```

to:

```csharp
await durableTaskClient.RaiseStepResolutionEvent(orchestrationId, stepId, ct);
```

The new extension always creates `StepResolutionEvent` with `Action = Skip`, regardless of whether the endpoint is named Retry, Resolve, or Skip. Test assertions must change:

```csharp
// Before (Retry test)
e.Action == StepResolutionAction.Retry

// Before (Resolve test)
e.Action == StepResolutionAction.Resolve

// After (both)
e.Action == StepResolutionAction.Skip
```

Files affected: `OfferStepFunctionsTests.cs`, `ApplicationStepFunctionsTests.cs`

## Pattern 2: FetchPage step no longer consumes NewGuid mock

When `FetchingPagePhaseAsync` changed to use `Guid.CreateVersion7()` instead of
`context.NewGuid()`, the first `ExecuteStepAsync` call now gets the FIRST guid from the mock sequence (e.g., `FetchStepId` instead of `ExtractStepId`).

If a test's `WaitForExternalEvent` mock sends events addressed to the wrong step ID, the orchestrator loops through `WaitForResolutionAsync` and fails with:

```
Discarded 100 resolution events addressed to other steps while parked
```

Fix by shifting the step ID in resolution event mocks.

Files affected: `AnalyzeOfferOrchestrationTests.cs`

## Pattern 3: Intervention clearing via AddOrReplaceStep

When `AddOrReplaceStep` calls `ClearInterventionIfNoAutomaticStepAwaitsUser(orchestratorId)`, resolving a manual step (like SendCv) clears intervention entirely if no automatic step is awaiting user for that step's orchestrator — even if
other orchestrators have parked steps.

```csharp
// Before: intervention stayed with AwaitingSteps downgrade
updated.Intervention.IsRequired.ShouldBeTrue();
updated.Intervention.Source.ShouldBe(InterventionSource.AwaitingSteps);

// After: intervention clears
updated.Intervention.IsRequired.ShouldBeFalse();
```

Files affected: `ApplicationStepFunctionsTests.cs`

## Pattern 4: TryMarkOrchestrationAsFailedAsync only sets intervention

When the orchestrator's catch block calls `TryMarkOrchestrationAsFailedAsync`, it now only calls `RequireIntervention()` on the document — it does NOT change the status to
`AnalysisFailed`. The status stays at whatever the last phase set it to (e.g., `Analyzing`).

```csharp
// Before
finalOffer.Status.ShouldBe(JobOfferStatus.AnalysisFailed);

// After
finalOffer.Status.ShouldBe(JobOfferStatus.Analyzing);
finalOffer.Intervention.IsRequired.ShouldBeTrue();
```

Files affected: `AnalyzeOfferOrchestrationTests.cs`

## Pattern 5: GetAliveInstancesAsync needs GetAllInstancesAsync mock

When production calls `GetAliveInstancesAsync()` (extension that queries `GetAllInstancesAsync`), the test's NSubstitute mock for `DurableTaskClient` must return real instances. Without this, methods like `ResetJobOfferAnalyze` are never
called and step/container status remains unchanged.

Requires `using Azure;` and `Page<T>/AsyncPageable<T>` from Azure.Core.

Files affected: `OfferReanalysisFunctionsTests.cs`

## Pattern 6: SetCustomStatus now includes attempt suffix

The `SetAwaitingStatusForStep` / `SetRetryingStatusForStep` methods now append `:{attempt}`:

```csharp
// Before
context.SetCustomStatus("awaitingUser:ExtractOfferData");

// After
context.SetCustomStatus("awaitingUser:ExtractOfferData:1");
```

Files affected: `AnalyzeOfferOrchestrationTests.cs`

## Pattern 7: WorkflowInProgressException mapping changed

The mapper type constant changed from `offer-analysis-in-progress` to `workflow-in-progress`, and the extension key changed from `offerId` to `workflowId`.

Files affected: `DomainExceptionProblemMapperTests.cs`

## Pattern 8: Intervention source casing — two-sided fix

When the user says "we use lower letter first everywhere", the backend domain constants (`ApplicationInterventionSource.CvGeneration` etc.) and the `InterventionSource.AwaitingSteps`
sentinel must use explicit camelCase strings instead of `nameof()`:

```csharp
// Correct
public const string CvGeneration = "cvGeneration";
public const string AwaitingSteps = "awaitingSteps";

// Wrong — produces PascalCase
public const string CvGeneration = nameof(CvGeneration);
```

Three surfaces must stay consistent:

1. Backend constants (`ApplicationInterventionSource.cs`, `InterventionSource` in `Intervention.cs`)
2. Frontend types (`types.ts`: `InterventionSource`) and fixtures
3. Domain serialization test (`ApplicationSerializationTests.cs`)

The serialization test is the authoritative pin — it catches backend/frontend casing drift.

## Pattern 9: Delegation coordination — subagents can overwrite your test fixes

When delegating test-fix work to subagents while also fixing tests yourself:

- Subagents may introduce broken helper methods (e.g., `WithAliveInstances` using
  `AsyncPageable<>.FromPages` which doesn't exist in the DurableTask SDK)
- Subagents modify files you read earlier — patch tools will reject with
  "modified since you last read it" warnings
- Subagents' NSubstitute mock setups can clash with yours (e.g., `GetAllInstancesAsync`
  configured differently in different tests)

**Mitigation:** After subagent completion, run `dotnet build` to catch compilation errors, then re-read any file the subagent touched before editing it. Keep subagent scope narrow —
"fix all 15 remaining tests" is better as "fix the 5 AnalyzeOfferOrchestration tests"
and "fix the 3 OfferReanalysis tests" dispatched separately.

## Pattern 10: Adding IsInstanceAliveAsync — cascading test breakage

When adding `durableTaskClient.IsInstanceAliveAsync(orchestrationId)` checks to an existing endpoint (e.g., dead-pipeline detection for resolve/skip/retry), ALL existing tests for that endpoint break. Root cause: `IsInstanceAliveAsync` is
an extension method that calls `GetInstanceAsync` internally — NSubstitute returns `null` by default, so the check always returns `false` (dead pipeline).

**Fix sequence:**

1. Create a `NewAliveDurableTaskClient()` helper that sets up `GetInstanceAsync` to return a running `OrchestrationMetadata`
2. Bulk-replace `Substitute.For<DurableTaskClient>("client")` → `NewAliveDurableTaskClient()`
   in the endpoint's test class
3. **Watch out:** `replace_all` hits the helper method itself, creating a recursive call. Fix the helper after bulk replacement.
4. Search for ALL callers of the modified endpoint across the test suite — integration tests in other classes (e.g., orchestration tests that call the endpoint) also break.
5. For dead-pipeline tests, override `GetInstanceAsync` to return `null` after creating the alive client.

```csharp
// Helper
private static DurableTaskClient NewAliveDurableTaskClient(string orchestrationId = OrchestrationId)
{
    var client = Substitute.For<DurableTaskClient>("client");
    var metadata = new OrchestrationMetadata("SomeOrchestration", orchestrationId)
        { RuntimeStatus = OrchestrationRuntimeStatus.Running };
    client.GetInstanceAsync(orchestrationId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<OrchestrationMetadata?>(metadata));
    return client;
}

// Dead-pipeline test
var client = NewAliveDurableTaskClient();
client.GetInstanceAsync(OrchestrationId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
    .Returns(Task.FromResult<OrchestrationMetadata?>(null));
```

Also check for copy-paste bugs in repository extension methods — `LoadJobOfferAsync`
had `nameof(Application)` instead of `nameof(JobOffer)` when copied from the application extensions. These surface only when tests assert `ResourceType` on the exception.

## Pattern 11: `using var timerTask` + refactored cancellation breaks test mocking

When production code uses `using var timerTask = context.CreateTimer(deadline, ct)` and a refactoring removes the `await cts.CancelAsync()` that previously cleaned up the timer, test mocking breaks irreparably:

**The causal chain:**

1. Before refactoring: code had `using var cts = new CancellationTokenSource(); ... await cts.CancelAsync();`
   The cancellation transitioned the mocked timer Task to Canceled state (completed), allowing
   `Task.Dispose()` in the `using` block to succeed.
2. After refactoring: the `CancelAsync()` was removed (moved to a different code path or eliminated). Now the timer task never transitions to a completed state.
3. NSubstitute's default for `Task` is `Task.CompletedTask`, so tests pass initially — but
   `Task.WhenAny(timerTask, eventTask)` non-deterministically picks whichever is first in the argument list (both are already completed), making park/resume tests flaky.
4. The natural fix — returning `new TaskCompletionSource<Task>().Task` (never-completing) from CreateTimer — causes `Task.Dispose()` to throw `InvalidOperationException` on the `using` block.

**Resolution options (in order of preference):**

1. **Restore the cancellation:** Add `await cts.CancelAsync()` back after the event wins
   `Task.WhenAny` in the production code. This is the correct Durable Task pattern.
2. **Remove `using var` from the timer task:** Durable Task Framework manages timer lifecycle internally. `Task.Dispose()` on a DTF timer is a no-op at best, harmful at worst.
3. **Use a linked CancellationTokenSource:** Create a child CTS that auto-cancels when the event completes, ensuring the timer transitions to Canceled state.

**Detection:** `InvalidOperationException: A task may only be disposed if it is in a completion
state (RanToCompletion, Faulted or Canceled)` at `Task.Dispose()` in `WaitForResolutionWithDeadline`.
