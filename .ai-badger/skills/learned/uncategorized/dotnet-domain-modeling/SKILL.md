---
name: dotnet-domain-modeling
description: "C#/.NET immutable domain model patterns: sealed records, CommunityToolkit.Diagnostics guards, state-transition methods, extension-point interfaces, and TDD for pure domain layers."
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

## Constructor-Validated Records

When a record must reject invalid input **at construction** (blank ids, out-of-range limits), use an explicit constructor with guards plus get-only auto-properties. Optional params get defaults in the constructor signature; callers use named arguments.

```csharp
public sealed record MemoryWriteRequest
{
    public MemoryWriteRequest(
        string projectId,
        string content,
        string? context = null,
        bool isolated = false,
        string? agentId = null,
        string? workspaceId = null)
    {
        Guard.NotNullOrWhiteSpace(projectId, nameof(projectId));
        Guard.NotNullOrWhiteSpace(content, nameof(content));

        ProjectId = projectId;
        Content = content;
        Context = context;
        Isolated = isolated;
        AgentId = agentId;
        WorkspaceId = workspaceId;
    }

    public string ProjectId { get; }
    public string Content { get; }
    public string? Context { get; }
    public bool Isolated { get; }
    public string? AgentId { get; }
    public string? WorkspaceId { get; }

    // Computed property — no backing field, so record equality ignores it.
    public string ContextName => ContextNaming.WorkspaceContext(WorkspaceId!);
}
```

Why not the alternatives:

| Shape | Problem |
|---|---|
| Positional primary ctor + same-signature chaining ctor (`: this(...)` + validation) | Legal but easy to get wrong (defaults on both ctors, ambiguity risk) |
| `required ... init` properties | Compile-time presence, but cannot run validation logic |
| Hand-rolled `?? throw` / `if (x == null) throw` inline | Repo invariants prefer a guard helper — reads as intent, consistent exception type/message |

Key nuance: **computed (expression-bodied) properties are NOT part of record value equality** — they have no backing field, so the synthesized `Equals`/`GetHashCode` skip them. A derived property (e.g. `Context => ContextNaming.WorkspaceContext(Id)`) can therefore live inside a value-equality record safely. Stored auto-properties ARE compared.

Guard exception types: `ArgumentException` for blank strings, `ArgumentOutOfRangeException` for out-of-range numerics. Tests assert the specific type, not the base.

## CommunityToolkit.Diagnostics Guards

If the repo's clean-layering rule forbids new domain packages (a domain dependency is an ADR-level decision), hand-roll a tiny `internal static` Guard class instead of adding CommunityToolkit — see `references/pure-domain-project-scaffolding.md` for the shape.

### Works Fine

```csharp
Guard.IsNotNull(arg);
Guard.IsNotNullOrWhiteSpace(arg);
Guard.IsGreaterThan(value, 0);
Guard.IsLessThanOrEqualTo(value, 100);
ThrowHelper.ThrowArgumentException(nameof(arg), "message");
ThrowHelper.ThrowInvalidOperationException("message");
```

### Pitfall: Guard methods return void — cannot compose in field initializers

`Guard.IsNotNull(x)` returns `void` (verified in 8.4.2: both overloads `-> Void`). You CANNOT
write `private readonly IMemoryStore _store = Guard.IsNotNull(store);` (CS0023) or
`Guard.IsNotNull(extensions).ToList()` (CS0029). Either assign on the next line in a ctor body,
or drop the check entirely (next pitfall).

**Field-initializer-compatible form (project rule in AiRaccoon):** when the guard must run at
field initialization (primary ctor, no ctor body), use the throw-helper coalesce — it RETURNS the
value, unlike Guard, so it composes:

```csharp
public sealed class WatchCommands(IWatchStore watchStore) : IWatchCommands
{
    private readonly IWatchStore _watchStore = watchStore ?? ThrowHelper.ThrowArgumentNullException<IWatchStore>(nameof(watchStore));
}
```

### Pitfall: a `??`-coalescing ctor-args test helper swallows the explicit `null!`

When strengthening ctor null-guard tests, the tempting shape is a shared valid-args helper with
optional parameters so each test nulls out one arg:

```csharp
private EncryptionCommands ValidCtorArgs(SqliteConnectionFactory? bank = null, ...) =>
    new(bank ?? MakeBank(), bws ?? MakeBws(), ...);   // ❌

var ex = Should.Throw<ArgumentNullException>(() => ValidCtorArgs(bank: null!));  // "should throw but did not"
```

The `??` coalescing means `bank: null!` NEVER reaches the ctor — the default substitutes, the
guard never fires, and the test fails with "should throw ... but did not" (verified 2026-08-06,
hit three times before abandoning the helper). C# cannot distinguish "argument omitted" from
"argument explicitly null" through a coalescing helper. **The honest shape is explicit
constructions per test** — one full ctor call per guard, with the target arg literally `null!` —
plus a `ParamName` assertion so a swapped guard (e.g. `Guard.IsNotNull(bws)` validating `bank`)
fails CI instead of passing silently:

```csharp
[Fact]
public void Constructor_NullBank_ThrowsArgumentNullException()
{
    var ex = Should.Throw<ArgumentNullException>(() =>
        new EncryptionCommands(null!,
            new FakeBwsRunner(...), new StubEnvProvider(null), new EncryptionSourceSidecar(...), new FakeLogger()));
    ex.ParamName.ShouldBe("bank");
}
```

The `ex.ParamName.ShouldBe(...)` assertion is the load-bearing part: exception-type-only
assertions cannot catch a guard checking the wrong parameter.

### Pitfall: with `<Nullable>enable</Nullable>` + DI, ctor null-checks are dead code — delete them

A reviewer will ask "nullable analysis is enabled, do we need those null checks?" — the honest
answer is NO for non-nullable reference-type ctor params. With NRT on, the compiler enforces
non-null at every call site; DI containers never inject null (they throw on missing
registrations). The `x ?? throw new ArgumentNullException(nameof(x))` / `Guard.IsNotNull(x)`
guards on DI-injected ctor params are provably dead. Delete them (plain `_store = store;`),
keep guards for VALUE validation (whitespace, ranges) where NRT can't help. Don't convert
hand-rolled `?? throw` to `Guard.IsNotNull` as a "modernization" — that keeps dead code alive
with a library call.

### Pitfall: Guard.IsEqualTo Has notnull + IEquatable Constraint

`Guard.IsEqualTo<T>(T value, T target, string name)` requires `T : notnull, IEquatable<T>`.

**Fails to compile with:**

