# LANE P4 — Runtime observability parity for the MEMORY embed/migration path

MoE planning lane P4 of task `doctor-feature-match`, 2026-08-26. Written by a planning subagent
against the task worktree; reviewed in the same task's review round. Sibling lanes: P1 (doctor
output contract), P2 (doctor implementation shape), P3 (doctor test design). Companion research
record: `2026-08-26-doctor-memory-embedding-research.md`.

Repo read: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/doctor-feature-match` @ `38752171`, branch `task/doctor-feature-match`. All line numbers are from that tree.

---

## 1. GROUND TRUTH — what each path emits today

### 1.1 Verification of the facts I was handed

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| 1 | `EmbedDrainService` is already the shared, corpus-parameterised drain | **Confirmed** | `DrainOnceAsync` `EmbedDrainService.cs:107-154`; corpus branch `:118-120`; `Log` ids 1002 `:195-196`, 1003 `:198-200`, 1004 `:202-204`, 1005 `:206-207`, 1006 `:209-211`, 1007 `:213-215`; `RecordDrainMetrics` `:161-169` using `MetricsConfigKeys.DrainRowsMetricName` / `DrainDurationMetricName` (`MetricsConfigKeys.cs:77`, `:80`) |
| 1a | **Correction:** 1005 `DrainFailed` is *not* emitted by `DrainOnceAsync` | **Your list is slightly off** | `DrainOnceAsync` **rethrows** at `:149-153`. 1005 is emitted only by the pump loop's catch at `:95-98`. Any caller other than `ExecuteAsync` gets no failure log. This matters for Option A. |
| 2 | Memory gets those logs, but only via `EmbedDrainService` | **Confirmed** | signalled by `PendingEmbedJob.cs:50`, `FileIngestor.cs:188`, or the pass's own re-signal `EmbedDrainService.cs:130` |
| 3 | The migration path is unobservable | **Confirmed** | `ModelMigrationJob.RunAsync:36-43` — `:38` awaits `DrainMigrationAsync` with no assignment, `:42` returns a literal `false`. `EntryEmbedder.DrainMigrationAsync:94-141` — silent `false` at `:102-105`, unbounded `while (true)` `:118-130`, `FinishModelMigration` `:132-134`. Zero `[LoggerMessage]` in the file (`EntryEmbedder.cs:17` is not even `partial`). |
| 4 | Operator gets nothing; Code reports every pass | **Confirmed, and worse than stated** | the drain also has **no `IOperationTelemetry` scope** — contrast `EmbedDrainService.cs:110` `telemetry.Begin(OperationName)` — so no span and no `background.*` duration/outcome measurement either |
| — | The owner's excerpt is genuinely 1003 | **Confirmed** | `EmbedDrainService.cs:199` template `"Embed drain pass finished for {Corpus}: {Rows} row(s)"` renders exactly `Embed drain pass finished for Code: 2 row(s)` |
| — | "ran in 1 ms" is 525 | **Confirmed** | `MaintenanceJobRunner.cs:99` calling `:146-148` |

### 1.2 The matrix

| Component | Corpus | Start | Finish + rows | Failure | Progress | Metrics | Span |
|---|---|---|---|---|---|---|---|
| `FileIngestor` signal `:188` | Memory | — (`TryEnqueue`, no log) | — | — | — | — | — |
| `FileIngestor` signal `:193` | Code | — (`TryEnqueue`, no log) | — | — | — | — | — |
| `PendingEmbedJob.RunAsync:48-52` | Memory | 525 (`MaintenanceJobRunner.cs:99`) | 525 duration only | 526 `:150-152` | — | `job.pending-embed.duration_ms` + `.rows` (`MaintenanceJobRunner.cs:116-129`, `PendingEmbedJob.cs:55-57`) | via runner |
| `CodeReindexJob.RunAsync:47-51` | Code | 525 | 525 duration only | 526 | — | `job.code-reindex.*` (`CodeReindexJob.cs:54-56`) | via runner |
| `EmbedDrainService.DrainOnceAsync` | **Memory** | **1002 Debug** `:109` | **1003 Info + `{Rows}`** `:142` | **1005 Warn** `:97` (loop only) | — | **`drain.memory.rows` + `.duration_ms`** `:161-169` | `embed.drain` `:110` |
| `EmbedDrainService.DrainOnceAsync` | **Code** | **1002 Debug** | **1003 Info + `{Rows}`** | **1005 Warn** | — | **`drain.code.rows` + `.duration_ms`** | `embed.drain` |
| `ModelMigrationJob.RunAsync:36-43` | Memory | 525 only | 525 duration only, **bool discarded `:38`** | 526 (only if the drain throws) | — | `job.model-migration.duration_ms` only — **not** `IReportsOutstandingRows`, so no `.rows` gauge (`MaintenanceJobRunner.cs:119-122`) | via runner |
| **`EntryEmbedder.DrainMigrationAsync:94-141`** | Memory | **none** | **none** | **none** | **none** (1,492 batches at `BatchSize=32`, `EntryEmbedder.cs:24`, `:121-122`) | **none** | **none** |

### 1.3 (a), (b), or (c)?

**(c) both — and there is a third factor neither (a) nor (b) names.**

1. **A genuine missing-log defect (a):** the migration relay — `ModelMigrationJob` → `DrainMigrationAsync` — has no start, finish, failure, progress, metric, or span. `EntryEmbedder.cs` contains zero `[LoggerMessage]` declarations. This is unambiguous and it is the path that owns the 47,723-row backlog.

2. **A correct log set whose Memory lines were owned elsewhere (b) — partly, not wholly.** `PendingEmbedJob.HasWorkAsync:29-40` has **no migration check**: with `provider='local'` (non-blank, `:32`) and 47,723 pending rows (`MemorySql.HasPendingEmbed`, `:37-39`), it *is* due — which the excerpt confirms, since 525 only fires for a job that passed the due gate (`MaintenanceJobRunner.cs:70-74` then `:99`). So it signalled `EmbedCorpus.Memory` at `PendingEmbedJob.cs:50`, and `EmbedDrainService` *should* have logged 1003 for Memory. The relay does not suppress the pump path; `EmbedPendingBatchAsync` (`EntryEmbedder.cs:225-231`) takes no lease. Pure (b) is therefore **not** the explanation.

3. **The third factor — the start line is Debug.** `1002 DrainStarted` is `LogLevel.Debug` (`EmbedDrainService.cs:195`). At the Information verbosity the owner's `info:`-prefixed excerpt demonstrates, a Memory pass is **invisible for its entire duration** and only becomes visible at 1003 when it completes. A 128-row Memory pass against a local ONNX session takes seconds-to-minutes; a 2-row Code pass completes instantly. So the asymmetry the owner saw is partly a *latency* artefact of an Information/Debug split that happens to be harmless for a fast corpus and blinding for a slow one.

**Code that decides it:** `EmbedDrainService.cs:195` (Debug start) + `:198` (Info finish) + `:142` (finish emitted only after the pass returns) + `PendingEmbedJob.cs:29-40` (no migration gate) + `EntryEmbedder.cs:94-141` (relay emits nothing).

---

## 2. THE SILENT RETURNS

| # | Site | What happens | Operator's wrong conclusion | Minimum signal |
|---|---|---|---|---|
| S1 | `EntryEmbedder.cs:102-105` | `TryAcquireAsync` false → `return false` | "the migration job is a no-op; nothing is draining" | **Debug**, id **1010**: `"Embed drain for {Corpus} skipped: another relay pass holds the model migration lease"` — grammar mirrors `WatchCatchUp.cs:217-219` (`"…lost its lease to another process and stopped"`). Debug is correct *because* the paired Information start line (§5, 1008) is what makes its absence meaningful; at Information this fires every 15s poll for the whole drain (`BankMaintenanceHostedService.cs:79` `OnDemandPollInterval`), ~70 lines in the owner's 17.5-minute window. |
| S2 | `EntryEmbedder.cs:109-114` — **not in your list** | open-migration re-check false → `return false` | same as S1, but the truth is the opposite: the migration is *done* | **Debug**, id **1011**: `"Embed drain for {Corpus} skipped: the model migration was already finished by another relay pass"`. Reachable: `ModelMigrationJob.HasWorkAsync:25-29` said open, the row closed between the due-check and the run. |
| S3 | `EntryEmbedder.cs:116` → `:144-153`, blank provider returns at `:147-150` — **not in your list** | reconcile no-ops, then `:128` → `EmbedAsync:278-279` → `embeddings.CreateGenerator` on a blank provider; whatever it throws escapes `DrainMigrationAsync`, is caught at `MaintenanceJobRunner.cs:82-89`, logged as generic 526, **not ledger-stamped (`:88`)**, retried every 15s **forever**, with every memory tool refused by `ToolGate.cs:25-31` | "some transient bank error, it'll retry" — while the bank is in a permanent locked-out retry loop | **Warning**, id **1012**: `"Embed drain for {Corpus} cannot run: no embedding provider is configured; the model migration stays open and bank tools remain refused"`, emitted from a guard clause *before* the loop, returning `false` instead of throwing. Behaviour change, deliberate: false leaves the migration open (correct per ADR-0076 "mark it finished only on completion") and stops the throw-loop. |
| S4 | `ModelMigrationJob.cs:38`, `:42` | the `bool` is discarded; a literal `false` is returned | 525 "ran in 1 ms" cannot distinguish *acquired-and-drained-47,723-rows* from *lease busy* from *already finished* — the bool was the only carrier | **No new log at this site.** The job's return value legitimately means "created work for the pending-embed sweep", which is false either way (its own contract, `:31-35`), and giving `ModelMigrationJob` a logger would open a **second event-id owner** inside the reporter's span and trip `EventIdBlocks_DoNotInterleaveBetweenOwners` (§5). The correct seam is inside `DrainMigrationAsync`, via the shared reporter. Worth doing anyway: stop discarding (`var finished = await …`) so the value is at least named. |
| S5 | `PendingEmbedJob.cs:32-35` | blank provider → `HasWorkAsync` false, silently, forever | "there is no embed backlog" | **No new log here** — a per-poll line is a 15s-forever flood, and the one-shot signal already exists (`BankEngineReporter`, ids 13-14, `docs/reference/logging-event-ids.md:31`). **But there is a real metric hole:** `MaintenanceJobRunner.cs:100` records metrics only for a job that *ran* (`:70-74` gate), so `job.pending-embed.rows` — the gauge that would show 47,723 — is never recorded when the provider is blank. That belongs to the sibling doctor lanes; flagged, not claimed. |
| S6 | `EmbedDrainService.cs:149-153` vs `:95-98` | `DrainOnceAsync` rethrows; 1005 is emitted only by the pump loop | a future direct caller of `DrainOnceAsync` silently loses the failure log | Structural, not a live defect. It is a hard constraint on Option A (§4). |
| S7 | `EntryEmbedder.cs:96-100` | hand-rolled `is null` throw; dead under DI (`AppRegistrations.cs:360-364` registers `IModelMigrationLease` as required) | — | Out of lane; violates `.ai-badger/invariants/guard-clauses.md`. Note only. |

---

## 3. STALLED MIGRATION — who should notice, and what already does

**Nothing notices today. Verified against all three components you named:**

- **`MaintenanceJobRunner`** — no migration or lease awareness anywhere; its four ids (525-528, `:146-160`) are run/fail/due-check-fail/rows-count-fail. A stale lease is a *successful* pass to it: 525.
- **`BankMaintenanceHostedService`** — the only migration reference in the file is a prose comment about poll cadence at `:74`. Its id block 510-524 (`docs/reference/logging-event-ids.md:55`) has no migration or lease line.
- **`SqliteModelMigrationLease`** — `TryAcquireAsync:39-45` executes `MemorySql.AcquireModelMigrationLease` (`MemorySql.cs:495-497`), whose WHERE is `lease_owner IS NULL OR lease_expires_at < @now`. **An expired lease is silently reclaimed and `true` is returned.** The stale-takeover fact is computed and thrown away.

**Precedent for exactly this line already exists in the repo:** `PromotionQueueService.cs:313-315`, EventId 709 Warning — `"Reclaimed {Count} stale promotion claim(s) left behind by a prior failed PromoteAsync pass"`; and the losing side, `WatchCatchUp.cs:217-219`, EventId 312 Warning. So this is not a new pattern, it is a missing application of an established one.

**Which component should notice:** the acquirer, at the moment of acquisition, because that is the only place the pre-state is knowable. `TryAcquireAsync`'s `bool` cannot carry it. Minimal shape: `DrainMigrationAsync` reads the open row's `lease_owner` / `lease_expires_at` / `started_at` (one indexed single-row SELECT on `model_migration`) *before* acquiring, and hands it to the reporter, which emits **1009 Warning** when `lease_owner` was non-null and `lease_expires_at < now`. One extra read per relay poll, on a one-row table.

**What the owner's bank proves** (arithmetic run, not eyeballed):

- `started_at` 1787739481, `lease_expires_at` 1787740592 → **1111 s = 18.5 min** span.
- `LeaseTtl` is 60 s (`IModelMigrationLease.cs:34`) and is renewed after every 32-row batch (`EntryEmbedder.cs:129`), so the last successful renewal was at `1787740592 − 60 = 1787740532` → **the drain ran and renewed for ~17.5 minutes, then stopped.**
- `lease_owner` is still populated and `lease_expires_at` is non-NULL. `ReleaseAsync` sets **both to NULL** (`MemorySql.cs:503-504`) and runs in a `finally` (`EntryEmbedder.cs:137-140`). Therefore release never ran → **the holder died hard**, it did not throw. A throw would have hit that `finally` *and* produced a 526 at `MaintenanceJobRunner.cs:86`.

**Not one log line records those 17.5 minutes of embedding work, and not one records that the work stopped.** That is this lane's defect in a sentence. `doctor` printing HEALTHY over this state is the sibling lanes' problem; I do not plan it here.

---

## 4. THE EXTRACTION

### 4.1 Option A — route migration draining through `EmbedDrainService`'s pass

`ModelMigrationJob.RunAsync` becomes `embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory))`, i.e. a copy of `PendingEmbedJob.cs:50`, and `EmbedDrainService`'s Memory branch (`:120`) grows the lease, the vec-dimension reconcile and the finish write.

Costs, each grounded:

1. **It multiplies the ToolGate outage.** `ToolGate.cs:25-31` refuses **every** memory tool while the migration row is open. Today's unbounded `while (true)` finishes as fast as inference allows. A bounded pass is 128 rows by default (`BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal`, `EmbedDrainService.cs:114-116`, `:22`) → 373 passes for 47,723 rows. The self re-signal (`:130-135`) only fires on a *full-budget* pass; the moment one pass comes back partial the next is gated on the 15s poll (`BankMaintenanceHostedService.cs:79`). A bad-luck sequence turns an 18-minute lockout into hours. This alone disqualifies A.
2. **The finish predicate is not the same predicate.** `while(true)` terminates on `batch.Count == 0` (`EntryEmbedder.cs:123-126`) — genuinely empty. The pass's `drained >= rowsPerRun` (`:130`) is deliberately *not* that: the comment at `:127-129` explains it counts only rows whose UPDATE landed. `FinishModelMigration` would need a new "no pending rows remain" query and a new state-machine edge — directly on ADR-0076's "mark it finished **only on completion**" (`docs/adr/0076-*.md`, Decision), so an ADR amendment.
3. **The lease crosses pass boundaries.** Each pass opens and disposes its own connection (`EmbedDrainService.cs:117`). The lease is a row, so it survives — but TTL is 60 s and the inter-pass gap can be 15 s, so acquire/release-per-pass is the only honest shape: 373 acquire/release round-trips, and a window between every pair where another process can legally take over mid-migration.
4. **Coalescing erases the request's identity.** The coalesce key is `EmbedDrainRequest`'s structural equality (`EmbedDrainRequest.cs:15-20`), so a migration signal and a `PendingEmbedJob` signal collapse into one entry. The pass can no longer tell from the request whether it is a migration pass — it must re-derive that from the bank on every Memory pass.
5. **1005 doesn't come free.** Per S6, `DrainOnceAsync` rethrows and the failure log lives in the loop — fine here, but it means the pass's error path is only correct while the pump loop is the sole caller, which A must preserve.
6. **Unverified risk to check first:** `EmbedDrainService` is `AddHostedService` (`AppRegistrations.cs:214`). If any short-lived/CLI path drives `MaintenanceJobRunner.RunDueAsync` without a running pump consumer, routing through the pump means such a process can never finish a migration. I did not confirm the caller set; this must be checked before A is considered viable.

### 4.2 Option B — extract the pass's logging/metrics recorder (chosen)

One copy of the log+metric code, two behaviourally-distinct call sites. Nothing in ADR-0076's ordering, the lease shape, the termination predicate, the pump, or the ToolGate window changes.

**The gate trap that dictates the shape.** `LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners` (`:35-50`) groups by the **outermost** declaring type (`OwnerOf`, `:141-150`) and forbids overlapping `[min,max]`. If only the *pass* ids (1002, 1003, 1005, 1007) move to the reporter, `EmbedDrainService` keeps 1004 and 1006 → owner ranges `[1004-1006]` and `[1002-1010]` **overlap → RED**. This is precisely the wedge the registry documents for 416, 418/419 and 424/425 (`docs/reference/logging-event-ids.md:45`, `:46`, `:49`). **So the whole nested `Log` class moves, or none of it does.** It moves.

**Where it lives:** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs`, namespace `AiRaccoon.Infrastructure.Embedding` — same namespace as both call sites, so no layering edge is added (`.ai-badger/invariants/clean-architecture-layering.md`). It is a **sealed class, not static**: it holds `IMeasurementRecorder` and `TimeProvider`, which `.ai-badger/invariants/static-classes.md` forbids in a static class.

