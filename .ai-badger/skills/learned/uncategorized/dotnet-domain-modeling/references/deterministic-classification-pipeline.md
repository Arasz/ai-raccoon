# Deterministic Classification Pipeline — Test Patterns

Reference for testing a multi-stage pure-domain classification pipeline (filter → correlate → classify).

## File Layout

```
src/MyApp.Domain/ChannelMonitoring/Classification/
    GmailHeaders.cs           # Primary constructor record — data shape
    RelevanceConfig.cs        # Injectable config record
    EmailRelevanceFilter.cs   # Static — IsRelevant() → bool
    SignalCorrelator.cs       # Static — Correlate() → string? (application ID)
    CheapClassifier.cs        # Static — Classify() → SignalClassification? (null = uncertain)

tests/MyApp.Domain.Tests/ChannelMonitoring/Classification/
    RelevanceFilterTests.cs   # 5-6 tests: ATS domain, unknown domain, contact email, LinkedIn, subject keywords, negative
    SignalCorrelatorTests.cs  # 4-6 tests: InReplyTo threading, recipient alias, company domain, no match, empty list
    CheapClassifierTests.cs   # 4-6 tests: auto-ack, rejection templates, uncertain returns null, null body
    ClassificationPipelineTests.cs  # 2-3 integration tests: full pipeline happy path, early exit, rejection path
```

## Test Helper Patterns

### Static factory for GmailHeaders

```csharp
private static GmailHeaders Headers(
    string from = "noreply@ats.com",
    string subject = "Application update",
    string to = "me@gmail.com",
    string? inReplyTo = null,
    string? references = null,
    string? messageId = null,
    DateTimeOffset? date = null) => new(
    From: from,
    Subject: subject,
    Date: date ?? new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
    InReplyTo: inReplyTo,
    References: references,
    To: to,
    MessageId: messageId ?? $"<{Guid.NewGuid()}@mail.example.com>"
);
```

### Static factory for test Application

```csharp
private static Application TestApplication(
    string id = "app-1",
    string userId = "user-1",
    string offerId = "offer-1",
    ApplicationState state = ApplicationState.CvSent,
    IReadOnlyList<ContactChannel>? channels = null) => new()
{
    Id = id,
    UserId = userId,
    OfferId = offerId,
    State = state,
    Channels = channels ?? [],
    CreatedAt = SomeInstant
};
```

### Shared RelevanceConfig

```csharp
private static readonly RelevanceConfig DefaultConfig = new(
    KnownAtsDomains: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "greenhouse.io", "lever.co", "workable.com", "smartrecruiters.com",
        "myworkday.com", "teamtailor.com", "recruitee.com"
    },
    LinkedInSenderPatterns: ["notifications-noreply@linkedin.com", "messages-noreply@linkedin.com"]
);
```

## Integration Test Pattern

The integration test exercises all three stages in sequence with realistic data. It asserts each stage independently (so failures are pinpointed) but proves the pipeline works end-to-end:

```csharp
[Fact]
public void Full_pipeline_ATS_auto_ack_is_relevant_correlated_and_classified()
{
    // Arrange — application with known contact channel
    var app = CreateApplication("app-1", "user-1", "offer-1",
    [
        new ContactChannel { Id = "ch-1", Type = ContactChannelType.Email, Value = "jobs@greenhouse.io" }
    ]);

    var headers = new GmailHeaders(
        From: "notifications@greenhouse.io",
        Subject: "Application Received - Software Engineer",
        Date: SomeInstant,
        InReplyTo: null, References: null,
        To: "me@gmail.com",
        MessageId: "<greenhouse-123@greenhouse.io>"
    );
    var body = "Thank you! We have received your application and will review it shortly.";

    // Act + Assert — each stage independently
    var isRelevant = EmailRelevanceFilter.IsRelevant(headers, DefaultConfig, []);
    isRelevant.ShouldBeTrue("ATS domain should be relevant");

    var appId = SignalCorrelator.Correlate(headers, [app]);
    appId.ShouldBe("app-1");

    var classification = CheapClassifier.Classify(headers, body);
    classification.ShouldNotBeNull();
    classification!.TransitionTo.ShouldBe(ApplicationState.AutoResponseReceived);
}
```

### Pipeline early-exit test

Proves the filter stops irrelevant emails before they reach correlation:

```csharp
[Fact]
public void Full_pipeline_unknown_sender_is_not_relevant()
{
    var headers = new GmailHeaders(
        From: "deals@spam-store.com",
        Subject: "50% off everything!",
        ...);

    var isRelevant = EmailRelevanceFilter.IsRelevant(headers, DefaultConfig, []);
    isRelevant.ShouldBeFalse("Unknown non-job domain should not be relevant");
    // No need to correlate or classify — early exit
}
```

## Email Parsing Helpers

The filter and correlator share email-parsing logic. Extract into private static helpers within each class (no shared util needed for 2 callers):