| Type | Error |
|---|---|
| Nullable reference (`string?`) | CS8714 — nullability doesn't match `notnull` |
| Nullable record (`MyRecord?`) | CS8714 — nullability doesn't match `notnull` |
| Enum without `IEquatable` | CS0315 — no boxing conversion to `IEquatable<T>` |

```csharp
// ❌ CS8714: string? doesn't match notnull
Guard.IsEqualTo(ApplicationId, null, nameof(ApplicationId));

// ❌ CS0315: enum doesn't implement IEquatable<T>
Guard.IsEqualTo(Disposition, SignalDisposition.Proposed, nameof(Disposition));
```

**Fix — use explicit `if` + ThrowHelper:**

```csharp
// Nullable check
if (ApplicationId is not null)
    ThrowHelper.ThrowInvalidOperationException(
        $"Signal is already correlated to application '{ApplicationId}'.");

// Enum check
if (Disposition != SignalDisposition.Proposed)
    ThrowHelper.ThrowInvalidOperationException(
        $"Cannot dismiss a signal in '{Disposition}' disposition; only '{SignalDisposition.Proposed}' is allowed.");
```

This pattern is consistent with how the project's `WorkflowStep.Require(bool, string)` works — explicit precondition checks with custom exceptions.

## State Machine Pattern

### Enum States

```csharp
public enum SignalDisposition { Proposed, Applied, Dismissed }
```

### Transition Methods

Each valid transition is a method that:
1. Guards preconditions (current state must be valid source)
2. Returns new instance with target state set
3. Throws `InvalidOperationException` on invalid source state

```csharp
public ChannelSignal Dismiss()
{
    if (Disposition != SignalDisposition.Proposed)
        ThrowHelper.ThrowInvalidOperationException(...);
    return this with { Disposition = SignalDisposition.Dismissed };
}

public ChannelSignal Apply()
{
    if (Disposition != SignalDisposition.Proposed)
        ThrowHelper.ThrowInvalidOperationException(...);
    return this with { Disposition = SignalDisposition.Applied };
}
```

### Exception Pattern

- **Invalid state transition**: `InvalidOperationException` (or custom domain exception)
- **Invalid argument**: `ThrowHelper.ThrowArgumentException` / `Guard.IsNotNull*`
- **Precondition failure**: `ThrowHelper.ThrowInvalidOperationException`

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

For state-machine-dependent policies, see `references/state-machine-policy-testing.md` for:
- Walking a state machine forward in test helpers
- Ordinal "at or past" comparison for idempotent/out-of-order detection
- Decision-category test matrix and note-content verification

## Injectable Components (Project Convention)

In AiRaccoon and similar injected-dependency projects: **static classes are reserved for
extensions and constants. Classes with logic must be injectable components with interfaces.**
See `references/injectable-components-pattern.md` for the rule, conversion pattern, and
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

**Pattern:** Add `string? TryGetUserId(AuthenticatedPrincipal) => null;` to `IPrincipalAllowlist`. The default returns `null`, meaning "I don't resolve dynamic IDs — fall back to the caller's static config." New implementations (e.g., `CosmosBetaAllowlist`) override it to return a real userId; old implementations (e.g., `SingleUserAllowlist`) inherit the `null` default and continue working.

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

**Pitfall:** DIM requires C# 8+ (already available in this project). The default implementation is only used when the implementor does NOT override it — if `SingleUserAllowlist` explicitly implements `TryGetUserId`, the default is ignored.

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

When a domain record needs validation (save-time, projection-time, or API-boundary), nest the `AbstractValidator<T>` class **inside** the validated type. Use `OverridePropertyName` for camelCase JSON property paths.

```csharp
using FluentValidation;

namespace MyApp.Domain.Feature;

public sealed record LinkedInProfileProjection
{
    public required string TemplateId { get; init; }
    public required string Headline { get; init; }
    public required IReadOnlyList<ProjectedPosition> Positions { get; init; }

    public sealed class Validator : AbstractValidator<LinkedInProfileProjection>
    {
        public Validator()
        {
            RuleFor(x => x.TemplateId).NotEmpty().OverridePropertyName("templateId");
            RuleFor(x => x.Headline).NotEmpty().OverridePropertyName("headline");
            RuleFor(x => x.Headline).MaximumLength(220).OverridePropertyName("headline");
            RuleFor(x => x.Positions).NotNull().OverridePropertyName("positions");
            RuleForEach(x => x.Positions)
                .SetValidator(new ProjectedPosition.Validator())
                .OverridePropertyName("positions");
        }
    }
}

public sealed record ProjectedPosition
{
    public required string Company { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public sealed class Validator : AbstractValidator<ProjectedPosition>
    {
        public Validator()
        {
            RuleFor(x => x.Company).NotEmpty().OverridePropertyName("company");
            RuleFor(x => x.Title).NotEmpty().OverridePropertyName("title");
            RuleFor(x => x.Title).MaximumLength(100).OverridePropertyName("title");
            RuleFor(x => x.Description).NotEmpty().OverridePropertyName("description");
            RuleFor(x => x.Description).MaximumLength(2000).OverridePropertyName("description");
        }
    }
}
```

### Conventions

| Convention | Rationale |
|---|---|
| `sealed class Validator` nested inside validated type | Co-located, discoverable, no separate file |
| `OverridePropertyName("camelCase")` on every rule | JSON error paths match API contract |
| `SetValidator(new Child.Validator())` for nested records | Composable validation chains |
| `MaximumLength(n)` for LinkedIn/platform limits | Hard enforcement, not warnings |
| Validate the validator in tests: `new T.Validator().Validate(instance).IsValid` | Ensures test fixtures pass validation |

### Testing Validators

Test both positive (valid input passes) and negative (limit violations fail):

```csharp
[Fact]
public void Validator_fails_when_headline_exceeds_220_chars()
{
    var projection = new LinkedInProfileProjection
    {
        TemplateId = "cvt-1",
        Headline = new string('A', 221), // one over limit
        // ... other required fields
    };

    var result = new LinkedInProfileProjection.Validator().Validate(projection);
    result.IsValid.ShouldBeFalse();
}

[Fact]
public void Validator_passes_on_valid_projection()
{
    var projection = /* valid fixture */;

    var result = new LinkedInProfileProjection.Validator().Validate(projection);
    result.IsValid.ShouldBeTrue();
}
```

### camelCase property paths: use the global resolver, not `WithName` (FluentValidation 12)

Verified against FluentValidation 12.1.1 by reflection:

- **`WithName("camelCase")` changes the ERROR MESSAGE only — `ValidationFailure.PropertyName`
  stays the C# member name (`"Limit"`, `"ProjectId"`).** If your tests assert
  `e.PropertyName == "limit"`, `WithName` silently fails them. `OverridePropertyName` (as in the
  example above) is the per-rule way to rename `PropertyName` itself.
