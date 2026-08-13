# Intervention Refactoring — Session Error Analysis

Session transcript summary from `task/cv-orchestration-reliability` refactoring.

## Error Categories (365 build errors → 0, 40+ test failures → 2)

### Build errors eliminated:

| Category | Error | Count | Fix |
|---|---|---|---|
| static class as param | CS0721 | 1 | Changed test param `ApplicationInterventionSource` → `string` |
| `nameof(T)` on generic | DURABLE2003 | 2 | `nameof(TSaveActivity)` → `typeof(TSaveActivity).Name` |
| Property on DTOs | CS0117 | ~200 | Reverted DTO replacements; `.InterventionRequired` stays on `ApplicationListFilter`/`ApplicationSummary`/`ApplicationListItem` |
| Missing `using` | CS0103 | ~30 | Added `using JobSearchAiAssistant.Domain.Workflows;` |
| `with` expressions | CS0200 | ~50 | Changed `private init` → `init` on `WorkflowContainer.Intervention`; rewrote `with { InterventionRequired = ... }` → `with { Intervention = new Intervention { ... } }` |
| Renamed types | CS0246 | ~60 | `SaveApplicationActivityInput` → `WorkflowContainerSaveInput<T>`; `LoadApplicationActivityInput` → `WorkflowContainerLoadInput<T>` |
| Step method signature | CS1503 | ~20 | Added `orchestratorId` param to `CreateAutomatic`/`CreateManual` calls |

### Test failures fixed:

| Category | Tests | Root cause | Fix |
|---|---|---|---|
| ClearInterventionFrom orchestratorId mismatch | ~23 | Parked step's orchestratorId ≠ ClearInterventionFrom call's orchestratorId | Matched orchestratorId in 4 files per test context |
| Step mock action wrong | 4 | `RaiseStepResolutionEvent` always sends `Skip` now | Changed mock expectations from `Resolve`/`Retry` to `Skip` |
| Intervention serialization casing | 1 | `nameof()` produced PascalCase, frontend expects camelCase | Changed constants to explicit lowercase strings |
| Intervention.IsRequired assertion | ~10 | New `ClearInterventionIfNoAutomaticStepAwaitsUser` clears foreign interventions when no step parked | Updated test assertions to match new behavior |
| Stale binary with --no-build | many | Source fixes not reflected in test binary | Rebuilt before retesting |

## Key Domain Model Changes

- `Intervention` property moved from Application/JobOffer to `WorkflowContainer<T>` base
- `ClearInterventionFrom()` now requires `(string orchestratorId, string interventionSource)`
- `ClearInterventionIfNoAutomaticStepAwaitsUser()` now requires `(string orchestratorId)`
- `WorkflowStep.CreateAutomatic/CreateManual` parameter order: `(id, orchestratorId, type, criteria, input, timestamp)`
- `OrchestrationId` → `OrchestratorId` on WorkflowStep
- `RaiseStepResolutionEvent` always sends `Skip` action now