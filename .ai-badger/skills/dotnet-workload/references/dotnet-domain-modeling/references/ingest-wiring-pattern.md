# Ingest Wiring Pattern (Transport → Repository with Cursor Durability)

Assumes Azure Durable Functions for the orchestration layer; the transport and repository pieces are store/vendor-agnostic (worked example below uses Gmail + Cosmos) — swap the orchestration for your own scheduler if the project has no Durable Functions.

When wiring a transport (`IGmailTransport`) to a repository (`IChannelSignalRepository`) for incremental data ingestion with cursor-based pagination.

### Architecture

```
Timer trigger → Durable Task orchestration → Activity (core logic)
                                                ↓
                                     IGmailTransport.FetchMessagesSinceAsync(cursor)
                                                ↓
                                     Dedup by ExternalId
                                                ↓
                                     IChannelSignalRepository.UpsertAsync (per signal)
                                                ↓
                                     IGmailIngestCursorRepository.Save (ONLY after all succeed)
```

### Key Decisions

| Decision | Rationale |
|---|---|
| Cursor advances only after entire batch persists | Crash mid-batch → re-read, not skip |
| Dedup by `(Source, ExternalId)` before persist | At-least-once delivery without double-creates |
| Durable Task orchestration as thin wrapper | `ScheduleWithConcurrencyGuardAsync` prevents double-runs |
| Activity class has public `IngestAsync` method | Testable without Durable Task context mocking |
| Separate `IGmailIngestCursorRepository` | Cursor is orthogonal to signal persistence |

### Interface Design

```csharp
// Domain layer — cursor store
public interface IGmailIngestCursorRepository
{
    Task<string?> GetLastProcessedHistoryIdAsync(string userId, CancellationToken ct);
    Task SaveLastProcessedHistoryIdAsync(string userId, string historyId, CancellationToken ct);
}

// Add to existing IChannelSignalRepository for dedup
Task<ChannelSignal?> GetByExternalIdAsync(string userId, string source, string externalId, CancellationToken ct);
```

### Implementation Pattern

```csharp
// Activity — contains testable core logic
public sealed partial class GmailIngestActivity(
    IGmailTransport transport,
    IChannelSignalRepository signalRepo,
    IGmailIngestCursorRepository cursorRepo,
    ILogger<GmailIngestActivity> logger)
{
    public async Task<GmailIngestOutcome> IngestAsync(string userId, CancellationToken ct)
    {
        var cursor = await cursorRepo.GetLastProcessedHistoryIdAsync(userId, ct);
        var messages = await transport.FetchMessagesSinceAsync(userId, cursor, ct);
        var latestHistoryId = await transport.GetLatestHistoryIdAsync(userId, ct);

        if (messages.Count == 0 || latestHistoryId is null)
            return new GmailIngestOutcome(0, cursor);

        var unique = messages.GroupBy(m => m.MessageId).Select(g => g.First()).ToList();
        int persistedCount = 0;

        foreach (var msg in unique)
        {
            var signal = MapToSignal(userId, msg);
            var existing = await signalRepo.GetByExternalIdAsync(userId, signal.Source, signal.ExternalId, ct);
            if (existing is not null) continue;
            await signalRepo.UpsertAsync(signal, ct);
            persistedCount++;
        }

        // ONLY reached if all persists succeeded — cursor stays put on exception
        await cursorRepo.SaveLastProcessedHistoryIdAsync(userId, latestHistoryId, ct);
        return new GmailIngestOutcome(persistedCount, latestHistoryId);
    }
}

// Durable Task orchestration — thin wrapper for ScheduleWithConcurrencyGuardAsync
[UsedImplicitly]
public static partial class GmailIngestOrchestration
{
    public const string Name = nameof(GmailIngestOrchestration);

    [Function(Name)]
    public static async Task<GmailIngestOutcome> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<GmailIngestOrchestrationInput>() ?? throw;
        return await context.CallActivityAsync<GmailIngestOutcome>(
            nameof(GmailIngestActivity), input);
    }
}
```

### Test Categories (minimum)

| Test | What it proves |
|---|---|
| At-least-once: run twice, same messages → no duplicates | Dedup by ExternalId |
| Mid-batch failure: UpsertAsync throws on Nth call → cursor NOT advanced | Crash safety |
| Mid-batch failure: already-persisted signals preserved | First N signals survive |
| Cursor advances after successful persist | Happy path |
| Second run advances cursor from previous position | Incremental ingestion |
| Empty inbox → no cursor advance | Edge case |
| Passes stored cursor to transport | Watermark passthrough |
| Passes null cursor for initial sync | First-run behavior |
| Dedup within same batch (duplicate messages) | GroupBy dedup |

### Pitfalls

| Pitfall | Fix |
|---|---|
| `GetByExternalIdAsync` not on `IChannelSignalRepository` | Add it — also update Cosmos + InMemory implementations + contract tests |
| Activity not testable because it's inside static Durable Task orchestration | Extract core logic into instance-based activity class with public `IngestAsync` |
| Cursor advanced before all signals persisted | Put `SaveLastProcessedHistoryIdAsync` AFTER the foreach loop, not inside |
| `FakeGmailTransport` only in one test project | Move to shared Testing project + add Infrastructure project reference to Testing.csproj |

