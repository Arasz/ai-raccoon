---
name: dotnet-domain-modeling
description: "Use when modeling immutable C#/.NET domain layers: sealed records with required/init props, CommunityToolkit.Diagnostics guards (and when to hand-roll them), state-transition methods, policy objects, extension-point interfaces (DIM), FluentValidation nested validators, ArchUnitNET purity enforcement — with TDD for pure domain layers. Triggers: DDD aggregates/value objects, domain-purity rules, validator design."
version: 1.1.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, csharp, domain-driven-design, records, communitytoolkit, ddd]
    related_skills: [test-driven-development, refactoring-fix, safe-bulk-refactoring, dotnet-mcp-server]
---

# .NET Domain Modeling

Immutable domain models in C# using sealed records, CommunityToolkit.Diagnostics guard clauses, and pure-domain TDD.

## When to Use

- Building DDD aggregates, entities, value objects in C#
- Domain layer must stay pure (no infra, HTTP, persistence, LLM dependencies)
- State machine / lifecycle transitions on domain objects
- Policy objects that evaluate decisions based on aggregate state + confidence thresholds
- Extension-point interfaces (repository, monitor, adapter)

## Immutable Record Pattern

### Structure

```csharp
using CommunityToolkit.Diagnostics;

namespace MyApp.Domain.Feature;

/// <summary>Brief doc comment — state contract, not rationale.</summary>
public sealed record MyAggregate
{
    public required string Id { get; init; }
    public required string UserId { get; init; }  // partition key
    public MyStatus Status { get; init; } = MyStatus.Draft;
    public required DateTimeOffset CreatedAt { get; init; }

    // Immutable transition: guard + return new instance
    public MyAggregate Activate()
    {
        if (Status != MyStatus.Draft)
            ThrowHelper.ThrowInvalidOperationException(
                $"Cannot activate from '{Status}'; only 'Draft' is allowed.");
        return this with { Status = MyStatus.Active };
    }
}
```

### Conventions

| Convention | Example |
|---|---|
| `sealed record` | No inheritance, value semantics, `with` expressions |
| `required` properties | Mandatory fields enforced at compile time |
| Default values on optional | `Status { get; init; } = Status.Draft;` |
| Methods return new instance | `return this with { ... };` — never mutate |
| Guard at entry | `ThrowHelper.Throw*` or `Guard.*` |
| Factory methods | `static MyAggregate Create(...)` for known entry points |
| camelCase JSON | Use `[JsonPropertyName("camelCase")]` if serialized |
| Minimal doc comments | 1–3 lines, state contract not rationale |


> Constructor-validated records: read `references/constructor-validated-records.md` when designing constructor-validated record types.

## CommunityToolkit.Diagnostics Guards

If the repo's clean-layering rule forbids new domain packages (a domain dependency is an ADR-level decision), hand-roll a tiny `internal static` Guard class instead — see `references/pure-domain-project-scaffolding.md` for the shape.

Core facts: `Guard.IsNotNull/IsNotNullOrWhiteSpace/IsGreaterThan/IsLessThanOrEqualTo` and `ThrowHelper.Throw*` work as expected; `Guard.IsEqualTo` has notnull+IEquatable constraints. The three real pitfalls, with code: read `references/communitytoolkit-guards-full.md` when a guard's exact constraint semantics matter.