**Each method takes the caller's `ILogger`** — not an injected `ILogger<EmbedDrainReporter>`. Two reasons: `[LoggerMessage]` methods take `ILogger` as a parameter anyway (`.ai-badger/invariants/high-performance-logging.md`), and it keeps the log **category** as the emitting path. Injecting the reporter's own logger would rewrite every existing 1002-1007 line's category from `AiRaccoon.Infrastructure.Embedding.EmbedDrainService` — the exact string in the owner's excerpt — breaking operator greps for zero gain.

```csharp
// src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs
namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>The embed drain's one log + metric surface. Both drainers report through this:
/// EmbedDrainService's bounded pump pass and EntryEmbedder's lease-held migration drain.</summary>
public sealed partial class EmbedDrainReporter(IMeasurementRecorder measurements, TimeProvider timeProvider)
{
    /// <summary>Progress heartbeat stride: the lease TTL, so a drain that renews is a drain that reports.</summary>
    internal static TimeSpan ProgressStride => SqliteModelMigrationLease.LeaseTtl;

    public void PassStarted(ILogger logger, EmbedCorpus corpus) => Log.DrainStarted(logger, corpus);

    public void MigrationStarted(ILogger logger, EmbedCorpus corpus, long owed) =>
        Log.MigrationDrainStarted(logger, corpus, owed);

    public void MigrationResumedAfterStall(ILogger logger, EmbedCorpus corpus, string previousOwner, TimeSpan age) =>
        Log.MigrationDrainResumedAfterStall(logger, corpus, previousOwner, age);

    public void MigrationLeaseHeld(ILogger logger, EmbedCorpus corpus) => Log.MigrationDrainLeaseHeld(logger, corpus);
    public void MigrationAlreadyFinished(ILogger logger, EmbedCorpus corpus) => Log.MigrationAlreadyFinished(logger, corpus);
    public void MigrationNoProvider(ILogger logger, EmbedCorpus corpus) => Log.MigrationDrainNoProvider(logger, corpus);
    public void MigrationProgress(ILogger logger, EmbedCorpus corpus, int rows, TimeSpan elapsed) =>
        Log.MigrationDrainProgress(logger, corpus, rows, elapsed);

    public void PassFailed(ILogger logger, EmbedCorpus corpus, Exception exception) => Log.DrainFailed(logger, corpus, exception);
    public void SkippedCoalesced(ILogger logger) => Log.DrainSkippedCoalesced(logger);
    public void InvalidRowsPerRun(ILogger logger, string value, int max) => Log.InvalidRowsPerRunSetting(logger, value, max);

    /// <summary>The finish line and its two series — one computation, two destinations (WP11).
    /// Both call sites land on drain.&lt;corpus&gt;.rows / .duration_ms with the same corpus tag.</summary>
    public void PassFinished(ILogger logger, EmbedCorpus corpus, int rows, TimeSpan elapsed)
    {
        Log.DrainFinished(logger, corpus, rows);
        var corpusName = corpus.ToString().ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        measurements.Record(new Measurement(MetricsConfigKeys.DrainRowsMetricName(corpusName),
            MeasurementKind.Histogram, rows, "count", now, MetricsConfigKeys.SelfMetricsProjectId));
        measurements.Record(new Measurement(MetricsConfigKeys.DrainDurationMetricName(corpusName),
            MeasurementKind.Histogram, elapsed.TotalMilliseconds, "ms", now, MetricsConfigKeys.SelfMetricsProjectId));
    }

    private static partial class Log
    {
        // 1002-1007 move here VERBATIM from EmbedDrainService — same ids, same levels, same templates.
        // Splitting the block would leave two overlapping owners, which
        // LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners forbids.
        [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Embed drain pass started for {Corpus}")]
        public static partial void DrainStarted(ILogger logger, EmbedCorpus corpus);
        // … 1003, 1004, 1005, 1006, 1007 unchanged … then the new ids of §5.
    }
}
```

