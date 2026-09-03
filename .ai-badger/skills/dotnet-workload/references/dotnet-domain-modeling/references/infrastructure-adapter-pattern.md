# Infrastructure Adapter Pattern (Transport Adapters)

When implementing a new external-service integration (Gmail API, LinkedIn, etc.), follow this pattern.
Distinct from Cosmos persistence — this is about wrapping an external HTTP API behind a clean boundary.

## Architecture Layers

```
Domain layer:       IChannelMonitor (extension-point interface, pure)
Infrastructure:     IGmailTransport (transport boundary, infra-only)
                    GmailChannelMonitor (implements IChannelMonitor, uses IGmailTransport)
                    GmailTokenRefresher (token lifecycle, raises interventions)
Tests:              FakeGmailTransport (test double)
                    GmailChannelMonitorTests
                    GmailTokenRefresherTests
```

**Key invariant:** Domain never sees Google/LinkedIn/etc. wire types. The transport interface
(`IGmailTransport`) lives in Infrastructure, not Domain. Only `IChannelMonitor` lives in Domain.

## Step-by-Step

### 1. Read existing patterns first

Before writing any code, read 3–5 files:
- The domain interface (`IChannelMonitor`, `IChannelSignalRepository`)
- An existing infrastructure implementation (e.g. `CosmosChannelSignalRepository`, `AnthropicLlmClient`)
- The `DataProtectionSecretCipher` for secret-handling patterns
- The test project's fakes (`InMemoryChannelSignalRepository`, `FakeSecretCipher`)

### 2. Create transport DTO + interface (Infrastructure only)

```csharp
// src/.../Infrastructure/Gmail/GmailMessage.cs
namespace MyApp.Infrastructure.Gmail;

public sealed record GmailMessage
{
    public required string MessageId { get; init; }
    public required string ThreadId { get; init; }
    public required string From { get; init; }
    public required string Subject { get; init; }
    public required DateTimeOffset Date { get; init; }
    public required string Snippet { get; init; }
}

// src/.../Infrastructure/Gmail/IGmailTransport.cs
namespace MyApp.Infrastructure.Gmail;

public interface IGmailTransport
{
    Task<IReadOnlyList<GmailMessage>> FetchMessagesSinceAsync(string userId, string? historyId, CancellationToken ct);
    Task<string?> GetLatestHistoryIdAsync(string userId, CancellationToken ct);
}
```

### 3. Create Fake transport (test project)

```csharp
// tests/.../Gmail/FakeGmailTransport.cs
public sealed class FakeGmailTransport : IGmailTransport
{
    private readonly List<GmailMessage> _messages = [];
    public IReadOnlyList<GmailMessage> Messages => _messages;
    public string? LatestHistoryId { get; set; }
    public Exception? Fault { get; set; }
    public string? LastRequestedHistoryId { get; private set; }

    public void AddMessage(GmailMessage message) => _messages.Add(message);

    public Task<IReadOnlyList<GmailMessage>> FetchMessagesSinceAsync(string userId, string? historyId, CancellationToken ct)
    {
        LastRequestedHistoryId = historyId;
        if (Fault is { } fault) return Task.FromException<IReadOnlyList<GmailMessage>>(fault);
        return Task.FromResult<IReadOnlyList<GmailMessage>>(_messages.ToList());
    }
    // ...
}
```

**Key:** The fake records `LastRequestedHistoryId` so tests can verify watermark passthrough.
The `Fault` property lets tests simulate transport errors.

### 4. Write tests FIRST (TDD Red)

Write the test file referencing types that don't exist yet. Verify build fails with CS0246.

**Essential test cases:**
- Happy path: messages map to domain signals with correct fields
- Empty result: returns empty list
- Watermark passthrough: transport receives the correct historyId/watermark
- Deduplication: duplicate message IDs are deduped before mapping
- Unique IDs: each mapped signal gets a unique `Id`
- Error propagation: transport faults bubble up

### 5. Implement the monitor (TDD Green)

```csharp
public sealed partial class GmailChannelMonitor(
    IGmailTransport transport,
    ILogger<GmailChannelMonitor> logger
) : IChannelMonitor
{
    public string ChannelType => "gmail";

    public async Task<IReadOnlyList<ChannelSignal>> FetchNewSignalsAsync(
        string userId, string? watermark, CancellationToken ct)
    {
        Guard.IsNotNullOrWhiteSpace(userId);
        var messages = await transport.FetchMessagesSinceAsync(userId, watermark, ct);
        var unique = messages.GroupBy(m => m.MessageId).Select(g => g.First()).ToList();
        return unique.Select(msg => MapToSignal(userId, msg)).ToList();
    }

    private static ChannelSignal MapToSignal(string userId, GmailMessage msg) => new()
    {
        Id = Guid.CreateVersion7().ToString(),
        UserId = userId,
        Source = "gmail",
        ExternalId = msg.MessageId,
        ReceivedAt = msg.Date,
        RawExcerpt = $"From: {msg.From}\nSubject: {msg.Subject}\n{msg.Snippet}",
        Disposition = SignalDisposition.Proposed,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
```

