# Azure Durable Functions Orchestration Pitfalls

## Non-Deterministic APIs in Orchestrators

Durable Functions orchestrators replay from the history log. Any non-deterministic call produces different values on replay, breaking replay correctness.

### `Guid.NewGuid()` → `context.NewGuid()`

```csharp
// ❌ DURABLE0002: non-deterministic on replay
var sessionId = Guid.NewGuid().ToString("n");

// ✅ Deterministic — replays return the same Guid
var sessionId = context.NewGuid().ToString("n");
```

The analyzer emits: `DURABLE0002: The method 'X' uses 'Guid.NewGuid()' that may cause non-deterministic behavior when invoked from orchestration 'Y'`

Other non-deterministic APIs to avoid in orchestrators:
- `DateTime.UtcNow` → `context.CurrentUtcDateTime`
- `Random.Next()` → use `context.NewGuid()` or pre-compute in an activity
- `Task.Delay()` → `context.CreateTimer()`

## Missing Usings for Workflow Types

New orchestrations in the `Applications/Interviews/` folder need these usings that the parent folder's existing orchestrations have but aren't obvious:

```csharp
using JobSearchAiAssistant.Api.Workflow;              // LlmStepRetry.Options
using JobSearchAiAssistant.Api.Workflow.Orchestrations; // StepResolutionEvent, StepResolution
using JobSearchAiAssistant.Domain.Workflows;          // InterventionCause, Intervention
using JobSearchAiAssistant.Domain.Workflows.Steps;    // StepType, StepExecutionMetadata
```

### Common build errors when these are missing:

| Missing Type | Using Needed | Error |
|---|---|---|
| `LlmStepRetry` | `JobSearchAiAssistant.Api.Workflow` | CS0103 |
| `InterventionCause` | `JobSearchAiAssistant.Domain.Workflows` | CS0103 |
| `StepResolutionEvent` | `JobSearchAiAssistant.Api.Workflow.Orchestrations` | CS0246 |
| `CustomStatuses` (no, this is local) | N/A | N/A |

## `JsonNode.Deserialize<T>()` Requires System.Text.Json

When using `step.Output.Deserialize<T>(options)` in orchestrations:

```csharp
// ❌ CS1061: 'JsonNode' does not contain a definition for 'Deserialize'
using System.Text.Json.Nodes; // alone is not enough

// ✅ Add both:
using System.Text.Json;
using System.Text.Json.Nodes;
```

`Deserialize` is an extension method in `System.Text.Json.JsonNodeExtensions` (namespace `System.Text.Json`), not a member of `JsonNode`.

## Step Lifecycle Pattern

Orchestrations that use `ExecuteStepAsync` get the full step lifecycle (park → await user resolution → retry/resolve/skip) automatically. The pattern:

```csharp
var step = await context.ExecuteStepAsync(
    new ApplicationStepExecutionContext<TOutput>(context.InstanceId, applicationDocument, initialSnapshot,
        () => context.CallActivityAsync<TOutput>(nameof(MyActivity), input, RetryOptions)
    ),
    new StepExecutionMetadata(StepType.MyStep, "Success description", ApplicationInterventionSource.MySource)
);

applicationDocument = step.Document; // may have been updated by the step lifecycle

switch (step.Status)
{
    case StepExecutionResultStatus.Skipped:
        return new Outcome(..., false);
    case StepExecutionResultStatus.Resolved:
        // Manual resolution — user supplied the output
        Guard.IsNotNull(step.Output);
        var manualResult = step.Output.Deserialize<T>(ApiJsonOptions.Pinned);
        break;
    default:
        // Normal completion
        Guard.IsNotNull(step.Output);
        var result = step.Output.Deserialize<T>(ApiJsonOptions.Pinned);
        break;
}
```

## Conflict Retry Pattern

Orchestrations that mutate the application document use `SaveWithConflictRetryAsync` which handles ETag conflicts by reloading and retrying:

```csharp
state = await context.SaveWithConflictRetryAsync(state,
    vd => vd.Document.AddPracticeSession(interviewId, session)
        .ClearInterventionFrom(ApplicationInterventionSource.InterviewPrepGeneration));
```

The mutate function receives the reloaded `VersionedDocument<Application>` and must return the mutated `Application`.

## Concurrency Gate for New Pipeline Types

When adding a new orchestration pipeline, the concurrency gate needs:

1. Instance ID methods in `ApplicationOrchestrationInstanceIds` (one per orchestration type)
2. A gate method in `IPipelineConcurrencyGate` + `PipelineConcurrencyGate` that checks `Pending/Running/Suspended` status
3. A domain exception (e.g. `PracticeSessionGenerationInProgressException`) for when the gate blocks
4. Wire the exception into `DomainExceptionProblemMapper` (triple: type constant + switch case + mapping method)

The gate checks both orchestration instance IDs when a single logical operation maps to multiple orchestrations (e.g. generation + scoring both block a new generation).

### TOCTOU Fix: Schedule-then-Verify (not Check-then-Schedule)

The original gate pattern had a TOCTOU window: `EnsureXxxNotRunningAsync` checks the instance, then the caller schedules. Between the check and the schedule, another caller could schedule the same orchestration.