Call site 1 — `EmbedDrainService`, behaviour identical, ordering preserved:

```csharp
// EmbedDrainService.DrainOnceAsync, replacing :109, :134, :142-143
reporter.PassStarted(logger, request.Corpus);
// …
if (drained >= rowsPerRun && !pump.TryEnqueue(request)) reporter.SelfReSignalNotQueued(logger, request.Corpus);

// RecordRows MUST stay before Succeeded() (#548 review, B1 — EmbedDrainService.cs:137-139).
pass.RecordRows(drained);
pass.Succeeded();
reporter.PassFinished(logger, request.Corpus, drained, timeProvider.GetElapsedTime(startedAt));
```

Call site 2 — `EntryEmbedder.DrainMigrationAsync`, the whole point of the lane:

```csharp
// EntryEmbedder becomes `public sealed class` still (no Log class of its own — it borrows the
// reporter's), taking EmbedDrainReporter + ILogger<EntryEmbedder> + IOperationTelemetry.
public async Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
{
    const EmbedCorpus corpus = EmbedCorpus.Memory;
    var state = await ReadOpenMigrationStateAsync(connection, cancellationToken).ConfigureAwait(false); // one indexed row
    if (state is null) { reporter.MigrationAlreadyFinished(logger, corpus); return false; }        // 1011

    if (!await migrationLease.TryAcquireAsync(connection, cancellationToken).ConfigureAwait(false))
    { reporter.MigrationLeaseHeld(logger, corpus); return false; }                                  // 1010 — was silent :102-105

    using var pass = telemetry.Begin(EmbedDrainService.OperationName);
    var startedAt = timeProvider.GetTimestamp();
    var drained = 0;
    try
    {
        if (state.LeaseWasStale) reporter.MigrationResumedAfterStall(logger, corpus, state.PreviousOwner!, state.Age); // 1009
        if (!await HasProviderAsync(connection, cancellationToken).ConfigureAwait(false))
        { reporter.MigrationNoProvider(logger, corpus); return false; }                              // 1012 — was a 15s throw-loop

        var owed = await CountPendingAsync(connection, cancellationToken).ConfigureAwait(false);
        reporter.MigrationStarted(logger, corpus, owed);                                            // 1008 — Information

        await ReconcileVecDimensionsAsync(connection, cancellationToken).ConfigureAwait(false);
        var nextReport = timeProvider.GetUtcNow() + EmbedDrainReporter.ProgressStride;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = /* …unchanged :121-122… */;
            if (batch.Count == 0) break;
            drained += await EmbedAsync(connection, batch, cancellationToken).ConfigureAwait(false);
            await migrationLease.TryRenewAsync(connection, cancellationToken).ConfigureAwait(false);
            if (timeProvider.GetUtcNow() >= nextReport)                                              // 1013 — bounded by TIME, not rows
            {
                reporter.MigrationProgress(logger, corpus, drained, timeProvider.GetElapsedTime(startedAt));
                nextReport = timeProvider.GetUtcNow() + EmbedDrainReporter.ProgressStride;
            }
        }

        await connection.ExecuteAsync(/* FinishModelMigration, unchanged :132-134 */).ConfigureAwait(false);
        if (drained > 0) pass.NoteWork();
        pass.RecordRows(drained);
        pass.Succeeded();
        reporter.PassFinished(logger, corpus, drained, timeProvider.GetElapsedTime(startedAt));       // 1003 + drain.memory.*
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        pass.Failed(ex);
        reporter.PassFailed(logger, corpus, ex);                                                     // 1005 — S6's gap, closed here
        throw;
    }
    finally { await migrationLease.ReleaseAsync(connection, cancellationToken).ConfigureAwait(false); }
}
```

