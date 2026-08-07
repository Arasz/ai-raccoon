# State Machine Policy Testing Patterns

Reusable test helpers and techniques for testing policy objects that depend on aggregate state machines.

## Walking a State Machine Forward in Tests

When testing a policy that needs an `Application` in a specific state, build a helper that walks the happy path forward. This avoids creating separate factory methods for each state and stays aligned with the real transition rules.

```csharp
private static Application NewApplication(ApplicationState state = ApplicationState.Draft)
{
    var app = Application.Create("app-1", "user-1", "offer-1", SomeInstant);

    // Walk the app forward to the desired state along the happy path
    var path = new[]
    {
        ApplicationState.CvReady,
        ApplicationState.CvSent,
        ApplicationState.AutoResponseReceived,
        ApplicationState.ResponseReceived,
        ApplicationState.Interview,
        ApplicationState.OfferReceived,
        ApplicationState.Hired
    };

    foreach (var s in path)
    {
        if (app.State == state) break;
        if (s == ApplicationState.Hired && state != ApplicationState.Hired) break;
        app = app.TransitionTo(s, SomeInstant, TransitionTrigger.System);
    }

    return app;
}
```

**Pitfall:** Terminal states like `Failed` and `Declined` are not on the forward path. To reach them, walk to the desired active state first, then apply the terminal transition explicitly. The helper above only handles forward-path states.

## Ordinal "At or Past" Comparison

For idempotent / out-of-order signal detection, compare ordinal positions on the forward path. This is cheaper than consulting the state machine and gives a clear
"skip" signal.

```csharp
private static readonly ApplicationState[] ForwardPath =
[
    ApplicationState.Draft,
    ApplicationState.CvReady,
    ApplicationState.CvSent,
    ApplicationState.AutoResponseReceived,
    ApplicationState.ResponseReceived,
    ApplicationState.Interview,
    ApplicationState.OfferReceived,
    ApplicationState.Hired
];

private static bool IsAtOrPast(ApplicationState current, ApplicationState target)
{
    var currentIdx = Array.IndexOf(ForwardPath, current);
    var targetIdx = Array.IndexOf(ForwardPath, target);

    // If either state is not on the forward path (e.g. Failed/Declined),
    // the "at or past" check does not apply.
    if (currentIdx < 0 || targetIdx < 0)
        return false;

    return currentIdx >= targetIdx;
}
```

**Use when:** A signal may arrive out of order and you need to detect whether the aggregate has already progressed past the proposed state.

**Pitfall:** `Array.IndexOf` returns -1 for states not in the array (Failed, Declined). Always guard against negative indices — returning `false` means "not comparable on the forward path", which is correct because Failed/Declined are
always-reachable from any active state and should be handled by a separate rule.

## Testing Policy Objects

Policy objects (like `SignalTransitionPolicy`) take domain inputs and return a decision record. Test each decision branch independently:

### Decision Categories

| Decision | When                                                                 | Test focus                                                   |
|----------|----------------------------------------------------------------------|--------------------------------------------------------------|
| NoOp     | Signal irrelevant, aggregate already advanced, state machine rejects | All NoOp reasons are distinct — test each one                |
| Apply    | Allowed transition + high confidence                                 | Verify `TargetState` set, note includes signal identity      |
| Propose  | Allowed transition + low confidence, OR terminal target              | Always propose for terminal targets regardless of confidence |

### Test Helper: Configurable Signal Factory

```csharp
private static ChannelSignal NewSignal(
    SignalDisposition disposition = SignalDisposition.Proposed,
    SignalClassification? classification = null) =>
    new()
    {
        Id = "signal-1",
        UserId = "user-1",
        Source = "linkedin",
        ExternalId = "ext-123",
        ReceivedAt = SomeInstant,
        RawExcerpt = "We'd like to schedule an interview",
        ApplicationId = "app-1",
        Classification = classification,
        Disposition = disposition,
        CreatedAt = SomeInstant
    };
```

### Theory Tests for Terminal States

When the policy must behave identically for all terminal states, use `[Theory]`:

```csharp
[Theory]
[InlineData(ApplicationState.Hired)]
[InlineData(ApplicationState.Failed)]
[InlineData(ApplicationState.Declined)]
public void NoOp_when_application_terminal(ApplicationState terminal) { ... }
```

### Note Content Verification

When the policy produces a note/reason string that downstream code will use:

```csharp
[Fact]
public void Apply_transition_records_signal_in_note()
{
    // ... setup + evaluate ...
    decision.Reason.ShouldNotBeNull();
    decision.Reason.ShouldContain(signal.Source);
    decision.Reason.ShouldContain(signal.Id);
}
```

