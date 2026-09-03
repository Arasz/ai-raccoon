## High-Performance Logging (Project Convention)

Every Infrastructure class that logs uses the nested `static partial class Log` pattern with `[LoggerMessage]` source generators. This avoids boxing allocations and string interpolation in hot paths.

```csharp
public sealed partial class MyChannelMonitor(...) : IMyMonitor
{
    // ... implementation methods ...

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Fetching messages for user {UserId} with watermark {Watermark}")]
        public static partial void FetchingMessages(ILogger logger, string userId, string? watermark);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Transport returned {RawCount} messages, {UniqueCount} unique for user {UserId}")]
        public static partial void TransportReturned(ILogger logger, int rawCount, int uniqueCount, string userId);
    }
}
```

**Conventions:**
- Outer class must be `partial` (required by source generator)
- Nested class: `private static partial class Log` — always named `Log`
- Sequential `EventId` starting at 1 within each class
- Never log tokens, message bodies, or PII — log IDs, counts, outcomes only
- `ILogger` passed as first parameter (not captured from outer scope)