### 4.3 Verdict

**Option B.** Option A is the more elegant story and the wrong engineering call: it converts a bounded-outage design into an unbounded one (`ToolGate.cs:25-31` × 373 passes), rewrites ADR-0076's completion predicate, and buys nothing the extraction does not. The binding constraint is *one copy of the logging/metrics code*, and B satisfies it exactly — `EmbedDrainReporter.PassFinished` is the sole site that emits 1003 and the sole site that writes `drain.<corpus>.*`, for both drainers. The two drain *loops* remain two, which is correct: one is a bounded pump pass with a self re-signal, the other is a lease-held whole-backlog drain that must finish before the ToolGate reopens. That is behavioural difference, not duplication — `.ai-badger/invariants/derive-or-delete-the-list.md` targets the second copy of a *helper*, and after this refactor there is exactly one.

---

## 5. EVENT IDS

**Free range: 1008 and up.** Measured, not inferred, using the registry's own procedure (`docs/reference/logging-event-ids.md:91-99`): 168 `EventId = ` occurrences in `src/`, `uniq -d` prints nothing, highest id **1007**. The registry's last table row is `1002-1007 | …/EmbedDrainService.cs` (`:84`).

New declarations, all inside `EmbedDrainReporter.Log`:

```csharp
[LoggerMessage(EventId = 1008, Level = LogLevel.Information,
    Message = "Embed drain for {Corpus} started under the model migration: {Owed} row(s) owed")]
public static partial void MigrationDrainStarted(ILogger logger, EmbedCorpus corpus, long owed);

[LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
    Message = "Embed drain for {Corpus} resumed a model migration opened {Age} ago; its previous holder '{PreviousOwner}' stopped renewing the lease")]
public static partial void MigrationDrainResumedAfterStall(ILogger logger, EmbedCorpus corpus, string previousOwner, TimeSpan age);

[LoggerMessage(EventId = 1010, Level = LogLevel.Debug,
    Message = "Embed drain for {Corpus} skipped: another relay pass holds the model migration lease")]
public static partial void MigrationDrainLeaseHeld(ILogger logger, EmbedCorpus corpus);

[LoggerMessage(EventId = 1011, Level = LogLevel.Debug,
    Message = "Embed drain for {Corpus} skipped: the model migration was already finished by another relay pass")]
public static partial void MigrationAlreadyFinished(ILogger logger, EmbedCorpus corpus);

[LoggerMessage(EventId = 1012, Level = LogLevel.Warning,
    Message = "Embed drain for {Corpus} cannot run: no embedding provider is configured; the model migration stays open and bank tools remain refused")]
public static partial void MigrationDrainNoProvider(ILogger logger, EmbedCorpus corpus);

[LoggerMessage(EventId = 1013, Level = LogLevel.Information,
    Message = "Embed drain for {Corpus} under the model migration: {Rows} row(s) in {Elapsed}")]
public static partial void MigrationDrainProgress(ILogger logger, EmbedCorpus corpus, int rows, TimeSpan elapsed);
```

