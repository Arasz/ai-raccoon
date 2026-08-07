# Platform Signal Parser — Pattern & Test Recipes

Reference for adding a new platform-specific signal parser to the deterministic classification pipeline.

## When to Use

When a new external channel (LinkedIn, Indeed, Glassdoor, etc.) sends email notifications that need to be parsed into domain signals. The parser is a **new stage** in the classification pipeline:

```
Inbound email → RelevanceFilter → [PlatformParser] → Correlator → Classifier → Policy → Transition
```

The parser sits between the relevance filter and the correlator. It only fires for emails from its platform's known senders.

## File Layout

```
src/MyApp.Domain/ChannelMonitoring/{Platform}/
    {Platform}EventType.cs           # enum: platform-specific event taxonomy
    {Platform}Signal.cs              # sealed record: intermediate domain shape (NOT persisted)
    {Platform}SignalParser.cs        # static class: Parse(GmailHeaders, string?) → {Platform}Signal?
    {Platform}SignalToChannelSignal.cs  # static class: Map({Platform}Signal, userId) → ChannelSignal

tests/MyApp.Domain.Tests/ChannelMonitoring/{Platform}/
    {Platform}SignalParserTests.cs
    {Platform}SignalToChannelSignalTests.cs
```

## Step-by-Step Implementation

### 1. Define the event taxonomy enum

Include ALL known event types, even if the parser can't currently detect them (e.g., from a planned API path). Unknown is always the last member.

```csharp
public enum LinkedInEventType
{
    ApplicationSent,
    ApplicationViewed,     // reserved for EU DMA API path
    ApplicationDownloaded, // reserved for EU DMA API path
    Rejection,             // reserved for EU DMA API path
    RecruiterMessage,
    InterviewRequest,
    Unknown
}
```

### 2. Define the intermediate signal record

This is NOT persisted — it's a parsing artifact. Include all fields the parser can extract, even if some are null for certain event types.

```csharp
public sealed record LinkedInSignal
{
    public required LinkedInEventType EventType { get; init; }
    public required string Company { get; init; }
    public required string JobTitle { get; init; }
    public string? JobId { get; init; }        // platform-specific stable ID
    public DateTimeOffset? AppliedAt { get; init; }
    public string? MessageExcerpt { get; init; } // for message-type events
}
```

### 3. Implement the parser (static class)

Key patterns:

- **Sender whitelist**: Check `headers.From` against a `HashSet<string>` of known platform senders. Return `null` for non-matching senders (not `Unknown`).
- **Event classification**: Pattern-match on combined subject + body. Check the most significant classification first (rejection > application > generic).
- **Field extraction**: Try subject first (more structured), then body. Never combine them into one string for regex — this causes greedy matches across boundaries.
- **Null body**: If body is null, still classify from subject alone. If neither subject nor body has recognizable patterns, return `Unknown` (not null — the sender matched).

```csharp
public static class LinkedInSignalParser
{
    private static readonly HashSet<string> LinkedInSenders =
    [
        "jobs-noreply@linkedin.com",
        "inmail-hit-reply@linkedin.com",
        "notifications@linkedin.com"
    ];

    public static LinkedInSignal? Parse(GmailHeaders headers, string? bodySnippet)
    {
        var fromLower = headers.From.ToLowerInvariant();
        if (!LinkedInSenders.Any(s => fromLower.Contains(s)))
            return null;  // NOT this platform's email

        var subject = headers.Subject;
        var body = bodySnippet ?? "";
        var eventType = ClassifyEventType(subject, body);
        var (company, jobTitle) = ExtractCompanyAndTitle(subject, body);
        // ...
    }
}
```

### 4. Implement the mapper

The mapper produces the persisted `ChannelSignal`. Key decisions:

- **ExternalId for dedup**: Prefer the platform's stable identifier (e.g., LinkedIn job ID → `li-job-12345`). When no stable ID exists, hash `(eventType + company + title)` with SHA-256 and take the first 16 hex chars.
- **Deterministic classification**: Only map event types where the classification is 100% certain (e.g., `ApplicationSent` → `CvSent` with confidence 1.0). Return `null` for uncertain types — the LLM classifier handles those later.
- **RawExcerpt**: Build a human-readable summary from the parsed fields, not the raw email body.

```csharp
public static class LinkedInSignalToChannelSignal
{
    public static ChannelSignal Map(LinkedInSignal signal, string userId)
    {
        var externalId = BuildExternalId(signal);
        var classification = MapClassification(signal);
        return new ChannelSignal
        {
            Id = Guid.CreateVersion7().ToString("N"),
            UserId = userId,
            Source = "linkedin",
            ExternalId = externalId,
            // ...
        };
    }

    private static string BuildExternalId(LinkedInSignal signal)
    {
        if (!string.IsNullOrWhiteSpace(signal.JobId))
            return $"li-job-{signal.JobId}";
        // Fallback: deterministic hash
        var raw = $"{signal.EventType}:{signal.Company}:{signal.JobTitle}";
        return $"li-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16]}";
    }

    private static SignalClassification? MapClassification(LinkedInSignal signal) =>
        signal.EventType switch
        {
            LinkedInEventType.ApplicationSent => new SignalClassification
            {
                TransitionTo = ApplicationState.CvSent,
                Confidence = 1.0,
                Summary = $"Application confirmed for {signal.JobTitle} at {signal.Company}"
            },
            _ => null  // uncertain → LLM classifier
        };
}
```

