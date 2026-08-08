# CommunityToolkit.Diagnostics Guard.IsEqualTo Compilation Errors

## The Error Messages

When you try to use `Guard.IsEqualTo<T>` with nullable types or enums, you get:

### Nullable reference types (string?, MyRecord?)

```
error CS8714: The type 'string?' cannot be used as type parameter 'T' in the
generic type or method 'Guard.IsEqualTo<T>(T, T, string)'. Nullability of type
argument 'string?' doesn't match 'notnull' constraint.

error CS8631: The type 'string?' cannot be used as type parameter 'T' in the
generic type or method 'Guard.IsEqualTo<T>(T, T, string)'. Nullability of type
argument 'string?' doesn't match constraint type 'System.IEquatable<string?>'.
```

### Enums (even with default IEquatable)

```
error CS0315: The type 'MyEnum' cannot be used as type parameter 'T' in the
generic type or method 'Guard.IsEqualTo<T>(T, T, string)'. There is no boxing
conversion from 'MyEnum' to 'System.IEquatable<MyEnum>'.
```

## Root Cause

`CommunityToolkit.Diagnostics.Guard.IsEqualTo<T>` has the constraint:
```csharp
public static void IsEqualTo<T>(T value, T target, [CallerArgumentExpression(nameof(value))] string? name = null)
    where T : notnull, IEquatable<T>
```

- `notnull` blocks nullable reference types
- `IEquatable<T>` blocks enums (they don't implement it by default, and the struct constraint prevents boxing)

## Solutions

### Pattern 1: Nullable reference check → if + ThrowHelper

```csharp
// ❌ Guard.IsEqualTo(CaseId, null, nameof(CaseId));
// ✅
if (CaseId is not null)
    ThrowHelper.ThrowInvalidOperationException(
        $"Signal is already correlated to case '{CaseId}'.");
```

### Pattern 2: Enum state check → if + ThrowHelper

```csharp
// ❌ Guard.IsEqualTo(Disposition, SignalDisposition.Proposed, nameof(Disposition));
// ✅
if (Disposition != SignalDisposition.Proposed)
    ThrowHelper.ThrowInvalidOperationException(
        $"Cannot dismiss a signal in '{Disposition}' disposition.");
```

### Pattern 3: Custom Require method (WorkflowStep pattern)

```csharp
private void Require(bool allowed, string action)
{
    if (!allowed)
        throw new InvalidStepTransitionException(Status, action);
}

// Usage:
public MyAggregate Activate()
{
    Require(Status is Status.Draft, nameof(Activate));
    return this with { Status = Status.Active };
}
```

## What DOES Work

These CommunityToolkit guard methods are safe to use with any type:

```csharp
Guard.IsNotNull(arg);                    // T? → T
Guard.IsNotNullOrWhiteSpace(stringArg);  // string? → string
Guard.IsNotNullOrEmpty(stringArg);       // string? → string
Guard.IsGreaterThan(int, 0);             // int : IComparable<int>
Guard.IsLessThanOrEqualTo(int, 100);     // int : IComparable<int>
Guard.IsInRange(val, min, max);          // T : IComparable<T>
ThrowHelper.ThrowArgumentException(name, msg);   // always works
ThrowHelper.ThrowInvalidOperationException(msg); // always works
```