**Finish and failure deliberately reuse 1003 and 1005** — same ids, same templates, same series. An operator grepping `1003` or `Embed drain pass finished for Memory` then covers both drainers, which is the whole point; ADR-0076's own precedent for reusing an id rather than minting a near-duplicate is recorded at `docs/reference/logging-event-ids.md:55` (513 reused by the on-demand poll loop).

### `docs/reference/logging-event-ids.md` — exactly two edits

1. **`:12`** — `**168**` → `**174**` (six new methods). This is what `DocumentedCount_MatchesTheMeasuredCount` (`LoggerMessageEventIdTests.cs:68-75`) parses, via the regex `\*\*(\d+)\*\*\s+`\[LoggerMessage\]`` at `:99`.
2. **`:84`** — replace the row so it covers the new span and the relocation:

> `| 1002-1013 | src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs (relocated 2026-08-26 from EmbedDrainService.cs — the whole nested Log class moved so the migration relay reports through the same declarations instead of a second copy; moving only the pass ids would have left EmbedDrainService owning 1004/1006 inside the reporter's 1002-1013 span, which EventIdBlocks_DoNotInterleaveBetweenOwners forbids, same wedge as 416/418/424. 1002-1007 unchanged in behavior, only the owning type moved. 1008-1013 new, LANE P4: the migration relay had no log line at all — 1008 the drain starting with rows owed, 1009 a stale lease reclaimed from a dead holder (the shape PromotionQueueService's 709 already logs for promotion claims), 1010 the lease held by another relay pass (was a silent `return false`), 1011 the migration already finished by another pass (also silent), 1012 no embedding provider configured — previously an exception rethrown every 15s poll with the bank locked by ToolGate, 1013 a time-strided progress heartbeat at the lease TTL so a 47k-row drain reports ~18 lines rather than 1,492) |`