This ensures the note carries enough context for audit trails without being brittle about exact formatting.

**Pitfall — nullable `Reason` field:** `SignalTransitionDecision.Reason` is `string?`. Shouldly's `ShouldContain` expects non-null `string`. Always assert non-null first:

```csharp
// ❌ CS8604: Possible null reference argument
decision.Reason.ShouldContain("below threshold");

// ✅
decision.Reason.ShouldNotBeNull();
decision.Reason!.ShouldContain("below threshold", Case.Insensitive);
```

Use `Case.Insensitive` when matching formatted numbers or locale-dependent strings to avoid brittle assertions.

## Transition Matrix Theory Test

When a policy evaluates multiple dimensions (app state × target state × confidence), use a `[Theory]` with `[InlineData]` for the full matrix. This catches regressions across the entire decision space:

```csharp
[Theory]
[InlineData(ApplicationState.CvSent, ApplicationState.AutoResponseReceived, 0.90, TransitionDecisionType.Apply)]
[InlineData(ApplicationState.CvSent, ApplicationState.AutoResponseReceived, 0.60, TransitionDecisionType.Propose)]
[InlineData(ApplicationState.CvSent, ApplicationState.ResponseReceived, 0.85, TransitionDecisionType.Apply)]
[InlineData(ApplicationState.CvSent, ApplicationState.ResponseReceived, 0.50, TransitionDecisionType.Propose)]
[InlineData(ApplicationState.CvSent, ApplicationState.Interview, 0.90, TransitionDecisionType.Apply)]
[InlineData(ApplicationState.CvSent, ApplicationState.Interview, 0.70, TransitionDecisionType.Propose)]
[InlineData(ApplicationState.CvSent, ApplicationState.OfferReceived, 0.85, TransitionDecisionType.Apply)]
[InlineData(ApplicationState.CvSent, ApplicationState.Failed, 1.00, TransitionDecisionType.Propose)]  // no-regret
[InlineData(ApplicationState.CvSent, ApplicationState.Failed, 0.90, TransitionDecisionType.Propose)]  // no-regret
[InlineData(ApplicationState.CvSent, ApplicationState.Failed, 0.50, TransitionDecisionType.Propose)]  // no-regret
[InlineData(ApplicationState.Interview, ApplicationState.OfferReceived, 0.90, TransitionDecisionType.Apply)]
[InlineData(ApplicationState.ResponseReceived, ApplicationState.Interview, 0.90, TransitionDecisionType.Apply)]
public void Transition_matrix(ApplicationState appState, ApplicationState target, double confidence, TransitionDecisionType expected)
{
    var classification = new SignalClassification { TransitionTo = target, Confidence = confidence, Summary = $"Test {target}" };
    var signal = NewSignal(classification: classification);
    var app = NewApplication(appState);

    var decision = Policy().Evaluate(signal, app);

    decision.Type.ShouldBe(expected);
    decision.TargetState.ShouldBe(target);
}
```

**Minimum coverage:** ≥ 12 rows covering Apply/Propose/NoOp decisions across at least 3 different app states. Always include terminal-target rows (Failed) at multiple confidence levels to prove the no-regret guarantee.

### Late Rejection Tests

A rejection arriving after the app has progressed is a special case — it should still Propose (not NoOp) because the target is terminal:

```csharp
[Fact]
public void Propose_late_rejection_on_active_app()
{
    // App is Interview; signal says Failed (late rejection)
    var classification = new SignalClassification { TransitionTo = ApplicationState.Failed, Confidence = 0.90, Summary = "Late rejection" };
    var signal = NewSignal(classification: classification);
    var app = NewApplication(ApplicationState.Interview);

    var decision = Policy().Evaluate(signal, app);

    decision.Type.ShouldBe(TransitionDecisionType.Propose);
    decision.TargetState.ShouldBe(ApplicationState.Failed);
}

[Fact]
public void NoOp_late_rejection_on_terminal_app()
{
    // App is Hired; signal says Failed — NoOp (app is terminal)
    var classification = new SignalClassification { TransitionTo = ApplicationState.Failed, Confidence = 0.90, Summary = "Rejection after hire" };
    var signal = NewSignal(classification: classification);
    var app = NewApplication(ApplicationState.Hired);

    var decision = Policy().Evaluate(signal, app);

    decision.Type.ShouldBe(TransitionDecisionType.NoOp);
}
```

The distinction: active app + terminal target → Propose. Terminal app + any target → NoOp.
