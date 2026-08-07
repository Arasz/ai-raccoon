# LLM Classifier Integration — Implementation & Test Patterns

Reference for implementing an LLM-based fallback classifier that extends the deterministic classification pipeline.

## File Layout

```
src/MyApp.Domain/ChannelMonitoring/Classification/
    EmailClassificationStepTypes.cs    # Step type constant
    EmailClassificationSchemas.cs      # Hand-composed JSON schema

src/MyApp.Domain/Llm/
    LlmPromptKeys.cs                   # Add ClassifyEmailSignal key
    DefaultPromptProvider.cs            # Add prompt text

src/MyApp.Api/ChannelMonitoring/Classification/
    LlmEmailClassifier.cs              # The classifier (Api layer, not Domain)

tests/MyApp.Domain.Tests/ChannelMonitoring/Classification/
    EmailClassificationSchemasTests.cs # Schema round-trip + structural tests

tests/MyApp.Api.Tests/ChannelMonitoring/Classification/
    LlmEmailClassifierTests.cs         # Classifier unit tests
```

## Step Type Constant

```csharp
namespace MyApp.Domain.ChannelMonitoring.Classification;

public static class EmailClassificationStepTypes
{
    public const string ClassifyEmail = nameof(ClassifyEmail);
}
```

## JSON Schema Contract (Hand-Composed)

Nullable enums with `JsonStringEnumMemberName` attributes don't auto-generate correctly with `JsonSchema.Net.Generation` — the output wraps them in `oneOf` and the enum values may not honor the camelCase overrides. Hand-compose for
predictable output:

```csharp
using Json.Schema;
using Json.Schema.Generation;

public static class EmailClassificationSchemas
{
    public static readonly string ClassificationSchema = GenerateClassificationSchema();

    private static string GenerateClassificationSchema()
    {
        var transitionToBuilder = new JsonSchemaBuilder()
            .Type(SchemaValueType.String)
            .Enum("Failed", "AutoResponseReceived");

        var root = new JsonSchemaBuilder()
            .Schema(MetaSchemas.Draft202012Id)
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Required("transitionTo", "confidence", "summary")
            .Properties(
                ("transitionTo", new JsonSchemaBuilder()
                    .OneOf(transitionToBuilder, new JsonSchemaBuilder().Type(SchemaValueType.Null))),
                ("confidence", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Number)
                    .Minimum(0)
                    .Maximum(1)),
                ("summary", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1))
            )
            .Build();

        return JsonSchemaGeneration.Serialize(root);
    }
}
```

### Schema Tests

```csharp
// Valid documents
[Fact]
public void Schema_evaluates_a_minimal_valid_document() { ... }  // transitionTo: "Failed", confidence: 0.85
[Fact]
public void Schema_evaluates_a_valid_document_with_null_transition() { ... }  // transitionTo: null
[Fact]
public void Schema_evaluates_a_valid_AutoResponseReceived_transition() { ... }

// Rejection tests
[Theory] [InlineData(-0.1)] [InlineData(1.1)]
public void Schema_rejects_an_out_of_range_confidence(double confidence) { ... }
[Fact]
public void Schema_rejects_an_invalid_transition_value() { ... }  // "Interview" not in enum
[Fact]
public void Schema_rejects_a_missing_required_summary() { ... }
[Fact]
public void Schema_rejects_an_unknown_top_level_property() { ... }

// Structural tests
[Fact]
public void Schema_property_names_are_camelCase() { ... }
[Fact]
public void Schema_transitionTo_enum_values_match_ApplicationState_members() { ... }
```

#### Pitfall: Locale-sensitive double in test data

```csharp
// ❌ BREAKS on systems with comma decimal separator (de-DE, pl-PL, etc.)
var document = JsonDocument.Parse($$"""{"confidence": {{confidence}}}""");
// Produces {"confidence": 1,1} which is invalid JSON

// ✅ CORRECT
var confidenceStr = confidence.ToString(CultureInfo.InvariantCulture);
var document = JsonDocument.Parse($$"""{"confidence": {{confidenceStr}}}""");
```

#### Pitfall: Nullable enum schema uses oneOf

The generated/hand-composed schema wraps nullable enums in `oneOf: [enum, null]`. Tests navigating the schema must handle this:

```csharp
// ❌ THROWS — "transitionTo" is an object with oneOf, not an array
var enumValues = document.RootElement
    .GetProperty("properties").GetProperty("transitionTo")
    .EnumerateArray()  // InvalidOperationException: expected Array, got Object

// ✅ CORRECT — navigate into oneOf to find the enum branch
var transitionToElement = document.RootElement
    .GetProperty("properties").GetProperty("transitionTo");
var enumValues = transitionToElement
    .GetProperty("oneOf")
    .EnumerateArray()
    .Where(e => e.TryGetProperty("enum", out _))
    .SelectMany(e => e.GetProperty("enum").EnumerateArray())
    .Select(v => v.GetString())
    .ToList();
```

