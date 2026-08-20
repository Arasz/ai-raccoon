# Subsystem Removal with Lean Replacement

When removing an entire subsystem (cost tracking, analytics, feature flag infrastructure) and replacing it with a simpler alternative, the safe ordering prevents cascading compile errors.

## Trigger

- Removing N+ files that form a coherent subsystem (interfaces, implementations, fakes, tests, infra config)
- Replacing with a simpler system that serves the same consumer contract (same callers, fewer dependencies)

## Phased Workflow

### Phase 0: Create the replacement (before touching consumers)

Create the new interface and implementation FIRST, while the old system still exists. Both can coexist — the old interface compiles, the new interface compiles, consumers still use the old one.

```
1. Write the new domain interface (ILlmTokenUsageTracker)
2. Write the new infrastructure implementation (MeterLlmTokenUsageEmitter + LlmTokenUsageTracker)
```

### Phase 1: Update all consumers to use the new type

Replace the old type with the new type in constructors and call sites. Do this BEFORE deleting the old files, so the compiler catches any missed references immediately.

```
For each consumer file:
  - Replace constructor param: ILlmCostTracker costTracker → ILlmTokenUsageTracker tokenUsageTracker
  - Replace call sites: costTracker.TrackAsync(...) → tokenUsageTracker.TrackAsync(...)
  - Build after each file (or batch of related files)
```

**Pitfall:** If you delete the old implementation files first, you get hundreds of cascading errors across every consumer. The compiler can't tell you "you forgot to update AnthropicLlmClient"
because the type simply doesn't exist anymore. Updating consumers first means the compiler reports ONLY the old implementation files as broken — a clean signal.

### Phase 2: Remove the old subsystem files

Delete in dependency order:

1. Infrastructure implementations (repositories, emitters, seeder, pricing catalog)
2. Domain interfaces and types (cost record, pricing, calculator, exception)
3. Budget guard / enforcement wrappers

Delete all at once (batch terminal `rm`) — they have no remaining consumers.

### Phase 3: Update DI registration

The composition root (InfrastructureDependencies, Program.cs) references both old and new types. Update in one pass:

1. Remove old registrations (ILlmCostTracker, ILlmCostLedgerRepository, ILlmBudgetGuard, etc.)
2. Add new registrations (ILlmTokenUsageTracker, MeterLlmTokenUsageEmitter)
3. Remove decorator/factory wrappers that only existed for the old subsystem (BudgetEnforcingLlmClientFactory)
4. Register the inner factory directly as the interface (PerUserLlmClientFactory → ILlmClientFactory)

### Phase 4: Remove infrastructure config

Terraform, Azure settings, local.settings — remove container definitions, app settings, alert rules, and variables that referenced the old subsystem.

### Phase 5: Remove frontend references

The frontend may reference problem types, error figures, or UI copy from the old subsystem. Remove problem type constants, helper functions, and UI branches.

### Phase 6: Clean up tests

1. Delete test files for the removed subsystem (unit tests, contract tests, integration tests)
2. Delete test fakes (RecordingLlmCostTracker, FakeLlmPricingCatalog, InMemoryLlmCostLedgerRepository)
3. Create new test fakes for the replacement (RecordingLlmTokenUsageTracker)
4. Update remaining tests that referenced old types

### Phase 7: Build & verify

```bash
dotnet build
dotnet test --filter "RequiresInfra!=true"
```

## Ordering Rules

| Rule                                         | Rationale                                                                             |
|----------------------------------------------|---------------------------------------------------------------------------------------|
| Create replacement before updating consumers | Both interfaces coexist; no breakage                                                  |
| Update consumers before deleting old files   | Compiler catches missed references                                                    |
| Delete old files before updating DI          | DI references the old types; delete first, then DI update removes the last references |
| Update DI before infra config                | App may fail to start if config references deleted sections                           |
| Clean up tests last                          | Tests are the final consumer; they break last                                         |

