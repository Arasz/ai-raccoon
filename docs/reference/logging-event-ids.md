# logging-event-ids

`[LoggerMessage]` `EventId` collision audit for `task/observability-truthfulness` (WI-2).
`LoggerMessageEventIdTests` pins the two collisions below once they're fixed and guards against
any *new* one; the deferred 1/2/3 reuse is an intentional, tracked exception.

## Status: planned, not yet implemented

Work was halted before the production fix landed (build machine saturated across four
concurrent worktree sessions — load average 200+ — so nothing compiled there would have been
trustworthy evidence). `WatchPipeline.cs` and `ServeRunner.cs` are unmodified as of this commit.
The table below is the plan for whoever picks this back up.

| EventId(s) | Collides with | Planned new id(s) | File |
|---|---|---|---|
| 200 | `S3CloudStore.cs` (keeps 200/201/205, untouched) | 302 | `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs` |
| 601, 602, 603 | `PromotionQueueService.cs` (keeps 600–604, untouched) | 700, 701, 702 | `src/AiRaccoon/Setup/Serve/ServeRunner.cs` |

`IdleWatchdog.cs` keeps its original 610–612 — not touched by this fix.

**Why 700–702 and not 613–615 (the originally planned numbers):** a concurrent session has taken
610–613 for a new `ObservabilityRunner` Log class, extending upward from 614 as it needs more —
i.e. the entire 610s neighborhood is contested right now. Picking well clear of it (700s) avoids
adding a second collision on top of the one being fixed. Re-check this section before
implementing, in case the 700s band is also claimed by then.

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