- **FV12 removed `FluentValidation.Internal.CamelCasePropertyNameResolver`** — it does not exist
  anymore. To make EVERY validator report camelCase paths process-wide, set the resolver once:

```csharp
ValidatorOptions.Global.PropertyNameResolver = (_, member, _) =>
    member.Name.Length == 0 ? member.Name : char.ToLowerInvariant(member.Name[0]) + member.Name[1..];
```

- In a **library** (Core/domain project), a `[ModuleInitializer]` is the natural one-time home for
  this — but it trips `CA2255` ("ModuleInitializer only intended in application code"). Either
  scope a `NoWarn` with a justification comment in the csproj, or set the resolver in a static
  constructor of a type the app is guaranteed to touch. Tests that construct validators directly
  still see the global (module initializers run on assembly load), so the camelCase path is
  consistent between app and test runs.

### Constructor guards → boundary validators (migration pattern)

When a review says "use FluentValidation for the validation you have", the clean move is to
migrate constructor guards OUT of request records and INTO nested validators invoked at the
boundary:

1. Records become plain data assignment (no `Guard.*` in ctors); nested `Validator` classes own
   the rules — single source of truth, no duplication.
2. Invoke at the ONLY production construction site: `new SearchQuery.Validator()
   .ValidateAndThrow(query)` in the MCP tool / controller before delegating.
3. This is safe only when the record has one production construction site (grep `new <Type>(`
   in src/ first). Domain-internal records (constructed by services with generated ids) and
   static-helper argument guards (`ContextNaming`, `RatingPolicy`) keep CommunityToolkit guards —
   a validator with no boundary caller is dead code.