```csharp
/// Extracts email from "Display Name <email@example.com>" format.
private static string ExtractEmail(string from)
{
    var ltIdx = from.LastIndexOf('<');
    var gtIdx = from.LastIndexOf('>');
    if (ltIdx >= 0 && gtIdx > ltIdx)
        return from.Substring(ltIdx + 1, gtIdx - ltIdx - 1).Trim();
    return from.Trim();
}

/// Extracts domain from email address. Returns empty if no @ found.
private static string ExtractDomain(string email)
{
    var atIndex = email.LastIndexOf('@');
    return atIndex >= 0 ? email.Substring(atIndex + 1) : string.Empty;
}
```

## Correlation Strategies

| Strategy | How it works | When it fires |
|---|---|---|
| Recipient alias | `user+app-42@gmail.com` → extract `app-42` after `+` | Emails routed through +alias forwarding |
| Channel email match | Sender email matches a `ContactChannel.Value` on the application | Direct reply from a known recruiter |
| Domain match | Sender domain matches a channel's domain | Different person at same company replies |
| In-Reply-To threading | `InReplyTo` header matches a stored message ID | Reply to a previously tracked outbound |

**Priority:** Alias match > exact email match > domain match. Return first hit.

## Pitfall: Application doesn't carry company name directly

The `Application` aggregate has `OfferId` pointing to a `JobOffer` with `Company`, but the correlator shouldn't load that cross-aggregate. Instead, use the `ContactChannel` values on the application — the channel email's domain IS the company domain for correlation purposes. If no channel email exists, domain matching is skipped (not an error).

## CheapClassifier Pattern-Matching Rules

Group patterns by classification target. Check the more specific/significant classification first:

```csharp
private static readonly string[] RejectionPatterns =
[
    "we regret to inform", "not moving forward", "will not be moving forward",
    "decided not to move forward", "we have decided not to",
    "position has been filled", "other candidates",
    "after careful consideration", "we are unable to offer"
];

private static readonly string[] AutoAckPatterns =
[
    "we received your application", "has been received",
    "thank you for your application", "application received",
    "we have received your application", "successfully submitted"
];
```

**Order matters:** Check rejection before auto-ack — "we regret to inform you that we received your application" should classify as rejection, not ack.

**Confidence value:** Use 0.90 for deterministic rejection (terminal — always proposed per no-regret guarantee). Use 0.85–0.90 for non-terminal pattern matches. These sit above the default auto-apply threshold (0.8), so the policy will Auto-Apply for non-terminal high-confidence matches.

### Expanding the CheapClassifier

When adding new signal classes (offer, interview, human reply), follow this sequence:

1. **Define pattern arrays** — one per new class, specific phrases only (avoid conversational fragments like "available for a call" that match too broadly)
2. **Set evaluation order** — most specific first: `rejection → offer → interview → human reply → auto-ack → null`
3. **Write tests for each new class** — at least 2 positive (body match + subject match) and 1 negative per class
4. **Write an evaluation-order test** — body containing keywords from two classes, assert the more specific wins (e.g. rejection overrides offer: "we regret to inform you that we will not be extending an offer")
5. **Re-run uncertain tests** — new patterns may match bodies that were previously uncertain

### Pattern Specificity Pitfall

Prefer specific phrases over conversational fragments:

| Pattern | Problem |
|---|---|
| `"available for a call"` | Matches "are you available for a call?" (uncertain inquiry, not a human reply) |
| `"schedule a call"` | More specific — implies recruiter-initiated scheduling |
| `"next steps"` | Generic but acceptable — rarely appears in non-job emails |
| `"would like to discuss"` | Good — professional follow-up language |

When a new pattern breaks the "uncertain returns null" test, remove or narrow the pattern rather than changing the test — the test is the specification.

## LLM Fallback Integration

When `CheapClassifier` returns `null`, the pipeline defers to `LlmEmailClassifier`. The integration test proves this handoff:

```csharp
[Fact]
public async Task Integration_CheapClassifier_null_then_LLM_classifies()
{
    // Arrange: email CheapClassifier can't handle
    var headers = new GmailHeaders(
        From: "recruiter@acme.com",
        Subject: "Quick question about your availability",
        Date: SomeInstant, InReplyTo: null, References: null,
        To: "me@gmail.com", MessageId: "<test@mail.example.com>");
    var body = "Hi, we wanted to ask if you're available for a call next week?";

    var cheapResult = CheapClassifier.Classify(headers, body);
    cheapResult.ShouldBeNull("CheapClassifier should defer uncertain emails");

    // Act: LLM takes over
    var llm = new FakeLlmClient();
    llm.SetJsonResult(EmailClassificationStepTypes.ClassifyEmail, new
    {
        transitionTo = (string?)null, confidence = 0.4,
        summary = "Availability inquiry - not a status update"
    });
    var classifier = new LlmEmailClassifier(Factory(llm), PromptProvider, new LlmBudgetGuard());
    var signal = Signal(rawExcerpt: $"{headers.Subject}\nFrom: {headers.From}\n\n{body}");
    var result = await classifier.ClassifyAsync(signal, 0m, 0.01m, 100m, CancellationToken.None);

    // Assert: low confidence → proposal, not auto-apply
    result.TransitionTo.ShouldBeNull("Low-confidence result should not propose a transition");
    result.Confidence.ShouldBe(0.4);
}
```

For the full LLM classifier implementation, schema contracts, budget guard testing, and FakeLlmClient patterns, see `references/llm-classifier-integration.md`.
