# logging-event-ids

`[LoggerMessage]` `EventId` allocation record — the defence against a fourth collision.
`LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies` asserts every `EventId`
is unique across the assemblies, unconditionally. **There is no allowlist** — a
previous version of this doc described `1`/`2`/`3` as an intentionally deferred,
allowlisted exception; the test was never written that way, and no `EventId` `1`, `2`,
or `3` exists anywhere in the solution today.

## Status: measured, zero duplicates

Measured directly against `src/` on this branch: **133** `[LoggerMessage]`-attributed
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
| 10-12 | `src/AiRaccoon/AppRunner.cs` (corrected 2026-08-16: this block said `Program.cs`, which is a four-line shim; the `[LoggerMessage]` methods are and were in `AppRunner.cs`) |
| 13-14 | `src/AiRaccoon/Observability/BankEngineReporter.cs` (added 2026-08-15: the startup line naming the running binary against the bank's embedding engine, WP3 step 5) |
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
| 414-415 | `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs` (docs/adr/0036: embed-time truncation and possible-[UNK]-collapse detectors — 414 is STORED CONTENT only since ADR-0071) |
| 416 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` (added 2026-08-15: a search query trimmed to the model window, ADR-0071 — split out of 414 so each is countable) |
| 500-506, 508 | `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` (507/509 removed 2026-08-11: per-element candidate/failure logs de-noised) |
| 525-526 | `src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs` (added 2026-08-15: one line per maintenance job that ran or failed, ADR-0070 — 530+ was taken by SweepHostedService and the uniqueness gate caught it) |
| 510-524 | `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs` (517-519 added 2026-08-14: the pending-embed retry sweep, .NET-F1 — a watch-driven embedding failure used to leave a row permanently pending; 520-521 added 2026-08-14: the noise-entry retention purge, ADR-0039; 522-524 added 2026-08-15: the promotion-discard and search-quality retention purges, ADR-0055. **512 and 516 are retired**: both are still declared here but have had no call site since ADR-0070 moved vacuuming into `MaintenanceJobRunner`, which logs 525 instead — they cannot fire. Retired rather than deleted so the numbers are not reused; found by the 2026-08-16 checklist run) |
| 530-537 | `src/AiRaccoon.Infrastructure/Degradation/SweepHostedService.cs` (537 is H6: skipped for access mode; moved from 520-527 on 2026-08-14 so the maintenance owner could stay contiguous when the noise purge extended it) |
| 601-609 | `src/AiRaccoon/Hosting/Node/NodeRunner.cs` (606-607 are the loopback token, ADR-0020; 608 is the lost-the-port restart, ADR-0022; 604 and 609 are the unanswered probe, ADR-0043) |
| 610-612 | `src/AiRaccoon/Setup/Serve/IdleWatchdog.cs` |
| 620-623 | `src/AiRaccoon/Observability/ObservabilityRunner.cs` (landed in `4c4be1c`, #109) |
| 630 | `src/AiRaccoon/Setup/Serve/ProxyRunner.cs` (ADR-0020) |
| 633-635 | `src/AiRaccoon/Setup/Serve/BackendLauncher.cs` (ADR-0020) |
| 636-639 | `src/AiRaccoon/Setup/Serve/ProxyForwarder.cs` (ADR-0020) |
| 640 | `src/AiRaccoon/Observability/OtlpExport.cs` (ADR-0009; OTLP export disabled warning) |
| 650-656 | `src/AiRaccoon/Hosting/Node/ServerRestart.cs` (ADR-0022; 656 is the unanswered probe, ADR-0043) |
| 660 | `src/AiRaccoon/Hosting/Node/ShutdownEndpoint.cs` (ADR-0022) |
| 670-673 | `src/AiRaccoon/Settings/SettingsEndpoint.cs` (ADR-0077: the control-plane settings resource; 672/673 log the key only, never the value — sync credentials and the embedding API key go through here) |
| 700, 702-704, 707-709 | `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs` (701/705/706 removed 2026-08-11: per-element eviction/failure logs de-noised; 708 = prune summary; 709 added 2026-08-14 = stale promotion claims reclaimed, ADR-0037) |
| 800-807 | `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` |
| 900 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` |
| 910-912 | `src/AiRaccoon/Tools/ToolRefusals.cs` |
| 920-921 | `src/AiRaccoon/Tools/MemoryTools.cs` (docs/adr/0040: read-path query guard shadow-mode verdict; 921 added — WP10, docs/plans/2026-08-15-performance-metrics-implementation.md: best-effort phase-measurement recording failure) |
| 950 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteNoiseClusterStore.cs` (ADR-0039: noise-learning substrate) |
| 951 | `src/AiRaccoon.Infrastructure/Sqlite/NoiseShadowObserver.cs` (ADR-0039: shadow mode records what a detector would have rejected, without rejecting) |
| 960 | `src/AiRaccoon.Infrastructure/Metrics/MetricsRecorder.cs` (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3) |
| 961 | `src/AiRaccoon.Infrastructure/Metrics/SqliteMetricsStore.cs` (WP3: the save-time query-identity allowlist) |
| 970-974 | `src/AiRaccoon.Infrastructure/Metrics/MetricsFlusher.cs` (WP3; moved from 962-964 and extended to 973-974 — the bounded shutdown-time final flush, review-fixes blocker 2 — freeing room the old block did not have: it sat wedged between SqliteMetricsStore's 961 and SqliteSearchQualityService's 965) |
| 965 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs` (WP10, docs/plans/2026-08-15-performance-metrics-implementation.md: `RecordSearchSafeAsync`'s best-effort failure) |

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
