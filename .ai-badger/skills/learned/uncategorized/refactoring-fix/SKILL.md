---
name: refactoring-fix
description: "Triaging and fixing build/test failures after a refactor."
version: 1.0.0
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [refactoring, debugging, triage, csharp, dotnet, angular, javascript, typescript]
    related_skills: [systematic-debugging, test-driven-development]
---

# Refactoring Fix

When a large refactor produces hundreds of build errors or test failures, batch fixes
and global find-and-replace are dangerous. Load `systematic-debugging` first for the
core debugging discipline. This skill extends it for the high-volume case.

## Core Rule: Categorize Before You Fix

**Never fire off bulk find-and-replace when facing 50+ errors.** Each error sits in a
specific context — a `with` expression is not a property access is not a DTO mapping.
Replacing a symbol globally will break contexts you haven't examined.

### Step 1: Group by error code and symbol

Run `dotnet build` or `dotnet test`, then pipe through:

```
dotnet build 2>&1 | grep "error CS" | awk -F': error CS' '{print $2}' | awk -F': ' '{print $1}' | sort | uniq -c | sort -rn
```

This gives counts per error code. Drill into each code:

```
dotnet build 2>&1 | grep "error CS0117" | head -10
```

Group further by the specific symbol name inside each code. Produce a markdown table:

| Category | Error Code | Count | Root Cause Hypothesis |
|---|---|---|---|
| `InterventionRequired` not found | CS0117 | 130 | Property moved from Application to WorkflowContainer |
| StepType/string swap | CS1503 | 106 | CreateAutomatic param order changed |
| `SaveApplicationActivityInput` | CS0246 | 30 | Type renamed to WorkflowContainerSaveInput<T> |

### Step 2: Present categories to the user BEFORE fixing (MANDATORY)

**Do not skip this step for any non-trivial error set (5+ distinct error codes).**
Write the analysis to a markdown file and open it in their editor for review.
For C# projects in Rider:

```bash
write_file(path=".tmp/test-failure-analysis.md", content="...")
open_file_in_editor(filePath=".tmp/test-failure-analysis.md")
```

Each category needs: the error count, the probable root cause with reasoning, the files
affected, and the proposed fix approach. The user can confirm, re-rank, or correct your
analysis before you invest time fixing. Diving into bulk replacements without this step
is the #1 cause of cascading regressions in high-volume triage sessions.

Example format:

| Category | Error Code | Count | Root Cause | Files |
|---|---|---|---|---|
| InterventionRequired not found | CS0117 | 130 | Property moved to WorkflowContainer | ApplicationTests.cs, ... |
| StepType/string swap | CS1503 | 106 | CreateAutomatic param order | OfferStepFunctionsTests.cs, ... |

### Step 3: Fix one category at a time

- Pick the smallest, most mechanical category first.
- Verify with `dotnet build` after each category — confirm the count dropped by exactly the expected amount.
- If counts don't match, stop and re-analyze.

## Pitfalls with C# Refactoring

### 1. Property on aggregate ≠ property on DTO
`.InterventionRequired` replaced globally will break `ApplicationListFilter.InterventionRequired`,
`ApplicationSummary.InterventionRequired`, and `ApplicationListItem.InterventionRequired`.
These DTOs kept flat properties while the domain aggregate switched to a nested `Intervention` record.
Only replace on domain types (Application, JobOffer), NOT on filter/response DTOs.

### 2. Object initializer syntax differs from property access
`with { InterventionRequired = true }` is an object initializer, not property access.
Replacing bare `InterventionRequired` here needs a different transformation than
replacing `.InterventionRequired`.

### 3. `nameof(T)` on generic type params resolves to the param name
`nameof(TSaveActivity)` in a generic method resolves to `"TSaveActivity"`, not the
concrete type. Durable Functions' source generator then can't find the activity.
Use `typeof(TSaveActivity).Name` instead for runtime resolution.

### 4. `using var` declarations look like `using` directives
Scripts that deduplicate directives by matching lines starting with `using ` will
silently delete `using var buffer = new MemoryStream();` from method bodies.
Filter directive dedup to only lines that are top-level using statements, e.g. by
checking that the line contains a namespace or type name, not `var`.

### 5. Global `ClearInterventionFrom()` without args gets wrong source
Methods whose signatures changed to require new parameters can't be fixed with a
single global replace. `ClearInterventionFrom()` → `ClearInterventionFrom("orch-1",
Source.CvGeneration)` hardcodes values that differ per test. Each call site needs
its own analysis to determine the correct orchestratorId and source.

### 6. `dotnet test --no-build` runs stale binaries
After modifying source files, `--no-build` tests run against the previously-compiled
binary. A fixed source file that still shows failures with `--no-build` means the
binary is stale — remove `--no-build` or run `dotnet build` first. This is
especially misleading when doing iterative test fixes: a test may appear to still
fail after fixing because the binary wasn't rebuilt.

### 7. Record `private init` blocks test `with` expressions
C# record properties with `private init` can only be set via `with` expressions from
within the declaring assembly. External test projects calling
`application with { PrivateInitProp = value }` will fail with CS0200. The fix is
either changing `private init` to `init` in the production code, or using domain
methods (`RequireIntervention()`) instead of direct `with`-expression state setup.

