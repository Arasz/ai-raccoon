# Testing: Lifecycle Completeness Matrix


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