## Test Recipes

### Parser tests (7 tests minimum)

| Test | What it proves |
|---|---|
| Parse application-sent email | Happy path: event type, company, title, job ID extraction |
| Parse recruiter InMail | Message-type event: excerpt captured, company/title empty |
| Parse rejection from ATS (not LinkedIn) | Returns `null` — rejections come from employer ATSs, not the platform |
| Unknown event type degrades to Unknown | Unrecognized pattern still produces a signal, not an exception |
| Non-LinkedIn email returns null | Sender whitelist works |
| Application without job ID has null JobId | Graceful degradation when platform ID is missing |
| Null body returns Unknown for platform sender | Subject-only classification works |

### Mapper tests (6 tests minimum)

| Test | What it proves |
|---|---|
| ExternalId set from platform identifier | Dedup key is present and deterministic |
| Different signals produce different ExternalIds | No hash collisions for distinct inputs |
| Same signal produces same ExternalId | Deterministic (no random component in dedup key) |
| Deterministic classification mapping | e.g., ApplicationSent → CvSent with 1.0 confidence |
| Uncertain events produce null classification | LLM classifier will handle these |
| Full pipeline: parse → map → ChannelSignal | End-to-end integration test |

### Full pipeline integration test

```csharp
[Fact]
public void Full_pipeline_parse_map_produces_valid_ChannelSignal()
{
    var headers = new GmailHeaders(
        From: "jobs-noreply@linkedin.com",
        Subject: "You applied for Backend Engineer at StartupCo",
        Date: SomeInstant, InReplyTo: null, References: null,
        To: "user@gmail.com", MessageId: "<msg-123@linkedin.com>"
    );
    var body = "Your application for Backend Engineer at StartupCo was sent. View job: https://www.linkedin.com/jobs/view/5011111222";

    var parsed = LinkedInSignalParser.Parse(headers, body);
    parsed.ShouldNotBeNull();

    var mapped = LinkedInSignalToChannelSignal.Map(parsed, "user-42");

    mapped.Source.ShouldBe("linkedin");
    mapped.UserId.ShouldBe("user-42");
    mapped.ExternalId.ShouldNotBeNullOrEmpty();
    mapped.Classification.ShouldNotBeNull();
    mapped.Classification!.TransitionTo.ShouldBe(ApplicationState.CvSent);
    mapped.Disposition.ShouldBe(SignalDisposition.Proposed);
}
```

## Pitfalls

| Pitfall | Fix |
|---|---|
| Combining subject + body for regex | Try subject first, then body separately. Combined strings cause greedy matches across boundaries. |
| Returning `null` for unrecognized patterns from known senders | Return `Unknown` event type — the sender IS from the platform, the pattern is just unrecognized. `null` means "not this platform's email." |
| Using random GUIDs for ExternalId | Use the platform's stable identifier (job ID, message ID) or a deterministic hash. Random IDs defeat deduplication. |
| Classifying uncertain events deterministically | Only map event types where classification is 100% certain. Return `null` from the classification mapper for uncertain types. |
| Hardcoding sender emails in logic | Use a `HashSet<string>` at the top of the parser class. Easy to extend, easy to test. |
| Forgetting the `Unknown` enum member | Always include `Unknown` as the fallback. New event types from the platform won't crash the parser. |
| Test email subjects don't match parser regex | When writing integration tests for the channel monitor, the email subject/snippet must match what the parser's `ClassifyEventType` + regex patterns actually recognize. Read the parser's patterns before writing test data. E.g., "Your application for X at Y was sent" does NOT match `Contains("applied")` or `Contains("application sent")` — use "You applied for X at Y" instead. Mismatched test data causes the parser to return `Unknown` (no deterministic classification) and tests fail with unexpected `RawExcerpt` values like "LinkedIn notification: Unknown". |
| `Guid.NewGuid()` in mapper code | Use `Guid.CreateVersion7()` per ADR-0004, not `Guid.NewGuid()`. V7 is time-ordered for Cosmos partition efficiency. |
| Asserting on subject content in RecruiterMessage tests | For `RecruiterMessage` events, `RawExcerpt` is built from the body snippet (`MessageExcerpt`), not the subject. Test assertions like `signal.RawExcerpt.ShouldContain("CompanyName")` will fail if the company name only appears in the subject. Ensure the test snippet contains the data you're asserting on. |

## Spike Research Notes

Before implementing a new platform parser, research the platform's actual email behavior:

- **What emails does the platform actually send?** (e.g., LinkedIn sends NO rejection emails — those come from employer ATSs)
- **What are the sender addresses?** (e.g., `jobs-noreply@linkedin.com`, `inmail-hit-reply@linkedin.com`)
- **What data is extractable from the email?** (e.g., LinkedIn includes job IDs in URLs like `linkedin.com/jobs/view/12345`)
- **What data is NOT available?** (e.g., LinkedIn does NOT send 'application viewed' or 'resume downloaded' emails)

Document these findings in `docs/research/beta-epics/spike-{N}-{platform}-access.md` before implementing.