### 8. Namespace-type collision (CS0118)

When a C# type name collides with a namespace the file is in, the compiler treats the
identifier as the namespace, not the type. Common in this project:

```csharp
// In namespace JobSearchAiAssistant.Api.Applications.Salary:
var salary = new Salary { ... };  // CS0118: 'Salary' is a namespace but is used like a type
```

**Fix:** Use the fully-qualified domain type:
```csharp
var salary = new Domain.Compensation.Salary { ... };
```

Or add a `using Salary = JobSearchAiAssistant.Domain.Compensation.Salary;` alias at the top.
This is especially common when test files live under a namespace that matches a domain type name.

### 9. Don't assume record property names — check the actual definition

When writing test assertions against record types, **always read the record definition** to get
the exact property names. Common mismatches:

| Assumed | Actual | Record |
|---|---|---|
| `PersistedCount` | `PersistedSignalCount` | `GmailIngestOutcome` |
| `SignalCount` | `NewSignalCount` | `CheckChannelMonitoringActivityResult` |

Naming conventions vary between authors. A `record Foo(int Bar)` positional parameter is
accessible as `.Bar`, not `.Baz` even if another similar record uses `.Baz`. Check each one.

### 10. Required members in C# records (CS9035)

C# `required` properties must be set in every object initializer — the compiler enforces this:

```csharp
public sealed record GmailMessage
{
    public required string MessageId { get; init; }
    public required string ThreadId { get; init; }
    public required DateTimeOffset Date { get; init; }
    // ...
}

// CS9035: Required member 'ThreadId' must be set
new GmailMessage { MessageId = "1", From = "a@b.com" };
```

When constructing test data inline, **every `required` property must be present**. Use a helper
method (like `MakeMessage()`) to avoid repeating all required fields in every test.

### 11. Domain types need explicit `using` in API test projects

API test projects transitively reference Domain through the API project, but domain types
still need explicit `using` directives. The compiler does NOT auto-import transitive namespaces.

```csharp
// These all need explicit using even though they're accessible transitively:
using JobSearchAiAssistant.Domain.ChannelMonitoring;        // for ApplicationState, SignalClassification
using JobSearchAiAssistant.Domain.ChannelMonitoring.Classification; // for CheapClassifier, GmailHeaders
using JobSearchAiAssistant.Domain.Applications;              // for Application, ApplicationState
```

When writing new test files in the API test project, copy the `using` block from an existing
test file in the same directory to avoid this.

### 12. Record init-accessor with backing field for range validation

C# records with `init` properties can validate ranges using a backing field. This is the
idiomatic pattern for .NET 8+ / C# 12+:

```csharp
public sealed record SignalClassification
{
    private double _confidence;

    public double Confidence
    {
        get => _confidence;
        init
        {
            if (value is < 0.0 or > 1.0)
                throw new ArgumentOutOfRangeException(nameof(Confidence), value, "Must be in [0.0, 1.0].");
            _confidence = value;
        }
    }
}
```

Note: this changes the record's equality behavior slightly (backing field is used in Equals).
For records where equality matters, test that `Equals` still works correctly after adding
a backing field.

### 13. ThrowHelper generic variant for null-coalescing expressions

`ThrowHelper.ThrowInvalidOperationException<T>(message)` returns `T` and works in `??`
expressions. The non-generic `ThrowHelper.ThrowInvalidOperationException(message)` returns
`void` and cannot be used there.

```csharp
// Works — generic variant returns T
var input = context.GetInput<FooInput>() ??
    ThrowHelper.ThrowInvalidOperationException<FooInput>("Missing input");

// Does NOT compile — void return
var input = context.GetInput<FooInput>() ??
    ThrowHelper.ThrowInvalidOperationException("Missing input");
```

## Verifying Progress

After each category fix:
```bash
dotnet build 2>&1 | grep -c "error CS"   # should drop predictably
dotnet test --filter "RequiresInfra!=true" --no-build | grep "^Failed!"  # watch failure counts
```

If the error count goes UP or drops by the wrong amount, your replacement was too
broad. Restore from git and try a narrower approach.

## Verify a Claimed-Finished Refactor Before You Plan

When someone hands you a refactor commit labelled "finished" (a prior session, another agent, the user), do NOT plan on top of their word — the commit may not build, and the true breakage is larger than the first error suggests. Verify first, then scope.

1. **Build first.** `dotnet build` short-circuits at the first failing project: a single error in the lowest layer (e.g. an unread injected `ILogger` param, CS9113) hides every compile error in the projects above it (app, tests). One visible error ≠ one problem.
2. **Size the real breakage by grepping old-API call sites, not by building.** When the refactor changed shapes (ctor arity, interface methods, deleted constants), the build only reveals each project's errors once its dependencies compile. Enumerate call sites repo-wide in one pass with filter inversion — grep the symbol, then exclude the new shape so only old-shape sites remain:
   ```bash
   grep -rn "new SqliteConnectionFactory" --include="*.cs" | grep -v "IEncryptionKeyResolver"
   grep -rn "GetPassphrase()" --include="*.cs"   # 0-arg calls against a 1-arg interface
   grep -rn "SourceBitwarden" --include="*.cs"   # constants deleted by the refactor
   ```