## Multi-language Considerations

When the subsystem spans multiple languages (C# + TypeScript + Terraform):

1. Do the C# changes first (Phases 0–3) — it's the compilation gate
2. Do Terraform next (Phase 4) — `terraform validate` catches structural errors
3. Do TypeScript last (Phase 5) — it's the most forgiving (no build gate for unused exports)
4. Tests in each language after their respective phase

## Real Example: Removing LLM Cost/Price System

Removed: `ILlmCostTracker`, `LlmCostCalculator`, `LlmPricing`, `ILlmPricingCatalog`,
`ILlmCostLedgerRepository`, `LlmBudgetGuard`, `BudgetEnforcingLlmClient`, `LlmBudgetOptions`,
`LlmBudgetExceededException`, 16 infrastructure files, 14 test files, 4 fakes, Terraform alerts, app settings, frontend problem types.

Replaced with: `ILlmTokenUsageTracker` (1 interface), `MeterLlmTokenUsageEmitter` (1 emitter),
`LlmTokenUsageTracker` (1 implementation) — 3 files total.

**File counts:**

- Deleted: ~35 files
- Created: 3 files
- Modified: ~15 files
- Net reduction: ~17 files

## Critical Pitfalls

### 1. Don't delete the Budget/ directory — let it stay empty

After deleting all files in `Domain/Budget/` or `Infrastructure/Llm/Budget/`, the empty directories are harmless. `rm -f` on each file is safer than `rm -rf` on the directory (avoids accidentally deleting files that were added between
planning and execution).

### 2. Remove the `using` for deleted namespaces in every consumer

When you delete `JobSearchAiAssistant.Domain.Budget`, every file that had
`using JobSearchAiAssistant.Domain.Budget;` will get CS0246 errors. The Phase 1 consumer updates should remove these usings as part of the constructor-param replacement pass.

### 3. Doc comments reference deleted types

XML `<see cref="ILlmBudgetGuard"/>` in remaining files won't cause compile errors but will produce CS1574 warnings. Scan remaining files for `cref` references to deleted types.

### 4. Frontend problem types may be referenced in tests too

If the frontend has test files that assert on problem type strings (e.g.,
`ProblemType.llmBudgetExceeded`), those tests need updating alongside the frontend cleanup.

### 5. `replace_all` on method-call arguments misses argument variants

When replacing a method call like `ClassifyAsync(Signal(), 0m, 0.01m, 100m, CancellationToken.None)`
→ `ClassifyAsync(Signal(), CancellationToken.None)` with `replace_all=true`, it only matches the exact `Signal()` pattern. Other calls with different arguments — `Signal(excerpt)`, `Signal(longExcerpt)`,
`signal` (variable) — require their own separate patches.

**The trap:** After the `replace_all` succeeds, you assume all call sites are fixed. But the variants remain untouched, producing CS1501 (wrong argument count) errors.

**Safe approach:**

1. Search for ALL call sites first to catalog every argument variant
2. Write a separate `patch()` call for each distinct argument pattern
3. Rebuild after all patches to catch stragglers

### 6. `grep "error CS"` returns exit code 1 when no matches

`dotnet build 2>&1 | grep "error CS"` returns exit code 1 when the build succeeds with zero errors (grep exits 1 on no match). Use `dotnet build 2>&1 | tail -5` instead to see the summary line.

### 7. Mixed-file updates: tuple return types in test helpers

When production constructors drop parameters, test helpers returning tuples with the deleted type also need updating — both the return type declaration AND the constructor call inside.

### 8. Tests-only cleanup variant

When production code was already migrated (Phases 0–5 done), only Phase 6 remains:

1. Categorize build errors into **delete-whole-file** vs **update-references**
2. Delete pure-delete files first, then create new fakes, then update remaining files
3. Rebuild and run tests — watch for incorrectly modified test arguments