### 6. Token refresher + intervention signals

When the external service uses OAuth, create a separate class for token lifecycle:

```csharp
public sealed partial class GmailTokenRefresher(
    IChannelSignalRepository signalRepository,
    ILogger<GmailTokenRefresher> logger
)
{
    public async Task RaiseTokenExpiredInterventionAsync(string userId, CancellationToken ct)
    {
        var signal = BuildInterventionSignal(userId, "token-expired", "...");
        await signalRepository.UpsertAsync(signal, ct);
    }

    private static ChannelSignal BuildInterventionSignal(string userId, string interventionType, string message)
    {
        var deterministicId = CreateDeterministicId(userId, interventionType);
        return new ChannelSignal { Id = deterministicId, ... };
    }

    // Deterministic ID from SHA-256 → first 16 bytes → Guid
    private static string CreateDeterministicId(string userId, string interventionType)
    {
        var payload = $"gmail:intervention:{userId}:{interventionType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes).ToString();
    }
}
```

**Key:** Deterministic IDs enable upsert idempotency — raising the same intervention twice
produces the same signal, so the repository upsert overwrites rather than duplicates.

**Token refresher test cases:**
- Expired token raises intervention signal
- Revoked consent raises intervention with clear message
- Deterministic IDs: same intervention raised twice produces one signal (idempotent)
- No sensitive data in logs (never log tokens or message bodies)

### 7. Verify Green — full suite

```bash
dotnet build
dotnet test --filter "RequiresInfra!=true"
dotnet test --filter "FullyQualifiedName~Gmail"  # focused check
```

## High-Performance Logging (Project Convention)

Use nested `static partial class Log` with `[LoggerMessage]` attributes:

```csharp
public sealed partial class GmailChannelMonitor(...) : IChannelMonitor
{
    // ... implementation ...

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Fetching Gmail messages for user {UserId} with watermark {Watermark}")]
        public static partial void FetchingMessages(ILogger logger, string userId, string? watermark);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Gmail transport returned {RawCount} messages, {UniqueCount} unique for user {UserId}")]
        public static partial void TransportReturned(ILogger logger, int rawCount, int uniqueCount, string userId);
    }
}
```

**Conventions:**
- Class must be `partial` (the outer class too)
- `static partial class Log` — nested, private, always named `Log`
- Sequential `EventId` starting at 1 within each class
- Never log tokens, message bodies, or PII
- Log message identifiers, counts, and classification outcomes only

## Common Pitfalls

| Pitfall | Fix |
|---|---|
| Transport interface in Domain layer | Keep it in Infrastructure — Domain only sees the monitor interface |
| Logging message bodies or tokens | Log IDs, counts, outcomes only — check whether your project has a logging/PII ADR to comply with |
| Non-deterministic intervention IDs | Use SHA-256 hash of (userId, interventionType) → Guid for upsert idempotency |
| Forgetting dedup on transport results | Always `GroupBy(ExternalId).Select(First())` before mapping |
| Using `IEnumerable<T>` in transport interface | Use `IReadOnlyList<T>` for async methods |
| Missing `CancellationToken` | Every async method must accept `CancellationToken ct` as last param |
| Fake transport doesn't record inputs | Add `LastRequestedHistoryId` etc. so tests can verify passthrough |
| Fake needed by multiple test projects | Move to shared `Testing` project; add Infrastructure project reference to `Testing.csproj` |
| Conditional DI registration for optional transports | When the monitor depends on a transport (e.g. `IGmailTransport`) that requires OAuth/credentials and may not be registered, guard the registration: `if (services.Any(d => d.ServiceType == typeof(IGmailTransport))) { services.AddSingleton<IChannelMonitor, GmailChannelMonitor>(); }`. Without this guard, `ValidateOnBuild` fails with "Unable to resolve service for type 'IGmailTransport'." |
| Test data doesn't match domain parser regex | When the monitor's integration tests use inputs (email subjects/snippets, etc.) that the domain parser's regex can't recognize, the parser returns `Unknown` or `null` and the test assertions fail with unexpected raw excerpts. Before writing test data, read the domain parser's actual classification method and regex patterns to find inputs that really match — don't guess a phrasing that "should" match and assume it does. |

## Sharing Fakes Across Test Projects

When a fake (e.g. `FakeGmailTransport`) is needed by both Infrastructure.Tests and Api.Tests, move it to the shared Testing project:

1. Move from `tests/.../Infrastructure.Tests/Gmail/FakeMyTransport.cs` → `tests/MyApp.Testing/Fakes/FakeMyTransport.cs`
2. Change namespace to `MyApp.Testing.Fakes`
3. Add Infrastructure project reference to `Testing.csproj` (the fake implements an Infrastructure-layer interface)
4. Update the original test project: `using MyApp.Testing.Fakes;`
5. Delete or empty the original file in Infrastructure.Tests

**Why Infrastructure reference in Testing:** The Testing project originally only references Domain. Fakes for Infrastructure interfaces (transport, service boundaries) need the Infrastructure project. This is a one-time setup — all subsequent Infrastructure-interface fakes go directly in Testing.