3. **Fix shared test helpers first — they collapse the blast radius.** A single old-shape helper (e.g. `NullKeyProvider : IEncryptionKeyProvider` implementing the pre-refactor interface) is used by 10-20 test files. Grep for IMPLEMENTATIONS of the changed interface, not just call sites: one helper update plus one mechanical swap (`new NullKeyProvider()` → `new NullEncryptionKeyResolver()`) clears most files before any test body is touched.
4. **A test referencing a production class that doesn't exist is usually a botched rename, not a missing feature.** If the referenced name violates the project's naming conventions (e.g. `RunWithBitwardenSecretsManagerEncryptionKey` for what was `ConfigVerbRunner`), the test file was renamed wrongly during the refactor — rename the test back rather than naming production code after the test.
5. **Check entry-point dispatch for silently deleted behavior.** A refactor that compiles can still delete a code path (e.g. the `Program.cs` verb branch gone, so `app <verb>` now launches the server). Walk the entry point with each argument shape before declaring scope.

Then categorize per the Core Rule above and plan the real fix.

### 14. String constants vs `nameof()` for serialized values

`nameof(CvGeneration)` produces `"CvGeneration"` (PascalCase). When these values
are serialized to JSON and consumed by a frontend that uses camelCase
(`"cvGeneration"`), the mismatch causes serialization test failures and broken
frontend displays. For domain constants that appear in API responses, use
explicit camelCase strings (`public const string CvGeneration = "cvGeneration"`)
instead of `nameof()` to guarantee serialization shape matches the frontend's
convention.

**When the user corrects casing:** If the user says "we use lower letter first
everywhere" or similar, the fix is TWO-SIDED: (1) change the backend constants
to explicit camelCase strings, (2) ensure the frontend types/fixtures use the
SAME camelCase values, and (3) update serialization tests to expect camelCase.
Never fix only one side — backend PascalCase + frontend camelCase = broken
display at runtime even if tests pass. The domain serialization test
(`ApplicationSerializationTests`) is the authoritative place to pin the expected
serialization shape.

### 15. CS9113 on primary constructor parameters — `_` is NOT a discard

C# primary constructor parameters (C# 12+) cannot use `_` as a discard. The compiler
treats `_` as a real parameter name and still fires CS9113 ("Parameter '_' is unread"):

```csharp
// FAILS — CS9113 still fires
public sealed partial class Foo(BarService _, ILogger<Foo> logger) { }

// CORRECT — suppress at the type declaration
#pragma warning disable CS9113 // Parameter is unread — reserved for future use
public sealed partial class Foo(BarService serverDefaults, ILogger<Foo> logger) { }
```

Keep the original parameter name (not `_`) so the intent is clear when the parameter
is eventually used. Place `#pragma warning disable CS9113` on the line immediately
before the class declaration, with a trailing comment explaining why.

### 16. `JsonIgnoreCondition.WhenWritingNull` breaks test JSON assertions

When the API configures `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`
(which this project does in `ApiJsonOptions`), null properties are **omitted entirely**
from JSON responses — they don't appear as `null`, they're simply absent.

Tests that assert `body.GetProperty("foo").ValueKind.ShouldBe(JsonValueKind.Null)` will
throw `KeyNotFoundException` because the property doesn't exist in the JSON at all.

**Fix:** Use `TryGetProperty` to assert absence:
```csharp
// WRONG — throws KeyNotFoundException
body.GetProperty("schedule").ValueKind.ShouldBe(JsonValueKind.Null);

// CORRECT — property is absent when null
body.TryGetProperty("schedule", out _).ShouldBeFalse();
```

This pattern applies to every DTO test that round-trips nullable properties through
`WriteOkAsync` + `ReadBodyAsync`. Check `ApiJsonOptions` for the serialization config
before writing JSON assertion tests.

### 17. DurableTask `OrchestrationMetadata` has no `CompletedAt`

The `OrchestrationMetadata` type from `Microsoft.DurableTask.Client` does NOT have a
`CompletedAt` property. For completed orchestrations, use `LastUpdatedAt` instead — it
reflects the timestamp when the orchestration reached its terminal state:

```csharp
// WRONG — CS0117: 'OrchestrationMetadata' does not contain a definition for 'CompletedAt'
lastRunAt = metadata.CompletedAt;

// CORRECT
lastRunAt = metadata.LastUpdatedAt;
```

Common in monitoring/status endpoints that report "when did the last run finish."

### 18. Cross-check test fixes against ADRs and specs

After fixing tests to match production code, verify the fixes against the
project's architecture decision records (ADRs) and design specs to catch
unintended behavioral changes. Production code can deviate from the spec
during a refactoring, and blindly matching tests to code hides those deviations.

Look for:
- `docs/adr/` — numbered ADRs (e.g., `0010-intervention-ownership.md`)
- `docs/superpowers/specs/` — feature design specs
- Method signature changes that add filtering parameters not in the ADR table
  (e.g., `ClearInterventionFrom(source)` → `ClearInterventionFrom(orchestratorId, source)`)

Flag deviations explicitly: "Tests match code, but ADR says X. Intentional?"

## Angular / JavaScript / TypeScript Pitfalls

### 0. TypeScript test cleanup after removing exported types/constants/functions

