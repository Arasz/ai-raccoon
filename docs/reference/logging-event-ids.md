# logging-event-ids

`[LoggerMessage]` `EventId` allocation and collision record. `LoggerMessageEventIdTests` guards
against any new collision; the deferred 1/2/3 reuse is an intentional, tracked exception.

## Status: the collisions are fixed; only 1/2/3 remain

Landed in #89. Measured across `src/` on `main` at 2026-08-07, the **only** duplicate `EventId`s
left in the assembly are `1`, `2` and `3` — the deliberately deferred set below.
`LoggerMessageEventIdTests` allowlists exactly those three and fails on any new collision.

| Was | Collided with | Now | File |
|---|---|---|---|
| 200 | `S3CloudStore` (keeps 200/201/205) | 302 | `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs` |
| 600-604 | `ServeRunner`'s 601/602/603 | 700-704 | `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs` |

`PromotionQueueService` moved wholesale rather than only the three ids that clashed: two modules
sharing one block was the actual defect, and moving the whole block leaves `ServeRunner` sole
owner of the 600s.

## The 600 block, measured

| Ids | Owner |
|---|---|
| 601, 602, 603, 605 | `Setup/Serve/ServeRunner.cs` |
| 610, 611, 612 | `Setup/Serve/IdleWatchdog.cs` |
| 620-623 | `Setup/Serve/ObservabilityRunner.cs` (reserved by concurrent work, not yet on `main`) |

**`610-613` was never free**, despite being reserved as "above `ServeRunner`'s 601-605" — a
concurrent session claimed it for a new runner, then found `IdleWatchdog` already owning
610/611/612 and moved to 620-623.

Worth recording how that surfaced, because it generalises: no test caught it and none could —
colliding ids compile, log, and pass every assertion. It appeared when someone read real output
from a live server and saw `IdleWatchdog[610]` scroll past above their own 610. **"Next free"
has to be measured across the whole assembly, not inferred from the nearest file you happen to be
reading.** The one-liner that measures it:

```
grep -rho "EventId = [0-9]\+" src | awk '{print $3}' | sort -n | uniq -d
```

## Known and deferred (out of scope for this task)

`EventId`s 1, 2, and 3 are each independently reused by six unrelated `Log` classes:

- `src/AiRaccoon/Program.cs` (1, 2, 3)
- `src/AiRaccoon/HostExtensions.cs` (2)
- `src/AiRaccoon/Setup/McpServerSetup.cs` (1)
- `src/AiRaccoon/Setup/EmbeddingAvailability.cs` (1, 2)
- `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` (1, 2, 3, 4, 5)
- `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs` (1, 2, 3)

**Not fixed here, deliberately.** `McpServerSetup.cs` is under active edit in a concurrent
session (adding an OTLP exporter and a `GET /observability` endpoint); renumbering it now would
collide with that in-flight work for a purely cosmetic gain. Duplicate `EventId`s across
*unrelated* `Log` classes are untidy rather than broken (most sinks disambiguate on category +
id), so this is a real finding but not urgent. `LoggerMessageEventIdTests` allowlists exactly
these three ids so the test still catches any *new* collision. Scheduled as its own cross-cutting
renumber once the concurrent work lands.

## Range plan (for the deferred renumber)

| Range | Owner (file) |
|---|---|
| 1–9 | `src/AiRaccoon/Program.cs` |
| 10–19 | `src/AiRaccoon/HostExtensions.cs` |
| 20–29 | `src/AiRaccoon/Setup/McpServerSetup.cs` |
| 30–39 | `src/AiRaccoon/Setup/EmbeddingAvailability.cs` |
| 40–49 | `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` |
| 50–59 | `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs` |
| 100–149 | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` |
| 200–201, 205 | `src/AiRaccoon.Infrastructure/Sync/S3CloudStore.cs` |
| 202–204 | `src/AiRaccoon.Infrastructure/Sync/AzureBlobCloudStore.cs` |
| 300–302 | `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs` (300–301), `WatchPipeline.cs` (302, planned) |
| 310 | `src/AiRaccoon.Infrastructure/Watch/WatchCatchUp.cs` |
| 320 | `src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs` |
| 330 | `src/AiRaccoon/Setup/Dependencies.cs` (one-off; unrelated to Watch despite the neighboring number) |
| 400 | `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs` |
| 500–507 | `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` |
| 510–516 | `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs` |
| 600–604 | `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs` |
| 610–612 | `src/AiRaccoon/Setup/Serve/IdleWatchdog.cs` |
| 610–613+ | contested: also claimed by a concurrent session's `ObservabilityRunner` — resolve before relying on this row |
| 700–702 | `src/AiRaccoon/Setup/Serve/ServeRunner.cs` (planned) |

*Note: this file needs an index row added to `docs/reference/README.md`; that file is owned by
another lane right now, so it isn't edited here.*
