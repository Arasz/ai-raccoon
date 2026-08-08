# State Machine Policy Testing Patterns

Reusable test helpers and techniques for testing policy objects that depend on aggregate state machines.

## Walking a State Machine Forward in Tests

When testing a policy that needs a `Ticket` in a specific state, build a helper
that walks the happy path forward. This avoids creating separate factory methods for
each state and stays aligned with the real transition rules.

```csharp
private static Ticket NewTicket(TicketState state = TicketState.Draft)
{
    var theTicket = Ticket.Create("ticket-1", "user-1", "offer-1", SomeInstant);

    // Walk the ticket forward to the desired state along the happy path
    var path = new[]
    {
        TicketState.Ready,
        TicketState.Submitted,
        TicketState.AutoAckReceived,
        TicketState.ResponseReceived,
        TicketState.InReview,
        TicketState.OfferReceived,
        TicketState.Approved
    };

    foreach (var s in path)
    {
        if (theTicket.State == state) break;
        if (s == TicketState.Approved && state != TicketState.Approved) break;
        theTicket = theTicket.TransitionTo(s, SomeInstant, TransitionTrigger.System);
    }

    return theTicket;
}
```

**Pitfall:** Terminal states like `Failed` and `Declined` are not on the forward path.
To reach them, walk to the desired active state first, then apply the terminal transition
explicitly. The helper above only handles forward-path states.

## Ordinal "At or Past" Comparison

For idempotent / out-of-order signal detection, compare ordinal positions on the
forward path. This is cheaper than consulting the state machine and gives a clear
"skip" signal.

```csharp
private static readonly TicketState[] ForwardPath =
[
    TicketState.Draft,
    TicketState.Ready,
    TicketState.Submitted,
    TicketState.AutoAckReceived,
    TicketState.ResponseReceived,
    TicketState.InReview,
    TicketState.OfferReceived,
    TicketState.Approved
];

private static bool IsAtOrPast(TicketState current, TicketState target)
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

**Use when:** A signal may arrive out of order and you need to detect whether the
aggregate has already progressed past the proposed state.

**Pitfall:** `Array.IndexOf` returns -1 for states not in the array (Failed, Declined).
Always guard against negative indices — returning `false` means "not comparable on the
forward path", which is correct because Failed/Declined are always-reachable from any
active state and should be handled by a separate rule.

## Testing Policy Objects

Policy objects (like `SignalTransitionPolicy`) take domain inputs and return a decision
record. Test each decision branch independently:

### Decision Categories

| Decision | When | Test focus |
|----------|------|------------|
| NoOp | Signal irrelevant, aggregate already advanced, state machine rejects | All NoOp reasons are distinct — test each one |
| Apply | Allowed transition + high confidence | Verify `TargetState` set, note includes signal identity |
| Propose | Allowed transition + low confidence, OR terminal target | Always propose for terminal targets regardless of confidence |

### Test Helper: Configurable Signal Factory

```csharp
private static ChannelSignal NewSignal(
    SignalDisposition disposition = SignalDisposition.Proposed,
    SignalClassification? classification = null) =>
    new()
    {
        Id = "signal-1",
        UserId = "user-1",
        Source = "gmail",
        ExternalId = "ext-123",
        ReceivedAt = SomeInstant,
        RawExcerpt = "We'd like to schedule a call",
        TicketId = "ticket-1",
        Classification = classification,
        Disposition = disposition,
        CreatedAt = SomeInstant
    };
```

### Theory Tests for Terminal States

When the policy must behave identically for all terminal states, use `[Theory]`:

```csharp
[Theory]
[InlineData(TicketState.Approved)]
[InlineData(TicketState.Failed)]
[InlineData(TicketState.Declined)]
public void NoOp_when_ticket_terminal(TicketState terminal) { ... }
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

This ensures the note carries enough context for audit trails without being brittle
about exact formatting.

**Pitfall — nullable `Reason` field:** `SignalTransitionDecision.Reason` is `string?`.
Shouldly's `ShouldContain` expects non-null `string`. Always assert non-null first:

```csharp
// ❌ CS8604: Possible null reference argument
decision.Reason.ShouldContain("below threshold");

// ✅
decision.Reason.ShouldNotBeNull();
decision.Reason!.ShouldContain("below threshold", Case.Insensitive);
```

Use `Case.Insensitive` when matching formatted numbers or locale-dependent strings
to avoid brittle assertions.

## Transition Matrix Theory Test

When a policy evaluates multiple dimensions (ticket state × target state × confidence),
use a `[Theory]` with `[InlineData]` for the full matrix. This catches regressions
across the entire decision space. The confidence values below are illustrative —
replace them with your own auto-apply threshold and tune per class of signal:

```csharp
[Theory]
[InlineData(TicketState.Submitted, TicketState.AutoAckReceived, 0.90, TransitionDecisionType.Apply)]
[InlineData(TicketState.Submitted, TicketState.AutoAckReceived, 0.60, TransitionDecisionType.Propose)]
[InlineData(TicketState.Submitted, TicketState.ResponseReceived, 0.85, TransitionDecisionType.Apply)]
[InlineData(TicketState.Submitted, TicketState.ResponseReceived, 0.50, TransitionDecisionType.Propose)]
[InlineData(TicketState.Submitted, TicketState.InReview, 0.90, TransitionDecisionType.Apply)]
[InlineData(TicketState.Submitted, TicketState.InReview, 0.70, TransitionDecisionType.Propose)]
[InlineData(TicketState.Submitted, TicketState.OfferReceived, 0.85, TransitionDecisionType.Apply)]
[InlineData(TicketState.Submitted, TicketState.Failed, 1.00, TransitionDecisionType.Propose)]  // no-regret
[InlineData(TicketState.Submitted, TicketState.Failed, 0.90, TransitionDecisionType.Propose)]  // no-regret
[InlineData(TicketState.Submitted, TicketState.Failed, 0.50, TransitionDecisionType.Propose)]  // no-regret
[InlineData(TicketState.InReview, TicketState.OfferReceived, 0.90, TransitionDecisionType.Apply)]
[InlineData(TicketState.ResponseReceived, TicketState.InReview, 0.90, TransitionDecisionType.Apply)]
public void Transition_matrix(TicketState ticketState, TicketState target, double confidence, TransitionDecisionType expected)
{
    var classification = new SignalClassification { TransitionTo = target, Confidence = confidence, Summary = $"Test {target}" };
    var signal = NewSignal(classification: classification);
    var theTicket = NewTicket(ticketState);

    var decision = Policy().Evaluate(signal, theTicket);

    decision.Type.ShouldBe(expected);
    decision.TargetState.ShouldBe(target);
}
```

**Minimum coverage:** ≥ 12 rows covering Apply/Propose/NoOp decisions across at least
3 different ticket states. Always include terminal-target rows (Failed) at multiple
confidence levels to prove the no-regret guarantee.

### Late Rejection Tests

A rejection arriving after the ticket has progressed is a special case — it should still
Propose (not NoOp) because the target is terminal:

```csharp
[Fact]
public void Propose_late_rejection_on_active_ticket()
{
    // Ticket is InReview; signal says Failed (late rejection)
    var classification = new SignalClassification { TransitionTo = TicketState.Failed, Confidence = 0.90, Summary = "Late rejection" };
    var signal = NewSignal(classification: classification);
    var theTicket = NewTicket(TicketState.InReview);

    var decision = Policy().Evaluate(signal, theTicket);

    decision.Type.ShouldBe(TransitionDecisionType.Propose);
    decision.TargetState.ShouldBe(TicketState.Failed);
}

[Fact]
public void NoOp_late_rejection_on_terminal_ticket()
{
    // Ticket is Approved; signal says Failed — NoOp (ticket is terminal)
    var classification = new SignalClassification { TransitionTo = TicketState.Failed, Confidence = 0.90, Summary = "Rejection after approval" };
    var signal = NewSignal(classification: classification);
    var theTicket = NewTicket(TicketState.Approved);

    var decision = Policy().Evaluate(signal, theTicket);

    decision.Type.ShouldBe(TransitionDecisionType.NoOp);
}
```

The distinction: active ticket + terminal target → Propose. Terminal ticket + any target → NoOp.