When exported symbols are removed from a TypeScript module (type URIs, interfaces,
functions), test files that reference them break. The safe workflow is: classify
each reference (import, mock data, assertion, entire test block), fix one file at a
time with targeted patches, verify each file individually, then sweep for stale
references with a combined regex grep.

**Key insight:** A removed symbol may appear as test data in unrelated tests (e.g.,
a `budgetProblem` constant used in `stackedAnalysisRun`'s "returns undefined" test).
Always grep for the *constant name*, not just the removed type.

Full pattern library, pitfalls, and examples:
[`references/typescript-test-cleanup-after-type-removal.md`](references/typescript-test-cleanup-after-type-removal.md).

### 1. Don't run vitest/jest directly — use the framework's test command

Angular projects configure test runners through `ng test`, which injects test setup
globals (`describe`, `it`, `expect`, `beforeEach`) and zone.js polyfills. Running
`npx vitest run src/app/foo.spec.ts` directly **skips this setup** and fails with
`ReferenceError: describe is not defined`.

**Always use the project's configured test command:**
- Angular: `npm run test:single` (= `ng test --watch=false`)
- Never: `npx vitest run <files>` for Angular spec files

The same applies to Jest in React projects — `npx jest` may miss Babel transforms
or module mappers configured in `package.json`'s `"jest"` key or `jest.config.*`.
Always prefer `npm test` or the project's configured entry point.

### 2. Package.json duplicate keys when patching scripts

When replacing a script in `package.json` via the `patch` tool, ensure the old
entry is removed. A `patch` that adds `"build": "ng build && npm run generate:rss"`
without removing the existing `"build": "ng build"` creates **duplicate JSON keys**.
Node.js silently uses the last one, but the Angular compiler emits warnings on every
build. Fix: use a `replace` patch that targets the exact old line, or read the file
first and verify no duplicates after patching.

### 3. SPA routing rewrites for Vercel/Netlify

When adding client-side routing to an Angular SPA deployed on Vercel, add a
`rewrites` rule to `vercel.json`:

```json
{ "rewrites": [{ "source": "/(.*)", "destination": "/index.html" }] }
```

Without this, direct navigation to `/blog` returns 404 because Vercel looks for
a `/blog/index.html` file that doesn't exist in the static build output.

### 19. Enum values serialize as camelCase in API JSON assertions

When the API configures `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` (as
`ApiJsonOptions` does), ALL enum values serialize as lowercase in JSON responses.
Tests comparing raw JSON string values must use lowercase:

```csharp
// WRONG — PascalCase, will fail with camelCase serialization
root.GetProperty("status").GetString().ShouldBe("Accepted");
root.GetProperty("outcome").GetString().ShouldBe("Applied");

// CORRECT — matches camelCase serialization
root.GetProperty("status").GetString().ShouldBe("accepted");
root.GetProperty("outcome").GetString().ShouldBe("applied");
```

This applies to every enum: `ProposalEntryStatus`, `SinkOutcome`, `ProposalStatus`,
`SignalDisposition`, etc. When fixing build errors in a worktree with new test files,
check ALL `.ShouldBe("PascalCase")` assertions against enum-typed JSON properties.

Also applies to `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` attributes on
domain enums — the attribute-level converter's naming policy interacts with the
options-level converter. In .NET 9+, the attribute takes precedence, but if the
attribute uses the non-generic `JsonStringEnumConverter<T>()` constructor (no naming
policy), the options-level camelCase converter may still apply. **Always verify by
checking actual serialization output, not by reading the attribute alone.**

### 20. InMemory fake ETag `"*"` must handle both insert and update

When an `InMemory*Repository` fake implements optimistic concurrency via ETags,
the `"*"` (blind upsert) path must work for BOTH new inserts AND updates to existing
documents. A common bug:

```csharp
// BUGGY — "*"" only works when document already exists
if (etag == "*" && exists)
{
    // Blind update — always allowed.
}
else if (etag is null)
{
    if (exists)
        throw new ConcurrencyConflictException(proposal.Id, "<new>");
}

// CORRECT — "*"" works for insert or update
if (etag == "*")
{
    // Blind upsert — always allowed.
}
```

When changing test data setup from `null` etag to `"*"` (to fix CS8625 nullable
warnings), the fake must handle the `"*"` + not-exists case. If it doesn't,
tests fail with `ConcurrencyConflictException` on the first insert.

### 21. Domain mutation methods need context-appropriate status guards

When a domain model has multiple mutation methods that operate on entries at
different lifecycle stages, each method must have its own status guard matching
its entry point — NOT share a generic guard:

```csharp
// MutateEntry — for user actions (accept/edit/reject): requires Pending
if (entry.Status != ProposalEntryStatus.Pending)
    throw new EntryNotPendingException(fieldPath, entry.Status);

// RecordEntryOutcome — for system actions (apply results): requires Accepted/Edited
if (entry.Status is not (ProposalEntryStatus.Accepted or ProposalEntryStatus.Edited))
    throw new EntryNotApplicableException(fieldPath, entry.Status);
```

A common bug: extracting a shared `MutateEntry` helper that enforces Pending status,
then calling it from `RecordEntryOutcome` which operates on already-Accepted entries.
The fix: `RecordEntryOutcome` must have its own entry-finding and status-checking
logic, not delegate to the Pending-requiring helper.

**When fixing this in tests:** domain tests that called `RecordEntryOutcome` on
Pending entries must update their test data to use Accepted entries instead, since
that's the correct precondition.

### 22. Extension method `using` directives in new feature files

When a merge introduces new files that call extension methods (`RequireUserId`,
`WriteOkAsync`, `WriteAcceptedAsync`, etc.), the required `using` directives are
often missing because the author relied on IDE auto-import in their local branch
but the merge target doesn't have the same ambient context.

**Systematic fix:** For each missing extension method, find a *working* file in the
same project that uses it, copy its `using` block for the extension's namespace, and
add it to the broken file. The key namespaces in this project:

| Extension method | Namespace |
|---|---|
| `RequireUserId` | `JobSearchAiAssistant.Api.Infrastructure.Auth` |
| `WriteOkAsync` / `WriteAcceptedAsync` / `WriteCreatedAsync` / `WriteProblemAsync` | `JobSearchAiAssistant.Api.Infrastructure.Responses` |
| `ReadFromJsonAsync` / `QueryGuid` / `QueryString` | `JobSearchAiAssistant.Api.Infrastructure.Http` |
| `ApiJsonOptions.Pinned` | `JobSearchAiAssistant.Api.Infrastructure.Json` |

Similarly, when orchestrations use `nameof(ActivityClass)` to reference activities in
a sub-namespace (e.g. `LinkedIn.Activities`), the orchestration file needs a `using`
for that sub-namespace — the compiler does NOT resolve `nameof` across namespace
boundaries without it.

**Detection pattern:**
```
error CS1061: 'FunctionContext' does not contain a definition for 'RequireUserId'
error CS1061: 'HttpContext' does not contain a definition for 'WriteOkAsync'
error CS0103: The name 'LoadLinkedInSnapshotActivity' does not exist in the current context
```

### 23. JSON deserialization must pass the project's pinned options

When new API endpoint code uses `JsonSerializer.DeserializeAsync<T>(req.Body)` without
passing the project's JSON options, deserialization fails on camelCase property names
because the *default* `System.Text.Json` serializer expects PascalCase.

**Symptom:** Tests send `{"accessToken":"token"}` but the endpoint throws
`JsonException: missing required properties including: 'AccessToken'`.

**Fix:** Every `DeserializeAsync` call in API endpoints must pass the project's options:
```csharp
// WRONG — uses default PascalCase naming
var body = await JsonSerializer.DeserializeAsync<T>(req.Body, cancellationToken: ct);

// CORRECT — uses project's camelCase + enum converter
var body = await JsonSerializer.DeserializeAsync<T>(req.Body, ApiJsonOptions.Pinned, ct);
```

This pitfall is especially common when new files are added in a merge — the author may
have written the code before the project's JSON conventions were established, or may
have copy-pasted from a tutorial that uses default options.

**Audit pattern:** After fixing build errors, search all new endpoint files for bare
`DeserializeAsync` calls missing the options parameter:
```
grep -rn 'DeserializeAsync.*cancellationToken:.*cancellationToken' src/
```
Any hit that doesn't also pass `ApiJsonOptions.Pinned` is a latent bug.

### 24. Test failures hidden by upstream build failures

When the main project (e.g. `JobSearchAiAssistant.Api`) fails to build, test projects
that depend on it (`JobSearchAiAssistant.Api.Tests`) also fail — but the test project's
*own* errors (nullability, wrong assertions, etc.) are hidden behind the upstream failure.

**Fix workflow:**
1. Fix all upstream build errors first.
2. Rebuild — if the test project now compiles, run tests.
3. Any test failures that appear *after* fixing build errors were always there but
   hidden. Treat them as part of the merge fix, not as new regressions.

In this session, a CS8602 null-reference warning in a NSubstitute `Arg.Is<T>` lambda
was hidden until the API project built successfully. The fix was adding a
null-forgiving `!` operator on the lambda parameter.

### 25. NSubstitute `Arg.Is<T>` lambda parameter treated as nullable

NSubstitute's `Arg.Is<T>(predicate)` uses expression trees. The C# compiler may treat
the lambda parameter `c` as nullable (`T?`) even when `T` is a non-nullable reference
type, producing CS8602 ("Dereference of a possibly null reference"):

```csharp
// CS8602 on c.UserId — compiler thinks c might be null
await repo.Received(1).SaveAsync(Arg.Is<Foo>(c =>
    c.UserId == id && c.Name == "bar"), Arg.Any<CancellationToken>());

// FIX — null-forgiving operator
await repo.Received(1).SaveAsync(Arg.Is<Foo>(c =>
    c!.UserId == id && c.Name == "bar"), Arg.Any<CancellationToken>());
```

This is a known NSubstitute + nullable reference types interaction. The `!` is safe
because NSubstitute never passes `null` to the predicate.

### 26. `is not null` is illegal in expression trees (CS8122)

C# expression trees (used by NSubstitute's `Arg.Is<T>(predicate)`, EF Core's `Where()`,
and similar APIs that take `Expression<Func<T,bool>>`) do NOT support pattern-matching
syntax. Using `is not null` inside such a lambda produces CS8122:

```csharp
// CS8122: An expression tree may not contain an 'is' pattern-matching operator
repo.Received(1).UpsertAsync(Arg.Is<BetaAllowlistEntry>(e =>
    e is not null && e.GitHubLogin == "newuser"), Arg.Any<CancellationToken>());
```

**Fix options (any one works):**

```csharp
// Option A — null-forgiving operator (preferred, matches pitfall #25)
Arg.Is<BetaAllowlistEntry>(e => e!.GitHubLogin == "newuser")

// Option B — explicit null check with == null (works in expression trees)
Arg.Is<BetaAllowlistEntry>(e => e != null && e.GitHubLogin == "newuser")
```

**Rule of thumb:** Inside any `Expression<Func<...>>` lambda, you can use `!= null`,
`== null`, method calls, and property access — but NOT `is`, `is not`, `is null`,
`is not null`, `switch` expressions, or pattern matching of any kind.

When fixing CS8602 in `Arg.Is<>` lambdas (pitfall #25), resist the urge to reach for
`is not null` — it will compile to CS8122 instead. Use `!` (null-forgiving) or `!= null`.

### 27. Nullable vs non-nullable ETag parameters in test setup

When repository interfaces declare `string etag` (non-nullable) but test code
passes `null`, you get CS8625 warnings. The fix depends on the intended semantics:

| Intent | Fix | Fakes behavior |
|---|---|---|
| Insert-only (fail if exists) | Change interface to `string? etag` | `null` → insert guard |
| Blind upsert (always succeed) | Use `"*"` | `"*"` → no guard |
| Optimistic concurrency | Pass specific etag | ETag match required |

For test data setup, `"*"` is almost always correct — tests just need to seed data,
not exercise concurrency. Use `replace_all` to batch-fix all
`UpsertAsync(entity, null, ct)` calls to `UpsertAsync(entity, "*", ct)`.

### 28. NSubstitute cannot mock static extension methods

NSubstitute only intercepts virtual/abstract members on the substitute object. Static
extension methods execute their real code. When you write `context.GetHttpContext().Returns(httpContext)`,
NSubstitute tracks the last virtual property access *inside* the extension method
(typically `context.Items` or `context.Features`), then `.Returns()` applies to that
internal property — not the extension method result.

**Error signature:**
```
NSubstitute.Exceptions.CouldNotSetReturnDueToTypeMismatchException :
  Can not return value of type DefaultHttpContext for FunctionContext.get_Items
  (expected type IDictionary`2).