## LlmEmailClassifier Implementation

```csharp
public sealed class LlmEmailClassifier(
    ILlmClientFactory llmClientFactory,
    IPromptProvider promptProvider,
    ILlmBudgetGuard budgetGuard)
{
    private const int MaxTokens = 1024;
    private const int MaxExcerptLength = 2000;

    public async Task<SignalClassification> ClassifyAsync(
        ChannelSignal signal,
        decimal monthToDateUsd,
        decimal estimatedCallCostUsd,
        decimal monthlyCapUsd,
        CancellationToken ct)
    {
        Guard.IsNotNull(signal);

        // 1. Budget gate
        var budgetCheck = budgetGuard.Authorize(monthToDateUsd, estimatedCallCostUsd, monthlyCapUsd);
        if (budgetCheck.Decision == BudgetDecision.Deny)
            throw new LlmBudgetExceededException(EmailClassificationStepTypes.ClassifyEmail, budgetCheck);

        // 2. Get LLM client (BYOK-aware)
        var llmClient = await llmClientFactory.GetClientAsync(signal.UserId, ct);

        // 3. Minimal payload (data minimization)
        var excerpt = signal.RawExcerpt.Length <= MaxExcerptLength
            ? signal.RawExcerpt
            : signal.RawExcerpt[..MaxExcerptLength] + "...";

        // 4. LLM call
        var request = new LlmRequest(
            ModelTier.Cheap,
            promptProvider.GetPrompt(LlmPromptKeys.ClassifyEmailSignal),
            [excerpt],
            EmailClassificationSchemas.ClassificationSchema,
            MaxTokens,
            EmailClassificationStepTypes.ClassifyEmail
        );
        var result = await llmClient.CompleteJsonAsync<EmailClassificationResponse>(request, ct);

        // 5. Map to domain type
        return MapToClassification(result);
    }

    private static SignalClassification MapToClassification(EmailClassificationResponse response)
    {
        var transitionTo = response.TransitionTo switch
        {
            "Failed" => ApplicationState.Failed,
            "AutoResponseReceived" => ApplicationState.AutoResponseReceived,
            _ => (ApplicationState?)null
        };
        return new SignalClassification
        {
            TransitionTo = transitionTo,
            Confidence = response.Confidence,
            Summary = response.Summary
        };
    }

    private sealed record EmailClassificationResponse
    {
        public string? TransitionTo { get; init; }
        public double Confidence { get; init; }
        public string Summary { get; init; } = "";
    }
}
```

### Key Design Decisions

| Decision                                           | Rationale                                                                                                                                           |
|----------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------|
| Budget params as method args, not constructor deps | Keeps the classifier testable without coupling to budget state management. The caller (orchestration layer) fetches monthToDateUsd from the ledger. |
| `ILlmCostTracker` NOT injected                     | Cost tracking is infrastructure-layer transparent via `ILlmCostTracker` decorator on `ILlmClient`. The classifier just uses the right `StepType`.   |
| `ModelTier.Cheap`                                  | Email classification is a straightforward categorization task — doesn't need strong-tier reasoning.                                                 |
| Max excerpt truncation                             | Google data policy: send least text that supports the decision. 2000 chars captures subject + opening paragraphs.                                   |
| Wire DTO is private nested record                  | The `EmailClassificationResponse` is a deserialization target, not a public contract. Keep it private.                                              |

## Classifier Tests

### Budget Exceeded

```csharp
[Fact]
public async Task Budget_exceeded_throws_LlmBudgetExceededException()
{
    var llm = new FakeLlmClient();
    llm.SetJsonResult(EmailClassificationStepTypes.ClassifyEmail, new
    {
        transitionTo = "Failed", confidence = 0.9, summary = "Test"
    });

    var denyGuard = new TestBudgetGuard(BudgetDecision.Deny, "Budget exceeded");
    var classifier = new LlmEmailClassifier(Factory(llm), PromptProvider, denyGuard);

    var ex = await Should.ThrowAsync<LlmBudgetExceededException>(
        () => classifier.ClassifyAsync(Signal(), 19.99m, 0.50m, 20m, CancellationToken.None));

    ex.StepType.ShouldBe(EmailClassificationStepTypes.ClassifyEmail);
    llm.Requests.ShouldBeEmpty("LLM should not be called when budget is exceeded");
}

// Test budget guard — injectable into tests
private sealed class TestBudgetGuard : ILlmBudgetGuard
{
    private readonly BudgetDecision _decision;
    private readonly string? _reason;
    public TestBudgetGuard(BudgetDecision decision, string? reason)
    {
        _decision = decision;
        _reason = reason;
    }
    public LlmBudgetCheckResult Authorize(
        decimal monthToDateUsd, decimal estimatedCallCostUsd, decimal monthlyCapUsd) =>
        new(_decision, monthToDateUsd, estimatedCallCostUsd, monthlyCapUsd, _reason);
}
```

