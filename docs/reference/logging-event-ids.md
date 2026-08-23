# logging-event-ids

`[LoggerMessage]` `EventId` allocation record — the defence against a fourth collision.
`LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies` asserts every `EventId`
is unique across the assemblies, unconditionally. **There is no allowlist** — a
previous version of this doc described `1`/`2`/`3` as an intentionally deferred,
allowlisted exception; the test was never written that way, and no `EventId` `1`, `2`,
or `3` exists anywhere in the solution today.

## Status: measured, zero duplicates

Measured directly against `src/` on this branch: **166** `[LoggerMessage]`-attributed
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
| 20 | `src/AiRaccoon/Setup/Extensions/HostExtensions.cs` |
| 30 | `src/AiRaccoon/Setup/McpServerSetup.cs` |
| 40-41 | `src/AiRaccoon/Setup/Models/EmbeddingAvailability.cs` |
| 100-103 | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` (101-103 added 2026-08-22: the remote-blob HMAC authenticity check, docs/work/2026-08-21-delta-review-fix-plan.md S2 — 101 skips the push-side tag for an unencrypted bank, 102 skips the pull-side check for the same reason, 103 warns on a legacy remote blob with no tag) |
| 200-202 | `src/AiRaccoon.Infrastructure/Sync/S3CloudStore.cs` |
| 203-205 | `src/AiRaccoon.Infrastructure/Sync/AzureBlobCloudStore.cs` |
| 300, 301 | `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs` |
| 302 | `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs` |
| 310-312 | `src/AiRaccoon.Infrastructure/Watch/WatchCatchUp.cs` |
| 320, 321 | `src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs` |
| 330 | `src/AiRaccoon/Setup/AppRegistrations.cs` |
| 400 | *(retired 2026-08-22, WP11-B2)* — was `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs`'s best-effort-embed-failed warning; the digest no longer embeds inline (it signals the embed topic via `IEventPump<EmbedDrainRequest>.TryEnqueue`, which cannot throw), so the call site — and the `Log` class it lived in — is gone. Retired rather than reused, same convention as 416/512/516. |
| 410-413 | `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs` |
| 414-415, 417 | `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs` (docs/adr/0036: embed-time truncation and possible-[UNK]-collapse detectors — 414 is STORED CONTENT only since ADR-0071; 417 added 2026-08-22, #466: the graph pools its own output, so the manifest's pooling.mode cannot be applied. **416 is a hole in this block, not a free id** — it was `EmbeddingService`'s query-trim event and moved to 418 (itself later moved to 426, #522 — see below) to open 417, because this owner sat wedged between `BundledModel`'s 413 and that 416 with nowhere to grow, the same wedge that moved `MetricsFlusher` off 962-964. Retired, never reused) |
| 418, 419 | *(retired 2026-08-23, #522 review)* — was `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs`'s query-trimmed-to-window (418, itself ex-416) and invalid-threads-setting (419) events. Moved to 426, 427 to make room for the new session-created event (428) without extending into `NoOpCodeChunker`/`CodeEmbedder`/`ManifestPoolingRepair` (420-425), which `EventIdBlocks_DoNotInterleaveBetweenOwners` forbids — the same convention as the 416 move: the owner's block relocates rather than orphaning a new type. ADR-0071 and ADR-0072 carry a second amendment naming the new number. Retired, never reused, same convention as 400/416/512/516 |
| 420 | `src/AiRaccoon.Infrastructure/Chunking/NoOpCodeChunker.cs` (added 2026-08-21: the interim `ICodeChunker` — one Information line marking the code-engine-wave gap, logged once per process, docs/work/2026-08-21-code-search-implementation-plan.md §12.5) |
| 421-423 | `src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs` (added 2026-08-22, #466: a code row that cannot embed used to reach `CodeCorpusSchema.MaxEmbedAttempts` and drop out of the drain's selection with no log line at all — 421 records the whole-batch fallback at Debug, 422 warns per failed attempt with its exception, 423 errors the moment a row crosses the ceiling and is abandoned) |
| 424, 425 | *(retired 2026-08-23, #497/#504 review)* — was `src/AiRaccoon.Infrastructure/Embedding/ManifestPoolingRepair.cs`'s pooling-mode-repaired and pooling-mode-not-repaired events (#470). Moved to 429-432 to add a second, truthful id pair for the #497 shape without extending into `EmbeddingService` (426-428), which `EventIdBlocks_DoNotInterleaveBetweenOwners` forbids for a second block on the same owner too — the same convention as the 418/419 move. Retired, never reused, same convention as 400/416/418/419/512/516 |
| 426-428 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` (relocated from 418-419 on 2026-08-23, #522 review: 426 is `QueryTrimmedToWindow` (ex-418, ex-416), 427 is `InvalidThreadsSetting` (ex-419) — both unchanged in behavior, only the id moved. 428 is new: the resolved ORT intra-op thread count a local session was actually built with, logged in the same nested `Log` class as the other two — the round-tripped `embedding.threads` setting had no observable confirmation it took effect before this) |
| 429-432 | `src/AiRaccoon.Infrastructure/Embedding/ManifestPoolingRepair.cs` (relocated from 424-425 on 2026-08-23, #497/#504 review, and doubled: 429/430 are the #470 sole-output shape unchanged in behavior (only the id moved) — 429 records the pooling block rewritten to 'model-output' at engine activation with vectors unchanged, 430 warns when it could not be written and 417 will therefore keep firing. 431/432 are new, for the #497 shape — a distinctly-named `onnx.embeddingOutput` the graph itself pools beside a genuinely token-level output: 431 records the same rewrite but says truthfully that vectors CHANGE (a different tensor is now read, so the D7 fingerprint change re-embeds, ADR-0084), 432 warns that event 417 will NOT catch a failed write here — it only fires when the output actually read is itself rank-2, and this shape's read output stays rank-3) |
| 500-506, 508 | `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` (507/509 removed 2026-08-11: per-element candidate/failure logs de-noised) |
| 525-528 | `src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs` (added 2026-08-15: one line per maintenance job that ran or failed, ADR-0070 — 530+ was taken by SweepHostedService and the uniqueness gate caught it; 527 added 2026-08-22, delta-review D6: the lastRun-ledger SELECT and `HasWorkAsync` used to run before the per-job try/catch, so either throwing escaped `RunDueAsync` and stopped every job registered after it — moved inside the same guard, logged distinctly from a run failure; 528 added 2026-08-23, WP3/#477: a job's `IReportsOutstandingRows.CountOutstandingRowsAsync` failing after the job itself already ran and is already ledger-stamped — only that pass's `job.<name>.rows` gauge is missing, logged distinctly from a run failure so it is not confused with one) |
| 510-524 | `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs` (517-519 added 2026-08-14: the pending-embed retry sweep, .NET-F1 — a watch-driven embedding failure used to leave a row permanently pending; 520-521 added 2026-08-14: the noise-entry retention purge, ADR-0039; 522-524 added 2026-08-15: the promotion-discard and search-quality retention purges, ADR-0055. **512 and 516 are retired**: both are still declared here but have had no call site since ADR-0070 moved vacuuming into `MaintenanceJobRunner`, which logs 525 instead — they cannot fire. Retired rather than deleted so the numbers are not reused; found by the 2026-08-16 checklist run. ADR-0076's on-demand poll loop deliberately reuses `RunFailed` (513) instead of minting a new id — a second near-duplicate would have interleaved with `MaintenanceJobRunner`'s adjacent 525-526 block) |
| 530-537 | `src/AiRaccoon.Infrastructure/Degradation/SweepHostedService.cs` (537 is H6: skipped for access mode; moved from 520-527 on 2026-08-14 so the maintenance owner could stay contiguous when the noise purge extended it) |
| 601-609 | `src/AiRaccoon/Hosting/Node/NodeRunner.cs` (606-607 are the loopback token, ADR-0020; 608 is the lost-the-port restart, ADR-0022; 604 and 609 are the unanswered probe, ADR-0043) |
| 610-612 | `src/AiRaccoon/Hosting/Watchdog/IdleWatchdog.cs` |
| 620-623 | `src/AiRaccoon/Hosting/Node/ObservabilityRunner.cs` (landed in `4c4be1c`, #109) |
| 630 | `src/AiRaccoon/Hosting/Proxy/ProxyRunner.cs` (ADR-0020) |
| 633-635 | `src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs` (ADR-0020; path corrected 2026-08-22 — moved from `Setup/Serve/`. 635's message extended with the captured stderr, delta-review plan C1) |
| 636-639 | `src/AiRaccoon/Hosting/Proxy/ProxyForwarder.cs` (ADR-0020) |
| 640 | `src/AiRaccoon/Observability/OtlpExport.cs` (ADR-0009; OTLP export disabled warning) |
| 650-656 | `src/AiRaccoon/Hosting/Node/ServerRestart.cs` (ADR-0022; 656 is the unanswered probe, ADR-0043) |
| 660 | `src/AiRaccoon/Hosting/Node/ShutdownEndpoint.cs` (ADR-0022) |
| 670-675 | `src/AiRaccoon/Settings/SettingsEndpoint.cs` (ADR-0075: the control-plane settings resource; 672/673 log the key only, never the value — sync credentials and the embedding API key go through here; 674 is the model-migration outbox commit, ADR-0076; 675 added 2026-08-21: the code corpus's own activation commit, no outbox, docs/work/2026-08-21-code-search-implementation-plan.md §3.3) |
| 680-681 | `src/AiRaccoon/Settings/RepairEndpoint.cs` (ADR-0075 amendment: the control-plane repair resource — 680 is a report served, 681 is a repair_requests outbox commit) |
| 682-683 | `src/AiRaccoon/Settings/PromotionQueuePruneEndpoint.cs` (ADR-0075 amendment: the control-plane promotion-queue-prune resource — 682 is a report served, 683 is a promotion_queue_prune_requests outbox commit) |
| 684 | `src/AiRaccoon/Settings/MaintenanceStatsEndpoint.cs` (ADR-0075 amendment: the control-plane maintenance-stats resource — read-only, no outbox) |
| 685 | `src/AiRaccoon/Settings/NoiseSummaryEndpoint.cs` (ADR-0075 amendment: the control-plane noise-summary resource — read-only, no outbox; closes `noise entries`' latent bank-open) |
| 686 | `src/AiRaccoon/Settings/WatchRegisteredEndpoint.cs` (ADR-0075 amendment: the control-plane watch-registered resource — read-only, no outbox; closes `watch registered`'s latent bank-open) |
| 700, 702-704, 707-709 | `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs` (701/705/706 removed 2026-08-11: per-element eviction/failure logs de-noised; 708 = prune summary; 709 added 2026-08-14 = stale promotion claims reclaimed, ADR-0037) |
| 800-807 | `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` |
| 899-900 | `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs` and `SqliteMemoryStore.Replace.cs` (path corrected 2026-08-22, same commit that added 899: the doc named `Sqlite/SqliteMemoryStore.cs`, but the file has lived at `Sqlite/Memory/SqliteMemoryStore.cs` since the class was split into partials — 899 is WP11 Finding (b)'s `ReplaceCoreAsync` held-transaction-span log, in `Replace.cs`, sharing the `Log` class nested in the outer `SqliteMemoryStore` partial; placed immediately below 900 rather than after 903, since `SqliteConnectionFactory` already owns 901-903) |
| 901, 902, 903 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs` (added 2026-08-21: the overlap-prune report (formerly the v11 ladder step, now an unconditional open-time step)'s overlap-prune report — one line per pruned watch + a count, docs/work/2026-08-21-code-search-implementation-plan.md §4; the migration itself is silent and only returns the pruned list, since `SqliteConnectionFactory.InitializeAsync` is the one caller in the chain that owns a logger) |
| 910-912 | `src/AiRaccoon/Tools/ToolRefusals.cs` |
| 920-921 | `src/AiRaccoon/Tools/MemoryTools.cs` (docs/adr/0040: read-path query guard shadow-mode verdict; 921 added — WP10, docs/plans/2026-08-15-performance-metrics-implementation.md: best-effort phase-measurement recording failure) |
| 951 | `src/AiRaccoon.Infrastructure/Sqlite/NoiseShadowObserver.cs` (ADR-0039: shadow mode records what a detector would have rejected, without rejecting) |
| 960 | `src/AiRaccoon.Infrastructure/Metrics/MetricsRecorder.cs` (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3) |
| 961 | `src/AiRaccoon.Infrastructure/Metrics/SqliteMetricsStore.cs` (WP3: the save-time query-identity allowlist) |
| 970-974 | `src/AiRaccoon.Infrastructure/Metrics/MetricsFlusher.cs` (WP3; moved from 962-964 and extended to 973-974 — the bounded shutdown-time final flush, review-fixes blocker 2 — freeing room the old block did not have: it sat wedged between SqliteMetricsStore's 961 and SqliteSearchQualityService's 965) |
| 965 | `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs` (WP10, docs/plans/2026-08-15-performance-metrics-implementation.md: `RecordSearchSafeAsync`'s best-effort failure) |
| 1000-1001 | `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs` (GH #357: `doctor`'s key-resolution and bank-open failure logs) |
| 1002-1007 | `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs` (added 2026-08-22, WP11-B2: the embed topic's single consumer — 1002 a drain pass started, 1003 a drain pass finished with its row count, 1004 a signal that raced to an already-empty pump (structurally unreachable for this 2-value coalescing topic, logged defensively), 1005 a drain pass failed; 1006 added WP11-C: `maintenance.embed-rows-per-run.global` is present but not a positive integer at most 4096 — garbage falls back to the 128 default, an over-ceiling value is clamped to 4096; logged once per distinct bad value, and an unset setting never logs this; 1007 added 2026-08-23, WP1 review round: a full-budget pass's own self re-signal did not enqueue — already queued (coalesced) or, in principle, capacity-dropped — routine when coalesced (proven by EmbedDrainContinuousTests), defensive only for the capacity-drop arm; either way the outbox recovers the rows on the next poll) |

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