```

**Root cause:** The extension method internally calls a virtual property on the
substitute. NSubstitute records that as "the last call" and misapplies `.Returns()`.

**Fix:** Decompile the extension method to find what it reads internally, then
populate that mechanism directly:

```bash
# Decompile to find the internal key
ilspycmd -t TypeName path/to/Assembly.dll
```

Example — `GetHttpContext()` (from `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`)
reads `context.Items["HttpRequestContext"]`:
```csharp
// ❌ WRONG — extension method, NSubstitute can't intercept
context.GetHttpContext().Returns(httpContext);

// ✅ CORRECT — populate the dictionary directly
context.Items.Returns(new Dictionary<object, object>
{
    ["UserId"] = userId,
    ["HttpRequestContext"] = httpContext
});
```

Similarly, to make `GetHttpContext()` return null (non-HTTP trigger), simply omit
the `"HttpRequestContext"` key from the dictionary — no `.Returns((HttpContext?)null)`.

The same pattern applies to any extension method on a substitute: `context.UserId`
(an extension property that reads `context.Items["UserId"]`), etc. Always decompile
first to find the exact key/mechanism.

**When you see "Can not return value of type X for Y.get_Property":** This IS the
extension-method symptom. Don't chase type mismatches — the real problem is that
`.Returns()` hit the wrong target.

### 29. Container contract tests drift when adding new Cosmos containers

When adding a new container to the `cosmos_containers` Terraform map, three places
must stay in sync:

1. **`infra/cosmos.tf`** — the `cosmos_containers` map entry
2. **`ProvisionCosmosEmulator/CosmosContainers.cs`** — the `Names` list
3. **`CosmosContainerContractTests`** — `SeparateContainers` and `ConfiguredContainerNames()`

If the new container uses the standard `/userId` partition key (i.e. it's in the
`cosmos_containers` map), it must NOT be in `SeparateContainers` and must NOT be
excluded from `ConfiguredContainerNames()`. Only containers with non-standard
partition keys (like `betaAllowlist` with `/id`) belong in `SeparateContainers`.

**Detection:** `CosmosOptionsDefaults_MatchTerraformCosmosContainers` fails with:
```
CosmosOptions has no container property defaulting to: <containerName>
```

**Fix:** Remove the container from `SeparateContainers` and remove its exclusion
from `ConfiguredContainerNames()`.

### 30. Interface return type enrichment cascades to all test assertions

When an interface method changes from a simple return type to a richer one (e.g.,
`bool IsAllowed()` → `AllowlistResult? TryResolve()`), the test fix pattern is
NOT a simple rename. Every call site needs structural changes:

**Positive cases** (was `true`, now non-null result with metadata):
```csharp
// BEFORE — simple boolean
sut.IsAllowed(principal).ShouldBeTrue();