### What the pinning test requires

| Test | Requirement | How this lane satisfies it |
|---|---|---|
| `EventIds_AreUniqueAcrossTheAssemblies` `:18-31` | no duplicate id in any product assembly | 1008-1013 unused; verified by the registry's own `uniq -d` (prints nothing) and max = 1007 |
| `EventIdBlocks_DoNotInterleaveBetweenOwners` `:35-50`, owner = **outermost** type (`:141-150`) | no two owners' `[min,max]` may overlap | **the binding constraint on the refactor**: move all six of 1002-1007 or none. After the move there is one owner, `EmbedDrainReporter`, spanning 1002-1013; `EmbedDrainService` and `EntryEmbedder` declare no `[LoggerMessage]` at all |
| `DocumentedCount_MatchesTheMeasuredCount` `:68-75` | prose count == reflected count | edit 1 above: 168 → 174 |
| `EveryEventIdInSource_FallsInsideADocumentedBlock` `:78-92`, parser `:105-126` | every source id inside a `^\|<digits>\|` block | edit 2 above; `| 1002-1013 |` expands correctly through the `-` branch at `:114-120` |
| `TheGuard_CoversEveryProductAssembly` `:54-61` | assembly list unchanged | no new project |

**Two adjacent gates that do *not* fire, checked so nobody assumes they do:** `OtlpNamesRegistryTests` (`:28-35`) only walks `Meter`/`ActivitySource` members — the reporter uses `IMeasurementRecorder`, so nothing to register. `BackgroundInstrumentationCoverageTests` (`:23-35`, `:46-57`) walks `IHostedService` implementors only; `EntryEmbedder` is not one, so adding its `IOperationTelemetry` scope is right but not gate-forced.

---

## 6. WORK PACKAGES

Every WP is test-first. Gate filters are the repo's real ones (`build.yml:134` runs `--filter "Speed=Fast&Performance!=Benchmark"`; trait values from `TestCategories.cs`). Log assertions use `FakeLogger<T>` + `logger.Collector.GetSnapshot()`, the pattern already in `BundledModelLoggingTests.cs:20,30`; metric assertions use `RecordingMeasurementRecorder`, already in `EmbedDrainMetricsTests.cs:40`; clocks use `FakeTimeProvider` (`EmbedDrainMetricsTests.cs:30`).

**WP-P4-1 — extract `EmbedDrainReporter` (pure move, zero behaviour change).**
Files: **new** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs`; `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs`; `src/AiRaccoon/Setup/AppRegistrations.cs`.
RED first: `EmbedDrainReporterTests` asserting 1002/1003 fire with the corpus tag and that `drain.code.rows` / `.duration_ms` land — written against the type before it exists.
AC: after the move, `EmbedDrainService` emits the **same ids, levels, templates, categories and series** as before — the category assertion is the load-bearing one, because it pins the decision that reporter methods take the caller's `ILogger` rather than their own; and `EmbedDrainService.cs` contains no `[LoggerMessage]`.
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`.

**WP-P4-2 — migration drain start / finish / failure through the reporter (1008; reuse 1003, 1005).**
Files: `EntryEmbedder.cs`; `EmbedDrainReporter.cs` *(shared with WP-1 → serialises)*; `AppRegistrations.cs` *(shared with WP-1 → serialises)*.
RED first: `EntryEmbedderMigrationDrainReportingTests` — drain N rows under an open migration, assert 1008 then 1003 with `Corpus=Memory` and `drain.memory.rows == N`.
AC: a migration drain that embeds N rows emits 1008 (Information, with rows owed) and 1003 (Information, `{Rows}` = N), records both `drain.memory.*` series, and opens one `embed.drain` telemetry scope with `RecordRows` **before** `Succeeded` (`EmbedDrainService.cs:137-139`).
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`.

**WP-P4-3 — the three silent returns (1010, 1011, 1012).**
Files: `EntryEmbedder.cs` *(shared with WP-2 → serialises)*; `EmbedDrainReporter.cs` *(shared → serialises)*.
RED first: three tests — lease held by a foreign owner; migration closed between due-check and run; blank provider.
AC: lease contention returns `false` **and** emits 1010; a closed migration returns `false` and emits 1011; a blank provider returns `false` without throwing, emits 1012, and leaves the migration open (`finished_at` still NULL). The blank-provider test is the `prove-the-check-fails` one: on `main` it goes red by *throwing*, which is the defect.
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`.