**Fix:** Add `ScheduleWithConcurrencyGuardAsync` to the gate that uses the DurableTask runtime's own idempotent scheduling as the atomic guard:

```csharp
// ❌ TOCTOU: check-then-schedule
await gate.EnsureCoverLetterNotRunningAsync(client, appId, ct);
// ← window: another caller could schedule here
await client.ScheduleNewOrchestrationInstanceAsync(...);

// ✅ Atomic: schedule-then-verify
await gate.ScheduleWithConcurrencyGuardAsync(client, instanceId,
    nameof(MyOrchestration), input,
    id => new CoverLetterGenerationInProgressException(appId), ct);
```

Implementation: try to schedule with a deterministic instance ID. If `OrchestrationAlreadyExistsException`, check runtime status — throw the domain exception if still active (Pending/Running/Suspended), return normally if terminal (Completed/Failed/Terminated).

### Conflict-Aware Step Merging

When `SaveWithConflictRetryAsync` retries on `ConcurrencyConflictException`, it re-runs the mutation function against a freshly reloaded document. This naturally merges non-overlapping step updates because `AddOrReplaceStep(stepId)` only touches the target step — all other steps from the reloaded document are preserved. The existing pattern is correct for this; no special merge logic is needed beyond re-running the mutation against the reloaded document.

Key invariant: every mutation function used with `SaveWithConflictRetryAsync` must be safe to re-run against a document that may have changed (idempotent with respect to the mutation's own step IDs, additive for other steps).

## Two-Phase Orchestration Pattern (Dry-Run + Apply)

When an operation needs user review before committing writes (e.g., bulk import from an external source), use two separate orchestrations.

### Architecture

```
POST /resource/import → 202 + operationId (dry-run orchestration)
    ↓ (user polls /operations/{id})
Dry-run completes → preview stored as orchestration output
    ↓ (user reviews preview)
POST /resource/import/{operationId}/apply → 202 + new operationId (apply orchestration)
    ↓
Apply orchestration loads dry-run result, executes accepted items only
```

### Instance ID Discipline

Use SEPARATE deterministic instance IDs for each phase so they don't block each other:

```csharp
internal static class ImportOrchestrationInstanceIds
{
    public static string ForDryRun(string userId) => $"import-dryrun-{userId}";
    public static string ForApply(string userId) => $"import-apply-{userId}";
}
```

This lets the user review the dry-run result while the apply hasn't started. Both phases use `ScheduleWithConcurrencyGuardAsync` with their own instance IDs — completing a dry-run doesn't block starting another dry-run of the same type.

### Precondition Check: Load Dry-Run Result

The apply endpoint must verify the dry-run completed before scheduling:

```csharp
var dryRunMetadata = await durableTaskClient.GetInstanceAsync(dryRunInstanceId, ct);
if (dryRunMetadata is not { RuntimeStatus: OrchestrationRuntimeStatus.Completed })
    throw new ImportNotReadyException(operationId);
```

### Activity: Load Dry-Run Result

The apply orchestration loads the dry-run result from the DurableTask client:

```csharp
public sealed class LoadDryRunResultActivity(DurableTaskClient client, ILogger logger)
{
    [Function(nameof(LoadDryRunResultActivity))]
    public async Task<ImportPreview> RunAsync([ActivityTrigger] LoadDryResultInput input, CancellationToken ct)
    {
        var metadata = await client.GetInstanceAsync(dryRunInstanceId, ct);
        if (metadata is not { RuntimeStatus: OrchestrationRuntimeStatus.Completed, SerializedOutput: { } output })
            throw new ImportNotReadyException(input.OperationId);
        return JsonSerializer.Deserialize<ImportPreview>(output, ApiJsonOptions.Pinned);
    }
}
```

**Pitfall:** `DurableTaskClient` is injected as a constructor dependency in activities (unlike orchestrator methods where it arrives as `[DurableClient]` method parameter).

### Partial Failure Handling

The apply orchestration processes records sequentially. Each record's write is atomic — a failure on record N doesn't roll back records 1..N-1. Failed records become interventions (logged, surfaced in the result's `errors` array). The orchestration still completes — unlike LLM-step failures that park the orchestration, import errors are non-recoverable per-record.

## API-Layer Type Mapping Through Activities

When the domain layer produces types that differ from the API contract (e.g., domain has `RecordIndex: int`, API has `PreviewId: string`), the activity is the bridge:

```
Domain BootstrapImportPreview (RecordIndex, no OperationId)
    ↓ RunDryRunImportActivity maps to ↓
API LinkedInImportPreview (PreviewId as RecordIndex.ToString(), OperationId, Summary)
```

The activity:
1. Calls the domain's pure function (e.g., `BootstrapImporter.DryRun(...)`)
2. Maps domain types to API types (int → string for IDs, add OperationId, compute Summary)
3. Returns the API type as the orchestration output

**Pitfall:** Don't duplicate domain types in the API layer. Use the domain types as-is in activities and only create API-layer wrappers when the contract genuinely differs (adding OperationId, wrapping in Summary, converting IDs from int to string).