1. **Guards return void** — cannot compose in field initializers (`CS0023`/`CS0029`). Use the throw-helper coalesce (`x ?? ThrowHelper.ThrowArgumentNullException<T>(nameof(x))`) when the guard must run at field-init time.
2. **`??`-coalescing ctor-args test helpers swallow explicit `null!`** — the default substitutes, the guard never fires, the test fails "should throw but did not". Use one full ctor call per guard with the target arg literally `null!` + a `ParamName` assertion.
3. **`<Nullable>enable</Nullable>` does not remove the need for boundary guards** — NRT is compile-time only; the runtime adds no null checks ([Microsoft Learn](https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/nullable-reference-types#nullable-references-and-static-analysis)), so deserialization, reflection and `#nullable disable` callers still deliver `null`. Drop the ctor guard only on internal types constructed solely by DI inside one nullable-enabled assembly; on public API boundaries keep it, per the framework invariant `invariants/guard-clauses.md`.


> State Machine Pattern (enum states, transition methods, exception pattern): read `references/state-machine-pattern.md` when modeling a state machine.

> C# string-interpolation traps (`\b` is backspace, not a regex word boundary): read `references/csharp-string-interpolation-gotchas.md` when building a regex or a path inside an interpolated string.

## Policy Object Pattern

When a domain decision depends on multiple inputs (aggregate state, signal classification, confidence threshold) and the logic is too complex for a single transition method, extract it into a **standalone policy class**.

```csharp
public sealed class SignalTransitionPolicy
{
    private readonly ChannelMonitoringOptions _options;

    public SignalTransitionPolicy(ChannelMonitoringOptions options)
    {
        Guard.IsNotNull(options);
        _options = options;
    }

    public SignalTransitionDecision Evaluate(ChannelSignal signal, Application application)
    {
        // Guard inputs, then evaluate decision tree:
        // 1. NoOp guards (no classification, already disposed, aggregate terminal, etc.)
        // 2. Allowed-transition check via ApplicationStateMachine.IsAllowed()
        // 3. "At or past" ordinal check for idempotent detection
        // 4. Confidence gate → Apply vs Propose
        // 5. Terminal target → always Propose
    }
}
```

### Conventions

| Convention | Example |
|---|---|
| Options record for thresholds | `ChannelMonitoringOptions` with `AutoApplyConfidenceThreshold` |
| Decision record as output | `sealed record SignalTransitionDecision` with `Type`, `TargetState`, `Reason` |
| Decision enum | `TransitionDecisionType { Apply, Propose, NoOp }` |
| Reason string includes source identity | `"Auto-applied from signal '{Id}' (source: {Source})."` |
| Class is `sealed class`, not record | Policies have no identity; they're services |

### Testing Policy Objects

For state-machine-dependent policies, read `references/state-machine-policy-testing.md` when testing them:
- Walking a state machine forward in test helpers
- Ordinal "at or past" comparison for idempotent/out-of-order detection
- Decision-category test matrix and note-content verification

## Injectable Components (Project Convention)

In injected-dependency projects: **static classes are reserved for
extensions and constants. Classes with logic must be injectable components with interfaces.**
Read `references/injectable-components-pattern.md` when applying the injectable-components rule; it has the conversion pattern and
migration priority.

## Extension-Point Interfaces

Interfaces live in the domain layer; implementations live in infrastructure.

```csharp
namespace MyApp.Domain.Feature;

public interface IChannelMonitor
{
    string ChannelType { get; }
    Task<IReadOnlyList<ChannelSignal>> FetchNewSignalsAsync(
        string userId, string? watermark, CancellationToken ct);
}

public interface IMyRepository
{
    Task<MyAggregate?> GetByIdAsync(string id, string userId, CancellationToken ct);
    Task<IReadOnlyList<MyAggregate>> GetByParentIdAsync(string parentId, string userId, CancellationToken ct);
    Task<MyAggregate> UpsertAsync(MyAggregate entity, CancellationToken ct);
}
```

Conventions:
- `CancellationToken ct` as last parameter
- `string userId` for partition-key scoping
- `IReadOnlyList<T>` for collections (not `IEnumerable<T>` for async)
- Nullable return for single-entity lookups

### Evolving Interfaces with Default Interface Methods (DIM)

When an existing interface needs a new method but all current implementors should keep working unchanged, add the method with a **default implementation**. This avoids a breaking change across all implementations.

**Pattern:** Add `string? TryGetUserId(AuthenticatedPrincipal) => null;` to `IPrincipalAllowlist`. The default returns `null`, meaning "I don't resolve dynamic IDs — fall back to the caller's static config." New implementations override it to return a real userId; existing ones inherit the `null` default and keep working.

```csharp
public interface IPrincipalAllowlist
{
    bool IsAllowed(AuthenticatedPrincipal principal);

    // NEW — default null means "caller should use IsAllowed + config-driven userId"
    string? TryGetUserId(AuthenticatedPrincipal principal) => null;
}
```

**Caller pattern (PrincipalResolver):** Try the new method first, fall back to the old contract:

```csharp
var userId = allowlist.TryGetUserId(principal);
if (userId is not null)
    return new PrincipalResolution.Authenticated(userId);

// Fallback: static allowlist path
return allowlist.IsAllowed(principal)
    ? new PrincipalResolution.Authenticated(options.UserId)
    : new PrincipalResolution.Forbidden(...);
```

**When to use:**
- Migrating from a static/single-user implementation to a dynamic/multi-user one
- The new method returns richer data (userId) than the existing bool method
- You want existing tests to pass unchanged (default `null` → falls through to old path)

**Pitfall:** the default implementation is used only when the implementor does NOT override it — an explicit implementation on the deriving type silently wins.

## Domain Purity Enforcement

Use ArchUnitNET (or similar) to enforce no infra dependencies:

```csharp
private const string ForbiddenPattern = @"^(Microsoft\.Azure|Azure\.|Microsoft\.EntityFrameworkCore|System\.Net\.Http)";

[Fact]
public void Domain_types_do_not_depend_on_infra_namespaces()
{
    var rule = Types()
        .That().ResideInAssembly(typeof(DomainAssemblyMarker).Assembly)
        .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(ForbiddenPattern));
    rule.Check(Architecture);
}
```

## FluentValidation Nested Validators (Project Convention)

When adding FluentValidation validators that nest (child validators, property-level rules, camelCase property paths, constructor guards → boundary validators), follow the full convention in `references/fluentvalidation-nested-validators.md`.

## TDD Workflow for Domain Models

RED → minimal domain types → GREEN → purity re-check, with the stub-first recipe for data-heavy models and the 'explore existing patterns first' step: read `references/tdd-workflow-domain-models.md` when doing TDD for domain models. Brand-new pure-domain project scaffolding: read `references/pure-domain-project-scaffolding.md` when scaffolding a pure-domain project.

## Deterministic Classification Pipeline

When a domain feature processes external signals (emails, notifications, etc.) through multiple stages before taking action, use a **pipeline of static utility classes**. Each stage is a pure function — no HTTP, persistence, or LLM dependencies. Stage sequence, per-stage test patterns, and the LLM-fallback classifier: `references/deterministic-classification-pipeline.md`; signal correlation & bootstrap import: `references/signal-correlation-and-bootstrap-import.md`; transport→repository ingestion with cursor durability: `references/ingest-wiring-pattern.md`.

For financial/tax domain modeling, read `references/financial-domain-modeling.md` when modeling rate tables, rounding, or progressive tax.

## HTTP Endpoint Testing (Azure Functions)

Applies only when endpoints are Azure Functions HTTP triggers — skip otherwise.

When writing tests for Azure Functions HTTP-triggered endpoints (non-durable), see `references/http-endpoint-testing-patterns.md` for:
- Test harness setup (FunctionContext, DefaultHttpContext, response body reading)
- camelCase enum serialization pitfall and detection
- userId-scoping test pattern
- In-memory repository with optimistic concurrency
- Middleware testing (AuditMiddleware, PrincipalResolutionMiddleware — GetHttpContext via Items dict)
- Status endpoint testing (DurableTaskClient.GetInstanceAsync mocking)
- PUT round-trip with FluentValidation (save + validate + return)
- Optional body parameters on Azure Functions methods
- Required NuGet packages

## Recommendation Engine Pattern

When a feature produces recommendations from multiple deterministic heuristics (with optional LLM prose framing but never LLM-authored numbers), use the static-heuristic + engine pattern. Heuristics are static methods operating on pre-computed domain results; the engine orchestrates calculator + heuristics, clamps between floor/stretch, and refuses unsupported inputs. Tests assert `Source == Deterministic` to prove no LLM touched the figures.

```
Inbound message → RelevanceFilter → Correlator → Classifier → Policy → Transition
                   (ours?)      (which entity?) (what kind?) (should we act?)
```

Full structure, conventions, testing strategy, and pitfalls: read `references/recommendation-engine-pattern.md` when building a recommendation engine.

## Infrastructure Adapter Pattern (External Services)

When implementing a new external-service integration (e.g. a mail API), follow the full 7-step sequence in `references/infrastructure-adapter-pattern.md`. Covers: transport DTO + interface, Fake transport, TDD cycle, monitor implementation, token refresher with deterministic intervention IDs, high-performance logging, and deduplication.

Key distinction from Cosmos persistence: the transport interface lives in Infrastructure, Domain only sees `IChannelMonitor`, and the adapter maps external wire types to domain signals.

## Cosmos Persistence (Infrastructure Layer)

Applies only when the project persists to Azure Cosmos DB — skip otherwise. The general shape holds for any store: a repository interface in Domain, a contract test suite, an InMemory fake, and a store-specific adapter in Infrastructure.

When implementing the Cosmos repository for a domain entity, follow the full 9-step sequence in `references/cosmos-persistence-implementation.md`. Covers: contract tests, InMemory fake, CosmosOptions, Cosmos repository, DI, Terraform container, and the easily-forgotten ProvisionCosmosEmulator update.

Four Cosmos-specific sub-patterns (same reference) — each a worked example of a store-agnostic need (encryption-at-rest, optimistic concurrency, single-document config, blind-vs-conditional upsert):
- **Encrypted document** — entity has sensitive data (API keys, compensation): encrypt via `ISecretCipher` before persisting; Cosmos stores a wrapper with `EncryptedSecret`. Test with ephemeral `DataProtectionProvider.Create("scope")`.
- **Optimistic concurrency** — contract uses `VersionedDocument<T>` (entity + ETag): CreateItemAsync/ReplaceItemAsync with ETag guards, `ConcurrencyConflictException` on conflicts.
- **Simple config document** — when the entity is a single per-user config (userId = id = partition key): ReadItemAsync + UpsertItemAsync, no concurrency. See `references/cosmos-persistence-implementation.md`.
- **Wildcard-ETag upsert** — `UpsertAsync(entity, etag)` supports `"*"` for blind upsert, real ETags for conditional replace: UpsertItemAsync with optional `IfMatchEtag`. See `references/cosmos-persistence-implementation.md`.


> High-performance logging: read `references/high-performance-logging.md` when applying the high-performance logging convention.

## Common File Layout

Domain + Infrastructure + Tests layout, naming, and file-per-type conventions: read `references/common-file-layout.md` when laying out the project.


> Intervention sources (which upstream writes flow into the domain): read `references/intervention-sources.md` when tracing how intervention sources map onto the model.

## Exception → ProblemDetails Wiring

When mapping domain exceptions to RFC 7807 ProblemDetails (problem-type constants, switch-case mapping, WriteProblemAsync), follow the full recipe in `references/exception-problemdetails-wiring.md`.

## Testing: Lifecycle Completeness Matrix

Build a transition matrix — N states × M transition methods, one test per cell, invalid paths asserted to throw: read `references/lifecycle-completeness-matrix.md` when building the transition matrix.

## Gotchas

Rows naming Azure Functions, Durable Functions, or Cosmos apply only if the project uses that service.

| Pitfall | Fix |
|---|---|
| `Guard.IsEqualTo` fails with nullable/enum types | Use `if` + `ThrowHelper.ThrowInvalidOperationException` — see `references/communitytoolkit-guard-pitfalls.md` |
| InternalsVisibleTo missing for Domain → Domain.Tests | The Domain project does NOT have `<InternalsVisibleTo>` by default. When implementing `internal` methods that tests need to call, add `<InternalsVisibleTo Include="<Proj>.Domain.Tests"/>` to `src/<Proj>.Domain/<Proj>.Domain.csproj`. Without it, tests get `CS0117: 'Type' does not contain a definition for 'Method'`. |
| Shouldly `ShouldContain` on `string?` properties (CS8604) | When testing nullable string properties like `SkipReason` or `Note` with `.ShouldContain(...)`, the C# analyzer flags CS8604 (possible null reference). Fix: use the null-forgiving operator: `.SkipReason!.ShouldContain(...)`. The test assertion itself IS the null check — if SkipReason were null, the test would fail before Shouldly even runs. |
| Missing `required` on constructor-like properties | Use `required` keyword, not `= null!` |
| `Array.IndexOf` returns -1 for states not on the forward path (Failed, Declined) | Guard negative indices before comparing ordinals — return `false` to mean "not comparable" |
| `Guid.NewGuid()` in Durable Functions orchestrator | Use `context.NewGuid()` — see `references/durable-functions-orchestration-pitfalls.md` |
| Entity/workspace ids via `Guid.NewGuid()` | User preference: use **sortable v7 guids** — `Guid.CreateVersion7()` (net9+) for ids that get listed or ordered. v7 embeds a timestamp, so lists sort deterministically by creation order without a separate CreatedAt sort. `Guid.NewGuid()` (v4) is random — fine for true uniqueness, wrong when ordering matters. Workspace ids, session ids, and any "list by recency" entity are v7 candidates. |
| `ThrowsAsync` on abstract DurableTaskClient methods | NSubstitute can't intercept `.ThrowsAsync()` on `Task<T>` returns from abstract methods. Use `.Returns(Task.FromException<T>(ex))` instead — see `references/durable-functions-testing-patterns.md` |
| C# interpolated string `$"\b"` is backspace, NOT regex word boundary | The compiler turns `\b` into U+0008 before the regex engine sees it, so the pattern silently never matches. **Fix:** `$"\\b..."` or `$@"\b..."`. Read `references/csharp-string-interpolation-gotchas.md` when a regex compiles but matches nothing — it also covers the raw-string-literal CS8997 trap. |
| .NET `Regex \b` word boundary not matching at string start | In C#, `\\b` in `Regex.Matches(text, @"\\bVP\\b")` (verbatim string) correctly produces the regex `\b`, but .NET's word-boundary rules use Unicode categories that differ from PCRE. `\bVP\b` may not match "VP" at string boundaries in .NET when Python/JS match on the same input. **Fix:** replace `\\b` with `string.Contains(word, OrdinalIgnoreCase)` + manual word-boundary check, or use `string.IndexOf` for position extraction. Don't spend multiple iterations tweaking regex patterns — switch to string methods after the first `\\b` miss. |
| Shouldly `ShouldContain(predicate)` shows no detail on failure | When `collection.ShouldContain(x => x.Prop.Contains("X"))` fails, Shouldly reports the predicate but not the actual values in the collection. **Fix:** add a temporary `Assert.Fail` that dumps all actual values: `Assert.Fail($"Actual: {string.Join("; ", items.Select(i => i.Prop))}")`. Remove after fixing. This one-line diagnostic saves multiple blind fix cycles. |
| Injecting `ILlmCostTracker` into LLM-calling classes | Cost tracking is handled by the infrastructure-layer `ILlmCostTracker` decorator that wraps `ILlmClient`. Classifiers and orchestrators do NOT need to inject `ILlmCostTracker` — they just use the correct `StepType` string so the decorator can tag the ledger record. Only inject `ILlmBudgetGuard` for pre-call budget checks. |

For worked cases (Azure Functions, worktree discipline, locale-sensitive tests), read `references/project-gotchas.md` when one of them bites.


> Durable Functions: applies only when orchestrating with Durable Functions — skip otherwise. Read `references/durable-functions-orchestrations.md` for deterministic code constraints, testing pipelines, and dry-run+apply.