**WP-P4-4 — stale-lease reclaim (1009). Reproduces the owner's bank.**
Files: `EntryEmbedder.cs` *(serialises with 2, 3)*; `EmbedDrainReporter.cs` *(serialises)*; `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` (one new single-row SELECT beside `HasOpenModelMigration`, `:474-475`).
RED first: seed `model_migration` id=1 with `finished_at IS NULL`, `lease_owner='dead:1:x'`, `lease_expires_at` in the past — the owner's exact state — and assert the next drain emits 1009 at Warning naming the previous owner and the age, then proceeds and finishes.
AC: as above; and a drain acquiring a *free* lease (`lease_owner IS NULL`) emits **no** 1009.
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`.

**WP-P4-5 — bounded progress heartbeat (1013).**
Files: `EntryEmbedder.cs` *(serialises)*; `EmbedDrainReporter.cs` *(serialises)*.
RED first: drain 5 × `BatchSize` rows on a `FakeTimeProvider`; advance past `SqliteModelMigrationLease.LeaseTtl` between two batches.
AC: exactly one 1013 when the clock crosses one stride; **zero** 1013 when the whole drain fits inside one stride. This is the anti-flood assertion — it is what makes the "1,492 lines" outcome unreachable.
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`.

**WP-P4-6 — the registry (docs).**
Files: `docs/reference/logging-event-ids.md` **only** — no shared file, so it is authorable in parallel, but it **must merge in the same PR** as WP-1…5: `DocumentedCount_MatchesTheMeasuredCount` and `EveryEventIdInSource_FallsInsideADocumentedBlock` are hard gates that go red the instant a new id lands undocumented, and equally red if the doc lands ahead of the code.
AC: count `**174**`; the `1002-1013 | …EmbedDrainReporter.cs` row present; both id tests green.
Gate: `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~LoggerMessageEventIdTests"`.

**Serialisation, stated plainly: this lane does not parallelise.** WP-1…5 all touch `EmbedDrainReporter.cs`; WP-2…5 all touch `EntryEmbedder.cs`. One worktree, one lane, sequential commits (`.ai-badger/invariants/small-commits-early-draft-pr.md`).

---

## 7. RISKS + SIMPLER SHAPE

**Log volume on a 47k-row drain.** A per-batch line floods: 47,723 ÷ 32 (`EntryEmbedder.cs:24`) = **1,492 lines**, and it would also fire 1,492 measurement records against a buffer capped at 1,000 by default (`MetricsConfigKeys.cs:8`), i.e. guaranteed `metrics.dropped`. Option A's per-pass alternative is 47,723 ÷ 128 = **373** — still noise. The chosen shape is **time-strided**: one 1013 per `SqliteModelMigrationLease.LeaseTtl` (60 s, `IModelMigrationLease.cs:34`) → **~18 lines for the owner's 17.5-minute drain**, and O(1) in row count. The stride is *derived* from a period the code already has a meaning for, not a fresh magic number (`.ai-badger/invariants/derive-or-delete-the-list.md`). Progress lines record **no** measurement, per `IOperationScope`'s "exactly one duration + outcome measurement" contract (`IOperationTelemetry.cs:15-21`), so metric volume stays at **1 point per drain**.

**Metric cardinality: unchanged.** `DrainRowsMetricName(corpus)` / `DrainDurationMetricName(corpus)` (`MetricsConfigKeys.cs:77`, `:80`) have corpus as their only dimension, and `EmbedCorpus` has exactly two values (`EmbedDrainRequest.cs:9-13`). Reusing the series adds points, not series. A `migration` dimension was considered and rejected: it would be a hand-parallel `drain.*` family, exactly what `InternalSeriesPrefixes` (`MetricsConfigKeys.cs:68-74`) exists to prevent. **Honest cost:** `drain.memory.rows` becomes bimodal (128 vs 47,723). Mitigations — 1008 carries the owed count so the operator never needs the histogram to get the number, and 28-day retention (`MetricsConfigKeys.cs:22-24`) ages the outlier out. A migration is a once-per-engine-change event, so it is a handful of points.

**ADR-0076 ordering.** Option B changes nothing in the outbox transaction (`EntryEmbedder.cs:50-88`), the `MarkAllEmbeddedPending` side effect (`:75-76`, `MemorySql.cs:397-399`), or the finish write (`:132-134`). One ordering constraint carries over into the moved code and must not be re-sorted: `pass.RecordRows(drained)` **before** `pass.Succeeded()` — `EmbedDrainService.cs:137-139` records that `Succeeded()` claims the scope's one measurement, so a rows value set afterwards lands on a spent scope and the OTLP histogram never sees it. A silent regression; WP-1's and WP-2's ACs both pin it.

**Event-pump coalescing.** Option B does not touch the pump. Under Option A the migration would share the `EmbedDrainRequest(Memory)` coalesce key (`EmbedDrainRequest.cs:15-20`) with `PendingEmbedJob.cs:50`, so the two signals collapse and the pass loses the ability to know it is a migration pass — a further cost of A, recorded in §4.1.

**Pre-existing concurrency finding, flagged not fixed.** `PendingEmbedJob.HasWorkAsync:29-40` has no migration gate and `EmbedPendingBatchAsync:225-231` takes no lease, so **two Memory drainers can run against the same pending rows today** — the pump's bounded pass and the lease-held relay. Not corruption (`MarkEmbedded` is per-row idempotent), but it means `drain.memory.*` and 1003 already have two producers, which is part of why the owner's Memory lines were expected and unattributable. Out of this lane's scope; it belongs to whoever owns ADR-0076's exclusivity next.

**`ask-if-simpler` minimum version** (`.ai-badger/invariants/ask-if-simpler.md`):