// AFTER — result object with metadata fields
var result = sut.TryResolve(principal);
result.ShouldNotBeNull();
result!.UserId.ShouldBe(expectedUserId);
result.IsAdmin.ShouldBeTrue();
```

**Negative cases** (was `false`, now `null`):
```csharp
// BEFORE
sut.IsAllowed(principal).ShouldBeFalse();

// AFTER
sut.TryResolve(principal).ShouldBeNull();
```

**Constructor parameter changes often accompany return enrichment.** If the
implementation class gains a new constructor parameter (e.g., `userId` for the
admin allowlist), every test instantiation must be updated:

```csharp
// BEFORE — 2 params
var allowlist = new SingleUserAllowlist(Provider, Login);

// AFTER — 3 params (userId added for the richer result type)
var allowlist = new AdminAllowlist(Provider, Login, ConfiguredUserId);
```

**Downstream consumers may change behavior.** When an admin flag is added to the
result, endpoints that check admin status (e.g., `executionContext.IsAdmin`) now
need the test's `FunctionContext.Items` dictionary to include the new key:

```csharp
// BEFORE — only UserId needed
context.Items.Returns(new Dictionary<object, object> { ["UserId"] = userId });

// AFTER — IsAdmin required for admin-gated endpoints
context.Items.Returns(new Dictionary<object, object> { ["UserId"] = userId, ["IsAdmin"] = true });
```

**Behavioral changes in filtering.** When admin access is added, endpoints that
previously filtered by userId may now return all results for admin users. Tests
that asserted filtered counts must update to expect the full set:

```csharp
// BEFORE — user sees only their own events (2 of 3)
root.GetArrayLength().ShouldBe(2);

// AFTER — admin sees all events
root.GetArrayLength().ShouldBe(3);
```

**Detection pattern:**
```
error CS1061: 'IPrincipalAllowlist' does not contain a definition for 'IsAllowed'
error CS0117: 'SingleUserAllowlist' does not contain a definition for '.ctor'
```

**Fix sequence:**
1. Read the new interface definition to understand the enriched return type
2. Read the implementation class for constructor changes
3. Update test instantiation (constructor args)
4. Update positive-case assertions (result object structure)
5. Update negative-case assertions (null instead of false)
6. Check downstream consumers for new context keys (e.g., FunctionContext.Items)
7. Check if behavior changed (filtering, access control) and update expectations

### 31. Domain type wrapping: flat → composite profile pattern

When a domain refactor splits a single type into sub-types and wraps them in a new composite, every test that constructs, saves, or asserts on the old type needs structural changes:

**Pattern (compensation refactor example):**
```csharp
// OLD — flat profile with all fields
var profile = new EmploymentCompensationCalculationProfile
{
    ContractType = CompensationContractType.B2B,  // read-only in new API
    TaxForm = TaxForm.LumpSum,                     // moved to B2BProfile
    ZusScheme = ZusScheme.Full,                    // moved to B2BProfile
    HasPit2 = true
};

// NEW — composite wrapping sub-profiles
var profile = new CompensationProfile
{
    B2BProfile = new B2BCompensationCalculationProfile
    {
        ZusScheme = ZusScheme.Full,
        TaxForm = TaxForm.LumpSum,
    },
    EmploymentProfile = new EmploymentCompensationCalculationProfile
    {
        HasPit2 = true,
        EmploymentCosts = EmploymentCostKind.Local,
    }
};
```

**Cascading fixes required:**
1. **Repository `SaveAsync` arg type** — changes from sub-type to composite type
2. **Repository getter method names** — `GetEmploymentProfileAsync` → `GetProfileAsync`
3. **Return type** — now `VersionedDocument<CompensationProfile>` instead of `VersionedDocument<EmploymentProfile>`
4. **Assertions** — `saved.Document.ContractType` → `saved.Document.EmploymentProfile!.ContractType`
5. **Read-only computed properties** — `ContractType` becomes a computed getter, not init-settable
6. **Constructor parameters** — calculator/engine now take `ICompensationCalculator` (composite) instead of `TakeHomeCalculator` (single)
7. **Method signatures** — `Calculate(Salary, profile, "2026", rates)` → `Calculate(SalaryOffer, CompensationProfile, CompensationPreferences, TaxYearRates)`

**Detection pattern:**
```
error CS1503: Argument 3: cannot convert from 'EmploymentCompensationCalculationProfile' to 'CompensationProfile'
error CS0200: Property or indexer 'EmploymentCompensationCalculationProfile.ContractType' cannot be assigned to -- it is read only
error CS0117: 'EmploymentCompensationCalculationProfile' does not contain a definition for 'TaxForm'
```

### 32. Method overload ambiguity when helper return types differ

When creating test helper methods, having two overloads with the same parameter signature but different return types causes compile errors because C# picks the simpler overload:

```csharp
// BUGGY — ambiguous, C# picks the Salary-returning version for engine.Recommend()
private static Salary OfferSalary(decimal m) => new() { Amount = m, ... };
private static SalaryOffer OfferSalary(decimal m, bool _) => new() { EmploymentOffer = new EmploymentSalaryOffer(OfferSalary(m)) };

