# CommunityToolkit.Diagnostics Guards — full section


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

**Field-initializer-compatible form (project rule):** when the guard must run at
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
private SomeCommands ValidCtorArgs(SqliteConnectionFactory? bank = null, ...) =>
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
        new SomeCommands(null!,
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
Guard.IsEqualTo(CaseId, null, nameof(CaseId));

// ❌ CS0315: enum doesn't implement IEquatable<T>
Guard.IsEqualTo(Disposition, SignalDisposition.Proposed, nameof(Disposition));
```

**Fix — use explicit `if` + ThrowHelper:**

```csharp
// Nullable check
if (CaseId is not null)
    ThrowHelper.ThrowInvalidOperationException(
        $"Signal is already correlated to case '{CaseId}'.");

// Enum check
if (Disposition != SignalDisposition.Proposed)
    ThrowHelper.ThrowInvalidOperationException(
        $"Cannot dismiss a signal in '{Disposition}' disposition; only '{SignalDisposition.Proposed}' is allowed.");
```

This pattern is consistent with how the project's `WorkflowStep.Require(bool, string)` works — explicit precondition checks with custom exceptions.

