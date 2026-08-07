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