// engine.Recommend expects SalaryOffer but gets Salary:
engine.Recommend(OfferSalary(32_000m), ...);  // CS1503: cannot convert Salary to SalaryOffer
```

**Fix:** Give the methods distinct names:
```csharp
private static Salary MakeSalary(decimal m) => new() { Amount = m, ... };
private static SalaryOffer MakeOffer(decimal m) => new() { EmploymentOffer = new EmploymentSalaryOffer(MakeSalary(m)) };
```

### 33. Factory method message text cascades to test substring assertions

When changing the message format in a domain factory method (e.g.
`SalaryRecommendation.Refuse()`, `TakeHomeResult.UnsupportedContractType()`), tests
across multiple projects that assert `ShouldContain("some substring")` on the message
will break if the new message uses different wording for the same concept.

**Session example:** Changed `Refuse()` from:
```
$"Cannot compute recommendation: contract type '{ContractType}' is unsupported. {UnsupportedReason}"
```
to:
```
$"Cannot compute recommendation: {UnsupportedReason}"
```

This broke tests in 3 separate files because `UnsupportedReason` from
`UnsupportedContractType()` says "not supported" while tests asserted `ShouldContain("unsupported")`.

**Prevention:** Before changing message text in any factory/refusal method:
1. Search ALL test projects for `ShouldContain` assertions on the old message substring
2. Check both the exact phrase AND semantic variants ("unsupported" vs "not supported")
3. Update all affected assertions in the same commit as the production change

**Detection pattern:** After changing message text, run:
```bash
dotnet test --filter "RequiresInfra!=true" 2>&1 | grep "FAIL"
```
Multiple failures across different test files/classes all pointing to `ShouldContain` on
a message field = this pitfall.

### 34. Roslyn phase short-circuit: declaration errors hide ALL method-body errors

Within a single project's compilation, Roslyn reports diagnostics by phase and **skips
the method-body binding phase entirely when declaration-phase errors exist**. So while
any declaration-phase errors (CS0535 interface not implemented, CS0246 type not found,
CS9035 required member, CS1501/CS7036 bad call arity at declaration level) are present,
every method-body diagnostic (CS1061, CS0029, CS0103, CS0117, CS7036 inside a method)
is silently suppressed.

**Consequence:** `dotnet build` can report "12 Error(s)" while the project actually
contains 393. The visible error list is phase-truncated, not just "hidden behind an
upstream project's failure" (pitfall #24 — this is the *same-project* variant).

Verified on Roslyn 10.0.302 (2026-08): a test project showed only 12 CS0535 errors from
4 files with old-shape interface stubs; after those 4 files were fixed, the rebuild
surfaced the full 393-error inventory (single-arg `TryParse` calls, deleted `Build`
methods, `BundledModel.EnsureAsync` static calls, etc.).

**Working rules:**
1. **The visible error count is NOT the inventory.** Group errors by code, fix the
   declaration-phase categories first (interface-shape stubs, missing types, required
   members), rebuild, and only then trust the list — the next batch appears.
2. Parse errors abort even harder (only syntax/directive diagnostics reported).
3. When the error list looks implausibly small for a big refactor, that is the signal:
   the declaration phase is failing and hiding the real wave.
4. A `#error` directive in a scratch file is a fast probe that a file is being compiled
   at all, but method-body probes will NOT report until declaration errors are gone.

### 35. LoggerFactory composition: `Create` + block lambda, not `.AddConsole` on a raw factory

For a bare `LoggerFactory` (no DI container), the options-lambda `AddConsole` overload
on `ILoggerFactory` doesn't exist in modern .NET — `new LoggerFactory().AddConsole(o => ...)`
binds to an `IConfiguration` overload and fails with CS1660. Also, a value-returning
expression lambda (`builder => builder.AddConsole(...)`) does not convert to
`Action<ILoggingBuilder>`.

Working form (stderr-routed console logging, the stdio-server pattern):
```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
});
var logger = loggerFactory.CreateLogger("CategoryName"); // CreateLogger<StaticClass> is illegal
```

### 36. CommunityToolkit Guard ParamName is the full argument expression

`Guard.IsNotNullOrWhiteSpace(encryptionData.SecretId)` throws with
`ParamName == "encryptionData.SecretId"` — the whole argument expression, not the
property name. Tests asserting `ex.ParamName.ShouldBe("SecretId")` fail; pin the
expression path instead.

## Reference

- `references/csharp-intervention-refactoring.md` — Full session transcript of a
  C# domain-model refactoring: Intervention model migration, error categories,
  fixed patterns, and restored-from-git recovery workflow. Load this when facing
  a similar property-extraction or method-signature refactoring in a .NET project.
- `references/react-component-extraction-test-breakage.md` — When extracting a React
  sub-component changes the UI contract (flat → accordion, visible → collapsed),
  existing parent tests break. Covers detection, fix patterns with
  `@testing-library/react`, Radix Accordion specifics, and the TDD sequence for
  safe component extraction. Load this when a component extraction causes
  `TestingLibraryElementError` in parent test files.
- `references/typescript-test-cleanup-after-type-removal.md` — When exported types,
  constants, interfaces, or functions are removed from a TypeScript module and test
  files reference them. Covers the classify-fix-sweep workflow, common patterns
  (remove entire test block, replace type in existing test, trim import), and
  pitfalls (stale constants in unrelated tests, preserving coverage for surviving
  behavior). Load this when `tsc`/`bun run lint`/`vitest` fail after an API removal.
