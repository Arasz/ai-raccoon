# logging-event-ids

`[LoggerMessage]` `EventId` allocation record — the defence against a fourth collision.
`LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies` asserts every `EventId`
is unique across the assemblies, unconditionally. **There is no allowlist** — a
previous version of this doc described `1`/`2`/`3` as an intentionally deferred,
allowlisted exception; the test was never written that way, and no `EventId` `1`, `2`,
or `3` exists anywhere in the solution today.

## Status: measured, zero duplicates

Measured directly against `src/` on this branch: **101** `[LoggerMessage]`-attributed
methods, every one carrying an explicit `EventId`, **zero duplicates**. The table below
is that measurement, not a hand-maintained list — see "How this table is produced"
below to reproduce it.

Worth recording why this doc exists at all: colliding ids compile, log, and pass every
assertion that isn't specifically checking for the collision — a duplicate is invisible
until someone reads real output from a live server and sees two different events
logged under the same id. `LoggerMessageEventIdTests` is that specific assertion, added
after such a collision was found this way; it is what makes a *fourth* collision a
build failure instead of a live-log surprise.

## The full allocation

One block per source file that owns a `Log` class or equivalent:

| Ids | File |
|---|---|
| 10-12 | `src/AiRaccoon/Program.cs` |
| 20 | `src/AiRaccoon/HostExtensions.cs` |
| 30 | `src/AiRaccoon/Setup/McpServerSetup.cs` |
| 40-41 | `src/AiRaccoon/Setup/EmbeddingAvailability.cs` |
| 100 | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` |
| 200-202 | `src/AiRaccoon.Infrastructure/Sync/S3CloudStore.cs` |
| 203-205 | `src/AiRaccoon.Infrastructure/Sync/AzureBlobCloudStore.cs` |
| 300, 301 | `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs` |
| 302 | `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs` |
| 310-312 | `src/AiRaccoon.Infrastructure/Watch/WatchCatchUp.cs` |
| 320, 321 | `src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs` |
| 330 | `src/AiRaccoon/Setup/Dependencies.cs` |
| 400 | `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs` |
| 410-413 | `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs` |
| 500-509 | `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` |
| 510-516 | `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs` |
| 520-527 | `src/AiRaccoon.Infrastructure/Degradation/SweepHostedService.cs` (527 is H6: skipped for access mode) |
| 601-603, 605-608 | `src/AiRaccoon/Setup/Serve/ServeRunner.cs` (604 unused — not a gap to fill; 606-607 are the loopback token, ADR-0020; 608 is the lost-the-port restart, ADR-0022) |
| 610-612 | `src/AiRaccoon/Setup/Serve/IdleWatchdog.cs` |
| 620-623 | `src/AiRaccoon/Observability/ObservabilityRunner.cs` (landed in `4c4be1c`, #109) |
| 630 | `src/AiRaccoon/Setup/Serve/ProxyRunner.cs` (ADR-0020) |
| 633-635 | `src/AiRaccoon/Setup/Serve/BackendLauncher.cs` (ADR-0020) |
| 636-639 | `src/AiRaccoon/Setup/Serve/ProxyForwarder.cs` (ADR-0020) |
| 640 | `src/AiRaccoon/Observability/OtlpExport.cs` (ADR-0009; OTLP export disabled warning) |
| 650-655 | `src/AiRaccoon/Setup/Serve/ServerRestart.cs` (ADR-0022) |
| 660 | `src/AiRaccoon/Setup/Serve/ShutdownEndpoint.cs` (ADR-0022) |
| 700-706 | `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs` |
| 800-807 | `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` |
| 900 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` |
| 910-911 | `src/AiRaccoon/Tools/ToolRefusals.cs` |

## How this table is produced

Regenerate it by measuring, not by hand-editing a row: reserving a block by inference
from the nearest file you happen to be reading is exactly how ids collide.

```bash
# every EventId with its file:line, sorted
grep -rn "EventId = " src --include="*.cs" \
  | sed -E 's/^([^:]+):([0-9]+):.*EventId = ([0-9]+).*/\3\t\1:\2/' \
  | sort -n

# duplicate check — must print nothing
grep -rho "EventId = [0-9]\+" src | grep -oE "[0-9]+" | sort -n | uniq -d
```

Before claiming a new block is free, run the duplicate check above across the *whole*
assembly, not just the file or module you're adding to — a block picked by "this looks
free from here" is how a collision gets introduced even with the numbering otherwise
clean.
