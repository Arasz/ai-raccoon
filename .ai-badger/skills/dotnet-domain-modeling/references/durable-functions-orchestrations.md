## Durable Functions Orchestrations

When building Azure Durable Functions orchestrations (activities, orchestrators, concurrency gates), see `references/durable-functions-orchestration-pitfalls.md` for:

- Non-deterministic API pitfalls (`Guid.NewGuid()`, `DateTime.UtcNow`)
- Missing usings for workflow types (`LlmStepRetry`, `InterventionCause`)
- `JsonNode.Deserialize` requiring `System.Text.Json` namespace
- Step lifecycle pattern (`ExecuteStepAsync` → park/resolve/skip)
- Conflict retry pattern (`SaveWithConflictRetryAsync`)
- Concurrency gate setup for new pipeline types
- TOCTOU fix: schedule-then-verify pattern (`ScheduleWithConcurrencyGuardAsync`)
- Conflict-aware step merging: re-run mutation against reloaded document

### Testing Durable Functions Pipelines

When writing tests for orchestrations, generators, HTTP-triggered functions, and exception mapping, see `references/durable-functions-testing-patterns.md` for:

- FakeLlmClient/FakeLlmClientFactory setup for generator tests
- Orchestration test patterns (SetupLoad/SetupSave stubs, scenario matrix)
- HTTP function test patterns (FunctionContext/DurableTaskClient substitution)
- Exception mapping test patterns (DomainExceptionProblemMapper verification)
- NSubstitute + DurableTaskClient pitfalls (`ThrowsAsync` vs `Task.FromException`, `TaskName` matchers, nullable params, expression tree null-propagation)
- C# 14 `extension` member syntax in tests
- Required usings for DurableTask test doubles

### Two-Phase Orchestration (Dry-Run + Apply)

When an operation needs user review before committing writes, use two separate orchestrations with separate instance IDs. See `references/durable-functions-orchestration-pitfalls.md` → "Two-Phase Orchestration Pattern" for the full
architecture, instance ID discipline, precondition checks, and partial failure handling.
