# C# Intervention Model Refactoring Patterns

Session reference: `job-search-ai-assistant` Intervention model migration
(flat properties → nested `Intervention` record on `WorkflowContainer<T>`).

## Changes Applied

### Production code (2 changes)
| File | Change | Pattern |
|---|---|---|
| `WorkflowOrchestrationExtensions.cs` | `nameof(TSaveActivity)` → `typeof(TSaveActivity).Name` | DURABLE2003: Generic type param resolves to param name |
| `WorkflowContainer.cs` | `private init` → `init` on `Intervention` property | Tests in separate assembly need `with` access |

### Test files (~30 files)
| Category | Old | New |
|---|---|---|
| Property access | `.InterventionRequired` | `.Intervention.IsRequired` |
| Property access | `.InterventionReason` | `.Intervention.Reason` |
| Property access | `.InterventionSource` | `.Intervention.Source` |
| AwaitingSteps source | `ApplicationInterventionSource.AwaitingSteps` | `InterventionSource.AwaitingSteps` |
| with expressions | `with { InterventionRequired = true, ... }` | `with { Intervention = new Intervention { ... } }` |
| Clear method | `.ClearInterventionFrom()` | `.ClearInterventionFrom(orchestratorId, source)` |
| Clear method | `.ClearInterventionIfNoAutomaticStepAwaitsUser()` | `.ClearInterventionIfNoAutomaticStepAwaitsUser(orchestratorId)` |
| Factory method order | `CreateAutomatic(id, type, crit, input, orchId, ts)` | `CreateAutomatic(id, orchId, type, crit, input, ts)` |
| Factory method order | `CreateManual(id, type, crit, input, ts)` | `CreateManual(id, orchId, type, crit, input, ts)` (new param) |
| Type renames | `SaveApplicationActivityInput` | `WorkflowContainerSaveInput<Application>` |
| Type renames | `LoadApplicationActivityInput` | `WorkflowContainerLoadInput<Application>` |
| Field renames | `.OrchestrationId` | `.OrchestratorId` |

### DTOs that KEPT flat properties (do NOT change)
- `ApplicationListFilter.InterventionRequired`
- `ApplicationSummary.InterventionRequired` / `InterventionReason`
- `ApplicationListItem.InterventionRequired` / `InterventionReason`

### ClearInterventionFrom orchestratorId semantics
The new `ClearInterventionFrom(string orchestratorId, string interventionSource)`:
- **orchestratorId**: Filters parked steps — only considers steps whose
  `OrchestratorId` matches. Tests must pass the same orchestratorId as their
  parked steps, or the parked step won't be found and intervention clears
  instead of downgrading.
- **interventionSource**: Ownership check — if the intervention's source
  doesn't match AND isn't `AwaitingSteps`, short-circuits to no-op.

### ClearInterventionIfNoAutomaticStepAwaitsUser behavior change
Now calls `ClearIntervention()` (clears ALL interventions, any source)
when no automatic steps are parked. Previously only cleared own source.
Test expectations for foreign-sourced interventions when no steps parked
must change from `IsRequired = True` to `IsRequired = False`.

## Error Categories Encountered
| CS Code | Count | Pattern |
|---|---|---|
| CS0721 | 1 | Static class as parameter type |
| DURABLE2003 | 2 | `nameof(T)` in generic context |
| CS0117 | 130 | Flat properties no longer exist on aggregate |
| CS1503 | 106 | Factory method param order swap |
| CS0246 | 30 | Renamed types |
| CS1061 | 44 | Property on wrong type (DTO vs aggregate) |
| CS1525 | 32 | Malformed `with` expressions from regex |
| CS7036 | 6 | Missing required params |
| CS9035 | 4 | Required member not set in initializer |