4. A new third-party dependency on the domain layer (FluentValidation in Core) is an
   architecture-level decision: write an ADR (the repo's `docs/adr/` convention) before adding
   the package reference.

## TDD Workflow for Domain Models

### 1. Explore existing patterns first

Read 3–5 existing files in the same domain to understand:
- Naming conventions (sealed record, required props, factory methods)
- Guard clause style (ThrowHelper vs Guard vs custom exceptions)
- Test patterns (Shouldly assertions, helper factories, xunit v3)

### 2. Write failing tests (RED)

```csharp
[Fact]
public void Dismiss_from_Proposed_succeeds()
{
    var signal = NewSignal(disposition: SignalDisposition.Proposed);
    var result = signal.Dismiss();
    result.Disposition.ShouldBe(SignalDisposition.Dismissed);
}

[Fact]
public void Dismiss_from_Applied_throws()
{
    var signal = NewSignal(disposition: SignalDisposition.Applied);
    Should.Throw<InvalidOperationException>(() => signal.Dismiss());
}
```

### 3. Verify RED — build fails or tests throw NotImplementedException

**Simple case (one new type):** Build fails with CS0234/CS0246 (type not found).

**Multi-type case (3+ new types across multiple test files):** Create minimal stub files with `throw new NotImplementedException()` so the project compiles. Verify the test *runner* executes and tests fail at runtime — this confirms the test harness reaches your assertions correctly before you write any logic.

```bash
dotnet build  # Should succeed (stubs compile)
dotnet test --filter "FullyQualifiedName~MyFeature" --no-build  # Should fail with NotImplementedException
```

**Why stubs over compile failure for multi-type:** When tests reference 3–4 new types across multiple files, a compile error in one file masks whether other test files even parse correctly. Stubs let you validate the full test harness structure (namespaces, imports, helper factories) before implementing logic.

**Exception — uniform missing-namespace red (brand-new project/namespace):** When every test file fails with the SAME `CS0234: type or namespace 'X' does not exist` because the whole namespace is absent (new Core project), compile-error red is sufficient — skip the stubs. Nothing is masked: every file parses, only the namespace lookup fails, and the error IS "feature missing". Verify red once with `dotnet test`, implement, then green. This is the shape used when scaffolding a fresh pure-domain library (see `references/pure-domain-project-scaffolding.md`).

### 4. Implement minimal domain types (GREEN)

Create the types, make tests pass.

**Port/interface member additions (contract rework):** when the corrected
contract adds a method to a domain port (e.g. `IMemoryStore.ShareAsync`), the
Domain test demanding it (a stub implementor + assertion on the returned
entry) fails to compile with `CS0535: does not implement interface member`
in EVERY downstream implementor. That is the expected red — the downstream
breakage is the next red, not a regression. Add the member to the interface
(no default impl unless the contract allows DIM), then resolve implementors
one at a time with their own failing tests. Grep all errors, not the first:
`dotnet build 2>&1 | grep -E "error CS" | sort | uniq -c` — the build stops
at the first failing project, hiding downstream test errors until the
upstream project compiles.

### 5. Verify GREEN — all tests pass

```bash
dotnet test --filter "RequiresInfra!=true"
```

### 6. Verify domain purity still passes

```bash
dotnet test --filter "DomainPurity"
```

### 0. Scaffolding a brand-new pure-domain project

Adding a new Core library to an existing solution (csproj shape, `InternalsVisibleTo` SDK item, slnx membership, zero-dependency Guard class, uniform missing-namespace red phase, test-count semantics)? See `references/pure-domain-project-scaffolding.md`.

## Deterministic Classification Pipeline

When a domain feature processes external signals (emails, notifications, etc.) through multiple stages before taking action, use a **pipeline of static utility classes**. Each stage is a pure function — no HTTP, persistence, or LLM dependencies.

For financial/tax domain modeling (rate tables, rounding, progressive tax), see `references/financial-domain-modeling.md`.

## HTTP Endpoint Testing (Azure Functions)

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

When a feature produces recommendations from multiple deterministic heuristics (with optional LLM prose framing but never LLM-authored numbers), use the static-heuristic + engine pattern. Heuristics are static methods operating on pre-computed domain results; the engine orchestrates calculator + heuristics, clamps between floor/stretch, and refuses unsupported inputs. Tests assert `Source == Deterministic` to prove no LLM touched the figures. See `references/recommendation-engine-pattern.md` for the full structure, testing strategy, and pitfalls.

```
Inbound message → RelevanceFilter → Correlator → Classifier → Policy → Transition
                   (is it job mail?) (which app?) (what kind?) (should we act?)
```

### Structure

```csharp
// 1. Data shape — minimal headers via primary constructor record
public sealed record GmailHeaders(
    string From, string Subject, DateTimeOffset Date,
    string? InReplyTo, string? References, string To, string MessageId
);

// 2. Injectable configuration — keeps domain lists out of logic
public sealed record RelevanceConfig(
    HashSet<string> KnownAtsDomains,
    IReadOnlyList<string> LinkedInSenderPatterns
);

// 3. Static stages — pure functions, Guard at entry
public static class EmailRelevanceFilter
{
    public static bool IsRelevant(GmailHeaders headers, RelevanceConfig config, IReadOnlyList<string> knownContactEmails)
    {
        Guard.IsNotNull(headers);
        Guard.IsNotNull(config);
        // ...pattern matching...
    }
}

public static class SignalCorrelator
{
    public static string? Correlate(GmailHeaders headers, IReadOnlyList<Application> activeApplications)
    {
        Guard.IsNotNull(headers);
        // ...recipient alias, channel email, domain matching...
    }
}

public static class CheapClassifier
{
    public static SignalClassification? Classify(GmailHeaders headers, string? bodySnippet)
    {
        Guard.IsNotNull(headers);
        // Returns null when uncertain (defers to LLM)
    }
}
```

### Conventions

| Convention | Rationale |
|---|---|
| Static classes for stages | No state, no DI — pure function signatures self-document the pipeline |
| `IReadOnlyList<string>` for external inputs | Immutable, no hidden mutation |
| `Record` for injectable config | Config is data, not behavior; inject via DI or parameter |
| `null` return for "uncertain" | Signals "defer to more expensive classifier" (LLM, human) |
| `Guard.IsNotNull` at every entry | Fail fast on contract violation |
| Primary constructor records for data shapes | Concise for multi-field DTOs with no behavior |

### Testing the Pipeline

Three test classes plus an integration test:
- **Per-stage tests** (RelevanceFilter, Correlator, Classifier) — focused unit tests for each stage
- **Integration test** — exercises filter → correlate → classify end-to-end with realistic data

For the full classification pipeline test patterns, see `references/deterministic-classification-pipeline.md`.

### LLM Fallback Classifier

When `CheapClassifier.Classify()` returns `null` (uncertain), the pipeline can fall back to an LLM-based classifier. The LLM classifier is an Api-layer class (not Domain — it depends on `ILlmClientFactory`) that:

1. **Budget-gates** via `ILlmBudgetGuard` — throws `LlmBudgetExceededException` on `Deny`
2. **Builds a minimal payload** — truncates raw excerpt (max ~2000 chars) for data minimization
3. **Calls `ILlmClient.CompleteJsonAsync<T>`** with `ModelTier.Cheap` (classification is a straightforward categorization task)
4. **Maps the response** to `SignalClassification` — high confidence (≥ threshold) → auto-apply candidate; low confidence → proposal

**Key design decision:** cost tracking (`ILlmCostTracker`) is handled transparently by the infrastructure-layer `ILlmClient` decorator, NOT by the classifier itself. The classifier only uses the correct `StepType` constant so the decorator can tag the cost record. Do not inject `ILlmCostTracker` into the classifier.

Constructor dependencies:
```csharp
public sealed class LlmEmailClassifier(
    ILlmClientFactory llmClientFactory,
    IPromptProvider promptProvider,
    ILlmBudgetGuard budgetGuard)
```

For the full implementation pattern, FakeLlmClient test setup, schema contracts, and budget guard testing, see `references/llm-classifier-integration.md`.

### Platform-Specific Signal Parsers

When adding a new platform (LinkedIn, Indeed, etc.) that sends email notifications, implement a **platform signal parser** as a new stage in the pipeline. The parser is a static class in `ChannelMonitoring/{Platform}/` with an intermediate domain record (not persisted) and a mapper to the shared `ChannelSignal` aggregate. Key decisions: sender whitelist returns `null` for non-matching emails (vs `Unknown` for unrecognized patterns from known senders), subject-first regex (never combine subject + body), deterministic ExternalId from platform identifiers, and deterministic classification only for 100%-certain event types.

For the full implementation sequence, test recipes, and pitfalls, see `references/platform-signal-parser.md`.

### Signal Correlation & Bootstrap Import

After implementing a platform signal parser, two additional domain concerns typically emerge:

1. **SignalCorrelator** — three-tier waterfall (exact ID → fuzzy company+title → no match) that matches inbound signals to existing applications. Never guesses on ambiguity (multiple candidates → return None).
2. **BootstrapImporter** — bulk-creates Application + JobOffer records from platform data exports (e.g., LinkedIn DMA Member Snapshot). Uses DryRun (pure classification) + Import (with delegate injection for domain purity). Dedup chain: offer URL → existing app → existing signal.

For the full pattern, result types, Application factory method, test matrices (30+ tests), and pitfalls, see `references/signal-correlation-and-bootstrap-import.md`.

## Ingest Wiring Pattern (Transport → Repository with Cursor Durability)

When wiring a transport (`IGmailTransport`) to a repository (`IChannelSignalRepository`) for incremental data ingestion with cursor-based pagination.

### Architecture

```
Timer trigger → Durable Task orchestration → Activity (core logic)
                                                ↓
                                     IGmailTransport.FetchMessagesSinceAsync(cursor)
                                                ↓
                                     Dedup by ExternalId
                                                ↓
                                     IChannelSignalRepository.UpsertAsync (per signal)
                                                ↓
                                     IGmailIngestCursorRepository.Save (ONLY after all succeed)
```

### Key Decisions

| Decision | Rationale |
|---|---|
| Cursor advances only after entire batch persists | Crash mid-batch → re-read, not skip |
| Dedup by `(Source, ExternalId)` before persist | At-least-once delivery without double-creates |
| Durable Task orchestration as thin wrapper | `ScheduleWithConcurrencyGuardAsync` prevents double-runs |
| Activity class has public `IngestAsync` method | Testable without Durable Task context mocking |
| Separate `IGmailIngestCursorRepository` | Cursor is orthogonal to signal persistence |

### Interface Design

```csharp
// Domain layer — cursor store
public interface IGmailIngestCursorRepository
{
    Task<string?> GetLastProcessedHistoryIdAsync(string userId, CancellationToken ct);
    Task SaveLastProcessedHistoryIdAsync(string userId, string historyId, CancellationToken ct);
}

// Add to existing IChannelSignalRepository for dedup
Task<ChannelSignal?> GetByExternalIdAsync(string userId, string source, string externalId, CancellationToken ct);
```

### Implementation Pattern

```csharp
// Activity — contains testable core logic
public sealed partial class GmailIngestActivity(
    IGmailTransport transport,
    IChannelSignalRepository signalRepo,
    IGmailIngestCursorRepository cursorRepo,
    ILogger<GmailIngestActivity> logger)
{
    public async Task<GmailIngestOutcome> IngestAsync(string userId, CancellationToken ct)
    {
        var cursor = await cursorRepo.GetLastProcessedHistoryIdAsync(userId, ct);
        var messages = await transport.FetchMessagesSinceAsync(userId, cursor, ct);
        var latestHistoryId = await transport.GetLatestHistoryIdAsync(userId, ct);

        if (messages.Count == 0 || latestHistoryId is null)
            return new GmailIngestOutcome(0, cursor);

        var unique = messages.GroupBy(m => m.MessageId).Select(g => g.First()).ToList();
        int persistedCount = 0;

        foreach (var msg in unique)
        {
            var signal = MapToSignal(userId, msg);
            var existing = await signalRepo.GetByExternalIdAsync(userId, signal.Source, signal.ExternalId, ct);
            if (existing is not null) continue;
            await signalRepo.UpsertAsync(signal, ct);
            persistedCount++;
        }

        // ONLY reached if all persists succeeded — cursor stays put on exception
        await cursorRepo.SaveLastProcessedHistoryIdAsync(userId, latestHistoryId, ct);
        return new GmailIngestOutcome(persistedCount, latestHistoryId);
    }
}

// Durable Task orchestration — thin wrapper for ScheduleWithConcurrencyGuardAsync
[UsedImplicitly]
public static partial class GmailIngestOrchestration
{
    public const string Name = nameof(GmailIngestOrchestration);

    [Function(Name)]
    public static async Task<GmailIngestOutcome> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<GmailIngestOrchestrationInput>() ?? throw;
        return await context.CallActivityAsync<GmailIngestOutcome>(
            nameof(GmailIngestActivity), input);
    }
}
```

### Test Categories (minimum)

| Test | What it proves |
|---|---|
| At-least-once: run twice, same messages → no duplicates | Dedup by ExternalId |
| Mid-batch failure: UpsertAsync throws on Nth call → cursor NOT advanced | Crash safety |
| Mid-batch failure: already-persisted signals preserved | First N signals survive |
| Cursor advances after successful persist | Happy path |
| Second run advances cursor from previous position | Incremental ingestion |
| Empty inbox → no cursor advance | Edge case |
| Passes stored cursor to transport | Watermark passthrough |
| Passes null cursor for initial sync | First-run behavior |
| Dedup within same batch (duplicate messages) | GroupBy dedup |

### Pitfalls

| Pitfall | Fix |
|---|---|
| `GetByExternalIdAsync` not on `IChannelSignalRepository` | Add it — also update Cosmos + InMemory implementations + contract tests |
| Activity not testable because it's inside static Durable Task orchestration | Extract core logic into instance-based activity class with public `IngestAsync` |
| Cursor advanced before all signals persisted | Put `SaveLastProcessedHistoryIdAsync` AFTER the foreach loop, not inside |
| `FakeGmailTransport` only in one test project | Move to shared Testing project + add Infrastructure project reference to Testing.csproj |

## Infrastructure Adapter Pattern (External Services)

When implementing a new external-service integration (Gmail API, LinkedIn, etc.), follow the full 7-step sequence in `references/infrastructure-adapter-pattern.md`. Covers: transport DTO + interface, Fake transport, TDD cycle, monitor implementation, token refresher with deterministic intervention IDs, high-performance logging, and deduplication.

Key distinction from Cosmos persistence: the transport interface lives in Infrastructure (not Domain), Domain only sees the extension-point interface (`IChannelMonitor`), and the adapter maps external wire types to domain signals.

## Cosmos Persistence (Infrastructure Layer)

When implementing the Cosmos repository for a domain entity, follow the full 9-step sequence in `references/cosmos-persistence-implementation.md`. Covers: contract test suite, InMemory fake, CosmosOptions, Cosmos repository, DI, Terraform container, and the easily-forgotten ProvisionCosmosEmulator update.

Two sub-patterns for specialized cases (documented in the same reference):
- **Encrypted document** — when the entity contains sensitive data (API keys, compensation). Entire document encrypted via `ISecretCipher` before persisting; Cosmos stores a wrapper with `EncryptedSecret`. Test with ephemeral `DataProtectionProvider.Create("scope")`.
- **Optimistic concurrency** — when the contract uses `VersionedDocument<T>` (entity + ETag). Uses CreateItemAsync/ReplaceItemAsync with ETag guards and `ConcurrencyConflictException` on conflicts.
- **Simple config document** — when the entity is a single per-user config (userId = id = partition key). Uses ReadItemAsync + UpsertItemAsync with no concurrency. See `references/cosmos-persistence-implementation.md`.
- **Wildcard-ETag upsert** — when `UpsertAsync(entity, etag)` supports `"*"` for blind upsert and real ETags for conditional replace. Uses UpsertItemAsync with optional `IfMatchEtag`. See `references/cosmos-persistence-implementation.md`.

## High-Performance Logging (Project Convention)

Every Infrastructure class that logs uses the nested `static partial class Log` pattern with `[LoggerMessage]` source generators. This avoids boxing allocations and string interpolation in hot paths.

```csharp
public sealed partial class MyChannelMonitor(...) : IMyMonitor
{
    // ... implementation methods ...

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Fetching messages for user {UserId} with watermark {Watermark}")]
        public static partial void FetchingMessages(ILogger logger, string userId, string? watermark);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Transport returned {RawCount} messages, {UniqueCount} unique for user {UserId}")]
        public static partial void TransportReturned(ILogger logger, int rawCount, int uniqueCount, string userId);
    }
}
```

**Conventions:**
- Outer class must be `partial` (required by source generator)
- Nested class: `private static partial class Log` — always named `Log`
- Sequential `EventId` starting at 1 within each class
- Never log tokens, message bodies, or PII — log IDs, counts, outcomes only
- `ILogger` passed as first parameter (not captured from outer scope)

## Common File Layout

### Domain + Infrastructure + Tests
src/MyApp.Domain/
  Feature/
    MyAggregate.cs          # sealed record with behavior
    MyStatus.cs             # enum
    MyClassification.cs     # value object record
    MyPolicy.cs             # standalone policy (sealed class)
    MyPolicyOptions.cs      # options record for configurable thresholds
    MyDecision.cs           # decision record + decision-type enum
    IMyRepository.cs        # extension point interface
    IMyMonitor.cs           # extension point interface
    MyStepTypes.cs          # step type contracts (if workflow)
    MyInterventionSource.cs # constants for intervention sources

src/MyApp.Infrastructure/
  Feature/
    MyTransport.cs          # transport DTO (sealed record, infra-only)
    IMyTransport.cs         # transport boundary interface (infra-only)
    MyChannelMonitor.cs     # implements IMyMonitor, uses IMyTransport
    MyTokenRefresher.cs     # token lifecycle, raises intervention signals

tests/MyApp.Domain.Tests/
  Feature/
    MyAggregateTests.cs     # behavior tests
    MyPolicyTests.cs        # policy decision tests
    MyIntegrationTests.cs   # cross-aggregate tests (e.g. intervention sources)

tests/MyApp.Infrastructure.Tests/
  Feature/
    FakeMyTransport.cs      # test double for IMyTransport
    MyChannelMonitorTests.cs
    MyTokenRefresherTests.cs
```

## Intervention Sources

When a domain feature raises interventions on another aggregate:

```csharp
// 1. Define the constant
public static class ApplicationInterventionSource
{
    public const string ChannelMonitoring = "channelMonitoring";
}

// 2. Register in the aggregate's LocalInterventionSources
protected override HashSet<string> LocalInterventionSources { get; } =
    [..existing, ApplicationInterventionSource.ChannelMonitoring];

// 3. Test both raise and clear
[Fact]
public void RequireIntervention_from_channelMonitoring_succeeds() { ... }
[Fact]
public void ClearIntervention_from_channelMonitoring_succeeds() { ... }
```

## Exception → ProblemDetails Wiring

When domain exceptions need HTTP error responses, each exception requires a **triple** of changes in `DomainExceptionProblemMapper`:

### 1. Add a problem-type constant

```csharp
private const string PracticeSessionNotFoundType = "https://tools.ietf.org/html/rfc7231#section-6.5.4";
private const string InvalidPracticeSessionTransitionType = "https://job-search-ai-assistant.dev/problems/invalid-practice-session-transition";
```

Convention: use the RFC 7231 URL for standard HTTP statuses (404, 409), or the project's own domain for domain-specific problems.

### 2. Add a switch-case entry

```csharp
PracticeSessionNotFoundException e => MapPracticeSessionNotFound(e),
```

### 3. Add the mapping method

```csharp
private static ProblemDetails MapPracticeSessionNotFound(PracticeSessionNotFoundException exception)
{
    var problem = new ProblemDetails
    {
        Type = PracticeSessionNotFoundType,
        Title = "Practice Session Not Found",
        Status = StatusCodes.Status404NotFound,
        Detail = exception.Message,
        Extensions = { ["applicationId"] = exception.ApplicationId, ["sessionId"] = exception.SessionId }
    };
    return problem;
}
```

Extension properties should expose every discriminator field the client needs to identify the error (IDs, status values, counts).

### Pitfall: WriteProblemAsync returns void

`HttpContext.WriteProblemAsync(problemDetails, ct)` writes the response but returns `void`, not `HttpResponse`. In Functions that must return `HttpResponse`:

```csharp
// ❌ Won't compile — void can't be returned as HttpResponse
return await req.HttpContext.WriteProblemAsync(problem, ct);

// ✅
await req.HttpContext.WriteProblemAsync(problem, ct);
return req.HttpContext.Response;
```

## Testing: Lifecycle Completeness Matrix

When testing state machine transitions, build a **transition matrix** to ensure
every invalid path is covered. For N states with M transition methods, you need
`N × M` tests total (one per cell).

**Example — 3 states (Proposed, Applied, Dismissed), 2 transition methods:**

| Source state → | Dismiss() | Apply() |
|---|---|---|
| Proposed | ✅ succeeds | ✅ succeeds |
| Applied | ❌ throws | ❌ throws |
| Dismissed | ❌ throws | ❌ throws |

That's **6 tests** minimum. A common mistake is writing 5 — testing
`Dismiss_from_Applied_throws` then forgetting `Apply_from_Applied_throws`
because "it's the same guard clause." Each cell is an independent test:

```csharp
// Dismiss: all 3 states
[Fact] public void Dismiss_from_Proposed_succeeds() { ... }
[Fact] public void Dismiss_from_Applied_throws() { ... }
[Fact] public void Dismiss_from_Dismissed_throws() { ... }

// Apply: all 3 states — DON'T skip Applied just because Dismiss tested it
[Fact] public void Apply_from_Proposed_succeeds() { ... }
[Fact] public void Apply_from_Applied_throws() { ... }   // ← easy to forget
[Fact] public void Apply_from_Dismissed_throws() { ... }
```

## Pitfalls

| Pitfall | Fix |
|---|---|
| `Guard.IsEqualTo` fails with nullable/enum types | Use `if` + `ThrowHelper.ThrowInvalidOperationException` — see `references/communitytoolkit-guard-pitfalls.md` |
| Forgetting to register new intervention source in `LocalInterventionSources` | Always update both the constant class AND the HashSet |
| Adding infra dependencies to Domain project | Domain purity test catches this — check with `dotnet test --filter "DomainPurity"` |
| Entity types placed in Api layer when repository interfaces are in Domain | Domain has no ProjectReference to Api — so `Domain.Persistence.IFooRepository` can't return a type from `Api.FooFeature`. If the spec says "put entity in Api", override it: persisted entities always go in Domain. Api owns only functions, DTOs, request/response types, and orchestrations. Detection: `CS0246` or `CS8625` on a repository method's return type. |
| InternalsVisibleTo missing for Domain → Domain.Tests | The Domain project does NOT have `<InternalsVisibleTo>` by default. When implementing `internal` methods (e.g., `ExtractJobId`) that tests need to call, add `<InternalsVisibleTo Include="JobSearchAiAssistant.Domain.Tests"/>` to `src/JobSearchAiAssistant.Domain/JobSearchAiAssistant.Domain.csproj`. Without it, tests get `CS0117: 'Type' does not contain a definition for 'Method'`. |
| Shouldly `ShouldContain` on `string?` properties (CS8604) | When testing nullable string properties like `SkipReason` or `Note` with `.ShouldContain(...)`, the C# analyzer flags CS8604 (possible null reference). Fix: use the null-forgiving operator: `.SkipReason!.ShouldContain(...)`. The test assertion itself IS the null check — if SkipReason were null, the test would fail before Shouldly even runs. |
| Nested collection mutation in sealed records with IReadOnlyList | When a record has `IReadOnlyList<T> Entries` and you need to replace one item by predicate, use index-based replacement: `var list = proposal.Entries.ToList(); list[index] = mutate(entry); return proposal with { Entries = list };`. Extract a `MutateEntry(key, Func<T,T>)` helper when 3+ mutation methods need it (accept/edit/reject). Don't use `.Select()` with conditional — it's less readable and harder to guard preconditions. |
| Mutable record methods | Every method must `return this with { ... }` — never modify in place |
| Missing `required` on constructor-like properties | Use `required` keyword, not `= null!` |
| `Array.IndexOf` returns -1 for states not on the forward path (Failed, Declined) | Guard negative indices before comparing ordinals — return `false` to mean "not comparable" |
| Incomplete lifecycle transition tests | Build a transition matrix; test every cell, not just one per method |
| Staged index diverges from working tree | `git diff main` shows working tree vs main; `git diff` (no args) shows index vs working tree. Both must match before committing — staged version is what gets committed |
| `Guid.NewGuid()` in Durable Functions orchestrator | Use `context.NewGuid()` — see `references/durable-functions-orchestration-pitfalls.md` |
| `DateTimeOffset.UtcNow` / `DateTime.UtcNow` in services | Inject `TimeProvider` (BCL, .NET 8+) as a ctor dependency; register `services.AddSingleton(TimeProvider.System)` in DI; use `_time.GetUtcNow()` everywhere. Reviews will ask for this — it makes clock access deterministic and testable. Tests inject `FakeTimeProvider` from the official `Microsoft.Extensions.TimeProvider.Testing` package (set `new FakeTimeProvider(fixedInstant)`, `SetUtcNow(...)` to advance). |
| Hand-rolled test doubles for clock/logging | User preference (explicit `f:` correction): use the OFFICIAL Microsoft packages instead — `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) and `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger<T>`/`FakeLogger`/`FakeLogCollector`, assert `Collector.LatestRecord.Level/Id.Id/Message`). They're in the test-scope `tests/Directory.Packages.props` under Central Package Management. `FakeLoggerFactory` is NOT in Diagnostics.Testing (it's in the separate `Microsoft.Extensions.Logging.Testing` package) — use `new FakeLogger()` or `new FakeLogger<T>()` directly. |
| Entity/workspace ids via `Guid.NewGuid()` | User preference: use **sortable v7 guids** — `Guid.CreateVersion7()` (net9+) for ids that get listed or ordered. v7 embeds a timestamp, so lists sort deterministically by creation order without a separate CreatedAt sort. `Guid.NewGuid()` (v4) is random — fine for true uniqueness, wrong when ordering matters. Workspace ids, session ids, and any "list by recency" entity are v7 candidates. |
| Transport interface placed in Domain layer | Keep it in Infrastructure — Domain only owns the extension-point interface (`IChannelMonitor`) |
| Sync-over-async for middleware contracts | When an interface method is synchronous by design (e.g., `IPrincipalAllowlist.IsAllowed` runs on every HTTP request in middleware) but the backing store is async (Cosmos), use `IMemoryCache` with a pre-loaded dictionary: load all entries on cache miss (acceptable for tiny datasets <100), cache for 60s, subsequent calls are pure in-memory. Document the trade-off in XML remarks. Alternative: change the interface to async (bigger refactor, touches middleware + all callers). |
| Monitor registered unconditionally when transport is optional | When a monitor depends on a transport that requires OAuth/credentials and may not be registered, guard DI: `if (services.Any(d => d.ServiceType == typeof(IGmailTransport))) { services.AddSingleton<IChannelMonitor, LinkedInChannelMonitor>(); }`. Without guard, `ValidateOnBuild` fails. See `references/infrastructure-adapter-pattern.md`. |
| Logging message bodies or tokens in transport adapter | Log IDs, counts, outcomes only — never tokens, bodies, or PII |
| Financial rounding with `Math.Round(value, 2)` default | Default `MidpointRounding.ToEven` rounds 94.3884 → 94.39. For payroll/tax/financial domain models, use `MidpointRounding.ToZero` (truncation toward zero): 94.3884 → 94.38. Always specify explicitly; never rely on the default for money. Define helpers: `static decimal RoundGrosze(decimal v) => Math.Round(v, 2, MidpointRounding.ToZero);` and `static decimal RoundZloty(decimal v) => Math.Floor(v);`. |
| Annual-threshold tax computed as monthly flat | Progressive PIT with annual thresholds (e.g. 120k PLN) changes net mid-year. Compute annual totals then divide by 12 for a deterministic monthly average. Month-by-month projection is more accurate but far more complex — start with annual-average, add projection as a follow-up. |
| TDD "stubs" for data-heavy domain models | When building a domain where records/enums ARE the data model (rate tables, profiles, result types), creating `NotImplementedException` stubs for records is awkward — records have no behavior to stub. Instead: (1) create all record/enum/interface files with their full shape, (2) create the service/calculator class with `throw new NotImplementedException()` in the method body, (3) write tests, (4) implement. The records compile on first pass; only the service class is stubbed. |
| Computed record properties drop out of equality | Expression-bodied properties (`public string Context => ContextNaming.WorkspaceContext(Id)`) have no backing field, so the synthesized `Equals`/`GetHashCode` ignore them. A derived property is therefore safe inside a value-equality record; a stored auto-property IS compared. |
| Constructor-validation on records | Use an explicit constructor + get-only auto-properties with guards when a record must validate at construction (blank ids, range limits). Positional primary-ctor + same-signature chaining ctor is easy to get wrong, and `required init` cannot validate. See "Constructor-Validated Records" section and `references/pure-domain-project-scaffolding.md`. |
| Sibling agent edits unrelated files in a shared worktree mid-task | `git status --short` at start AND end of the task. Unexpected diffs outside your scope (docs/spec files) are another agent's concurrent work — leave them, report them, never revert. Only re-read files a sibling actually touched before rebuilding. |
| Concurrent process commits/stages mid-task (not just edits) | Stronger case: a parallel session can land commits AND staged renames while you work — files change under you between reads, and `git commit -A` sweeps their half-done work into yours (and can commit a broken tree). Defenses: (1) **commit a baseline of the uncommitted tree at task start** so your own commits stay separable; (2) when a patch fails with "file modified since last read", re-read fresh and check `git reflog`/`git status` for concurrent commits BEFORE retrying — the file may have been legitimately rewritten (e.g. `git show HEAD:<file>` to see what a concurrent refactor did); (3) commit YOUR files with explicit paths (`git commit --only <paths>`) so another process's staged renames stay out of your commit; (4) if the concurrent refactor is half-applied and breaks the build, surface it to the user and let them decide (finish it / build on top / commit only yours) — do not silently finish or revert their work. |
| Non-deterministic intervention IDs | Use SHA-256 hash of (userId, interventionType) → Guid for upsert idempotency |
| Forgetting dedup on external API results | Always `GroupBy(ExternalId).Select(First())` before mapping to domain signals |
| Subagent forgets to commit files in worktree | Always `git status --short` after subagent completion; commit untracked files yourself |
| Subagent forgets to add config properties (CosmosOptions, DI) | Verify build in MERGED state, not just worktree — isolated builds can miss missing registrations |
| DI registration layer mismatch after replacing implementation | When replacing a simple Domain implementation (e.g., `SingleUserAllowlist`) registered in `ApiDependencies` with an Infrastructure implementation (e.g., `CosmosBetaAllowlist` that depends on `IBetaAllowlistRepository` + `IMemoryCache`), **move** the `IPrincipalAllowlist` registration from `ApiDependencies` to `InfrastructureDependencies`. Don't leave both — last-wins DI semantics silently hide the old registration, but it's confusing and the old class gets instantiated needlessly. |
| Subagent doesn't push branch | Instruct explicitly: push your branch before finishing — user can't follow progress otherwise |
| Sibling subagent corrupts file with cross-layer references | When multiple agents edit the same file in parallel, a sibling may inject using directives or registrations that reference layers the project can't see (e.g., Api namespace in Infrastructure project). Always re-read the file after a sibling warning and verify no cross-layer references were introduced before rebuilding. Detection: CS0234 in Infrastructure project referencing Api namespace. |
| camelCase enum values in JSON assertions
| `Response.Body` not writable/seekable in Azure Functions tests | `DefaultHttpContext` defaults to `NullStream` for `Response.Body`. Set `Response = { Body = new MemoryStream() }` in the test setup. Reset `Position = 0` before reading response body. |
| `ThrowsAsync` on abstract DurableTaskClient methods | NSubstitute can't intercept `.ThrowsAsync()` on `Task<T>` returns from abstract methods. Use `.Returns(Task.FromException<T>(ex))` instead — see `references/durable-functions-testing-patterns.md` |
| `Arg.Any<string>()` for `TaskName` parameter | `ScheduleNewOrchestrationInstanceAsync` takes `TaskName`, not `string`. Use `Arg.Any<TaskName>()` |
| CS8072 in `Arg.Is` lambda with `?.` | Expression tree lambdas can't use null-propagating operator. Use `o != null && o.Prop == val` |
| `StartOrchestrationOptions` missing using | It's in `Microsoft.DurableTask` (Abstractions), not `Microsoft.DurableTask.Client`. Both usings needed. |
| Concurrency gate TOCTOU (check-then-schedule) | Use `ScheduleWithConcurrencyGuardAsync` — schedule first, verify on `OrchestrationAlreadyExistsException` — see `references/durable-functions-orchestration-pitfalls.md` |
| Locale-sensitive double in xUnit `[InlineData]` | When injecting `[InlineData(1.1)]` doubles into JSON strings via `$"{value}"`, the system locale may format `1.1` as `1,1` (comma decimal separator), breaking `JsonDocument.Parse`. Always use `value.ToString(CultureInfo.InvariantCulture)` for the interpolation. |
| Locale-sensitive double in Shouldly `ShouldContain` | `decision.Reason.ShouldContain("0.50")` fails when the system locale formats doubles with comma (`"0,50"`). The runtime formats via `string.Format($"Confidence {confidence:F2}")` using current culture. Fix: assert on semantic content (`ShouldContain("below threshold")`) instead of formatted numbers. |
| Existing test breaks when expanding enum/schema values | When adding new values to an enum (e.g. expanding a JSON schema `transitionTo` enum), existing tests that used a now-valid value as the "invalid" test case will break. Before expanding, grep for tests like `Schema_rejects_an_invalid_transition_value` that substitute a value you're about to add. Update them to use a truly nonexistent value (e.g. `"NonExistentState"`). |
| Overly broad patterns in rule-based classifier | When adding keyword patterns to a `CheapClassifier` or similar rule-based classifier, always re-run the negative/uncertain test cases after adding each pattern. Generic phrases like `"available for a call"` match too broadly and break "uncertain returns null" tests. Prefer specific phrases (`"schedule an interview"`) over conversational fragments. |
| C# interpolated string `$"\b"` is backspace, NOT regex word boundary | In C# non-verbatim interpolated strings (`$"..."`), `\b` is the backspace escape character (U+0008), NOT the regex `\b` word boundary. So `$"\b{keyword}\b"` produces a pattern with literal backspace chars that never matches. **Fix:** use `$"\\b{keyword}\\b"` (double backslash) for literal `\b` that the regex engine interprets as word boundary. In raw file bytes: `5c62` = single backslash (broken), `5c5c62` = double backslash (correct). Detection: regex with `\b` compiles and runs but silently matches nothing — no exception, no warning. **Quick diagnostic:** use `xxd` on the `.cs` file to check raw bytes at the `$"\b"` position. This is a DIFFERENT issue from .NET's Unicode word-boundary behavior (which is the next pitfall). |
| .NET `Regex \b` word boundary not matching at string start | In C#, `\\b` in `Regex.Matches(text, @"\\bVP\\b")` (verbatim string) correctly produces the regex `\b`, but .NET's word-boundary rules use Unicode categories that differ from PCRE. `\bVP\b` may not match "VP" at string boundaries in .NET when Python/JS match on the same input. **Fix:** replace `\\b` with `string.Contains(word, OrdinalIgnoreCase)` + manual word-boundary check, or use `string.IndexOf` for position extraction. Don't spend multiple iterations tweaking regex patterns — switch to string methods after the first `\\b` miss. |
| Shouldly `ShouldContain(predicate)` shows no detail on failure | When `collection.ShouldContain(x => x.Prop.Contains("X"))` fails, Shouldly reports the predicate but not the actual values in the collection. **Fix:** add a temporary `Assert.Fail` that dumps all actual values: `Assert.Fail($"Actual: {string.Join("; ", items.Select(i => i.Prop))}")`. Remove after fixing. This one-line diagnostic saves multiple blind fix cycles. |
| Nullable enum JSON schema via `JsonSchema.Net.Generation` | The generator wraps nullable enums in `oneOf: [string-enum, null]`. Tests must navigate `GetProperty("oneOf").EnumerateArray().Where(e => e.TryGetProperty("enum", out _))` to access enum values — trying `.EnumerateArray()` directly on the property throws `InvalidOperationException` (it's an object, not an array). For nullable enums with `JsonStringEnumMemberName`, prefer hand-composing the schema (like `EmailClassificationSchemas`) over auto-generation for predictable output. |
| Injecting `ILlmCostTracker` into LLM-calling classes | Cost tracking is handled by the infrastructure-layer `ILlmCostTracker` decorator that wraps `ILlmClient`. Classifiers and orchestrators do NOT need to inject `ILlmCostTracker` — they just use the correct `StepType` string so the decorator can tag the ledger record. Only inject `ILlmBudgetGuard` for pre-call budget checks. |

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

When an operation needs user review before committing writes, use two separate orchestrations with separate instance IDs. See `references/durable-functions-orchestration-pitfalls.md` → "Two-Phase Orchestration Pattern" for the full architecture, instance ID discipline, precondition checks, and partial failure handling.