> **WP-1 + WP-2 + WP-6, and nothing else.** One new event id — **1008**, Information, with the rows owed — plus reuse of the existing **1003** for the finish, both emitted from inside `DrainMigrationAsync` through the extracted reporter. Drop 1009-1013 and the heartbeat.
>
> That is ~40 lines of production change, one registry row, and one count bump, and it already converts `"ran in 1 ms"` from ambiguous to decisive: **an Information start line with no finish line ⇒ a drain is in flight or its holder died; no start line at all ⇒ the relay never acquired.** That single bit is what the owner was missing.
>
> The extraction (WP-1) is **not** optional even in the minimum: without it, giving the migration path a start/finish line means a second copy of the logging and metrics code, which is the one thing the binding constraint forbids. And WP-1 is what forces the whole-block move and the registry edit, so the floor genuinely is these three.
>
> Of the rest, **1013** (heartbeat) and **1009** (stale-lease reclaim) pay for themselves next — 1009 is the line that would have named the owner's current bank state out loud. **1010/1011** are Debug and can wait indefinitely. **1012** is the odd one out: it is not observability, it is a liveness fix (a permanent 15s throw-loop with the bank ToolGate-locked, §2 S3) and arguably belongs in a defect PR of its own rather than in this lane.

---

## SCHEMA-LAST

### Table 1 — path × signal, today vs after

| path | corpus | start log | finish log + rows | failure log | metrics | gap |
|---|---|---|---|---|---|---|
| FileIngestor signal `FileIngestor.cs:188` (today & after) | Memory | — | — | — | — | none — a `TryEnqueue` is not a pass; the drain reports |
| FileIngestor signal `FileIngestor.cs:193` (today & after) | Code | — | — | — | — | none, same reason |
| PendingEmbedJob `PendingEmbedJob.cs:48-52` (today & after) | Memory | 525 `MaintenanceJobRunner.cs:99` | 525 duration only | 526 `:150-152` | `job.pending-embed.duration_ms`, `.rows` | `.rows` never recorded when provider blank (`:70-74` gate) — doctor lanes |
| CodeReindexJob `CodeReindexJob.cs:47-51` (today & after) | Code | 525 | 525 duration only | 526 | `job.code-reindex.duration_ms`, `.rows` | none |
| EmbedDrainService pass (today) | Code | 1002 **Debug** `:109` | 1003 Info + `{Rows}` `:142` | 1005 Warn `:97` (loop only) | `drain.code.rows`, `drain.code.duration_ms` | 1005 unreachable for a direct `DrainOnceAsync` caller (S6) |
| EmbedDrainService pass (today) | Memory | 1002 **Debug** | 1003 Info + `{Rows}` | 1005 Warn | `drain.memory.rows`, `.duration_ms` | Debug start ⇒ a slow pass is invisible at Information for its whole duration |
| **Migration relay `EntryEmbedder.cs:94-141` (today)** | Memory | **none** | **none — bool discarded `ModelMigrationJob.cs:38`** | **none** | **none** | **total: no start, finish, rows, failure, progress, metric or span; 17.5 min of work unlogged** |
| EmbedDrainService pass (after) | Code | 1002 Debug (via reporter) | **1003 via `EmbedDrainReporter.PassFinished`** | 1005 via reporter | `drain.code.*` — unchanged names | none; ids/levels/templates/category byte-identical (WP-1 AC) |
| EmbedDrainService pass (after) | Memory | 1002 Debug (via reporter) | **1003 via the same method** | 1005 via reporter | `drain.memory.*` — unchanged | Debug start remains for the bounded pass — deliberate; the migration path gets an Information start instead |
| **Migration relay (after)** | Memory | **1008 Information + `{Owed}`** | **1003 + `{Rows}`, same method, same series** | **1005 + `pass.Failed(ex)`** | **`drain.memory.rows`, `.duration_ms` — the same two series, one copy of the code** | closed. Plus 1009 stale-lease Warn, 1010/1011 skip Debug, 1012 no-provider Warn, 1013 heartbeat ≤1 per 60 s lease TTL, and an `embed.drain` span |

### Table 2 — work packages

| WP | files | depends on | acceptance criterion | gate |
|---|---|---|---|---|
| P4-1 extract `EmbedDrainReporter` | **new** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs`; `EmbedDrainService.cs`; `src/AiRaccoon/Setup/AppRegistrations.cs` | — | 1002-1007 all move; `EmbedDrainService` emits identical ids/levels/templates/**category**/series; `EmbedDrainService.cs` holds no `[LoggerMessage]` | `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"` |
| P4-2 migration start/finish/failure | `EntryEmbedder.cs`; `EmbedDrainReporter.cs` *(shared → serialises with P4-1)*; `AppRegistrations.cs` *(shared → serialises)* | P4-1 | a drain of N rows emits 1008 then 1003 (`Corpus=Memory`, `{Rows}`=N), records both `drain.memory.*`, opens one `embed.drain` scope with `RecordRows` before `Succeeded` | same filter |
| P4-3 silent returns 1010/1011/1012 | `EntryEmbedder.cs` *(shared → serialises with P4-2)*; `EmbedDrainReporter.cs` *(shared)* | P4-2 | lease held ⇒ `false` + 1010; migration closed ⇒ `false` + 1011; blank provider ⇒ `false` + 1012, no throw, `finished_at` still NULL (red on `main` by throwing) | same filter |
| P4-4 stale-lease reclaim 1009 | `EntryEmbedder.cs` *(shared → serialises)*; `EmbedDrainReporter.cs` *(shared)*; `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` | P4-3 | OPEN row + non-null `lease_owner` + past `lease_expires_at` (the owner's exact state) ⇒ 1009 Warn naming holder + age, then drain proceeds; a free lease ⇒ no 1009 | same filter |
| P4-5 bounded heartbeat 1013 | `EntryEmbedder.cs` *(shared → serialises)*; `EmbedDrainReporter.cs` *(shared)* | P4-4 | exactly one 1013 per `LeaseTtl` crossed on a `FakeTimeProvider`; **zero** when the drain fits inside one stride | same filter |
| P4-6 registry | `docs/reference/logging-event-ids.md` *(no shared file; must merge in the same PR)* | P4-1…P4-5 | count `**168**`→`**174**`; row `1002-1007 \| …EmbedDrainService.cs` → `1002-1013 \| …EmbedDrainReporter.cs` with the relocation note | `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~LoggerMessageEventIdTests"` |