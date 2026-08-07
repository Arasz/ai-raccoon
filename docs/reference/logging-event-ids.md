# logging-event-ids

`[LoggerMessage]` `EventId` allocation and collision record. `LoggerMessageEventIdTests` guards
against any collision, with no allowlist.

## Status: no duplicates remain

Landed in #89 and finished in #109. The six modules that each started numbering at 1 were moved
to their own blocks, and the test's `1/2/3` allowlist was deleted with them — measured across
`src/` on `main`, the assembly holds no duplicate `EventId`.

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

## The full allocation

Every `[LoggerMessage]` id in the assemblies, one block per module. `LoggerMessageEventIdTests`
fails on any duplicate — there is no allowlist.

| Ids | Owner |
|---|---|
| 10-12 | `Program.cs` |
| 20 | `HostExtensions.cs` |
| 30 | `Setup/McpServerSetup.cs` |
| 40-41 | `Setup/EmbeddingAvailability.cs` |
| 100 | `Access` |
| 200-205 | `Infrastructure/Sync/S3CloudStore.cs` |
| 300-302 | `Infrastructure/Watch` (event source, pipeline) |
| 310-312, 320-321, 330 | `Infrastructure/Watch` (catch-up, scheduler, store) |
| 400 | `Infrastructure/Embedding` |
| 410-412 | `Infrastructure/Embedding/BundledModel.cs` |
| 500-507, 510-516 | `Infrastructure/Maintenance` |
| 601-605 | `Setup/Serve/ServeRunner.cs` |
| 610-612 | `Setup/Serve/IdleWatchdog.cs` |
| 620-623 | `Setup/Serve/ObservabilityRunner.cs` |
| 700-704 | `Infrastructure/Promotion/PromotionQueueService.cs` |
| 800-807 | `Setup/Cli/Commands/EncryptionCommands.cs` |
| 900 | `Infrastructure/Sqlite/SqliteMemoryStore.cs` |

Six modules previously shared ids 1-8 — `Program`, `HostExtensions`, `McpServerSetup`,
`EmbeddingAvailability`, `BundledModel` and `EncryptionCommands` — each having started its own
numbering at 1. They now hold the blocks above.

**Before reserving a range, measure it across the whole assembly**, not from the nearest file:

```
grep -rho "EventId = [0-9]\+" src | awk '{print $3}' | sort -n | uniq -d
```

That prints nothing today. If it prints anything, a block was picked by inference rather than
measurement — which is exactly how `610-613` was once reserved while `IdleWatchdog` already held
610/611/612.