### Low Confidence → Proposal

```csharp
[Fact]
public async Task Low_confidence_classification_returns_proposal()
{
    var llm = new FakeLlmClient();
    llm.SetJsonResult(EmailClassificationStepTypes.ClassifyEmail, new
    {
        transitionTo = (string?)null, confidence = 0.3, summary = "Uncertain"
    });

    var classifier = new LlmEmailClassifier(Factory(llm), PromptProvider, new LlmBudgetGuard());
    var result = await classifier.ClassifyAsync(Signal(), 0m, 0.01m, 100m, CancellationToken.None);

    result.TransitionTo.ShouldBeNull();
    result.Confidence.ShouldBe(0.3);
}
```

### LLM Request Details

```csharp
[Fact]
public async Task LLM_request_uses_correct_step_type_and_tier()
{
    // ... setup llm with SetJsonResult ...
    await classifier.ClassifyAsync(Signal(), 0m, 0.01m, 100m, CancellationToken.None);

    llm.Requests.ShouldHaveSingleItem();
    llm.Requests[0].StepType.ShouldBe(EmailClassificationStepTypes.ClassifyEmail);
    llm.Requests[0].Tier.ShouldBe(ModelTier.Cheap);
    llm.Requests[0].JsonSchema.ShouldNotBeNullOrEmpty();
}
```

### Integration: CheapClassifier (null) → LLM → Classified

```csharp
[Fact]
public async Task Integration_CheapClassifier_null_then_LLM_classifies()
{
    // Arrange: email that CheapClassifier cannot classify
    var headers = new GmailHeaders(...);
    var body = "Hi, we wanted to ask if you're available for a call next week?";
    var cheapResult = CheapClassifier.Classify(headers, body);
    cheapResult.ShouldBeNull("CheapClassifier should defer uncertain emails");

    // Act: LLM classifier takes over
    var llm = new FakeLlmClient();
    llm.SetJsonResult(EmailClassificationStepTypes.ClassifyEmail, new
    {
        transitionTo = (string?)null, confidence = 0.4,
        summary = "Availability inquiry - not a status update"
    });
    var classifier = new LlmEmailClassifier(Factory(llm), PromptProvider, new LlmBudgetGuard());
    var result = await classifier.ClassifyAsync(signal, 0m, 0.01m, 100m, CancellationToken.None);

    // Assert
    result.ShouldNotBeNull();
    result.Confidence.ShouldBe(0.4);
    result.TransitionTo.ShouldBeNull("Low-confidence result should not propose a transition");
}
```

### Data Minimization

```csharp
[Fact]
public async Task Long_excerpt_is_truncated_for_data_minimization()
{
    var longExcerpt = new string('x', 5000);
    // ... setup ...
    await classifier.ClassifyAsync(Signal(longExcerpt), 0m, 0.01m, 100m, CancellationToken.None);

    var sentContent = llm.Requests[0].ContentParts[0];
    sentContent.Length.ShouldBeLessThan(longExcerpt.Length);
}
```

## Prompt Text

Keep the prompt short — the schema enforces structure, the prompt just sets classification criteria:

```csharp
[LlmPromptKeys.ClassifyEmailSignal] = """
    You will be given the raw text of an email from a job application channel.
    Classify the email's intent: is it a rejection (transition to Failed), an
    auto-acknowledgement of an application (transition to AutoResponseReceived),
    or something else (no transition)?

    Focus on the subject line and the opening sentences. Only classify when the
    email's intent is clear from its text — if the email is ambiguous, a scheduling
    request, a general inquiry, or anything that does not clearly indicate a
    rejection or auto-acknowledgement, set transitionTo to null.

    Respond with the structured shape described by the JSON Schema below. Set
    confidence honestly: use 0.8 or higher only when the classification is
    near-certain, and below 0.5 when you are genuinely uncertain.
    """
```

## docs/flows.md Update

When adding the LLM fallback to a mermaid flowchart, include:

- Budget guard decision node (`BUDGET{"LlmBudgetGuard authorize?"}`)
- Budget deny path → `LlmBudgetExceededException → no-op`
- Confidence gate node (`LOW{"confidence ≥ 0.8?"}`)
- Two output paths: auto-apply candidate vs proposal
- Description bullet covering: budget gate, minimal payload, schema validation, confidence-based routing, cost tracking via infrastructure decorator
