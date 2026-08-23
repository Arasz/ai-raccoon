# 0091. The event pump never blocks a producer

Date: 2026-08-22

Status: Accepted

Owner ruling G17 (CHANGE), `docs/work/2026-08-22-post-delta-3-wp11-feedback.md:17-23`: *"We want to
use one system, based on bounded channels - not a semaphore - exactly the same solution as for
metrics... I want you to extract the channel based events pump."* Ratified in this session:
document it as ADR-0091. Amends ADR-0076 (`0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md`)
— **precisely**: `PendingEmbedJob`/`CodeReindexJob` keep ADR-0076's on-demand-relay shape
(`IMaintenanceJob.HasWorkAsync`, gated on the row, not the clock) as producers, but no longer perform
the drain that shape used to run inline — `EmbedDrainService`, the pump's single consumer, does that
now (Decision 4). `ModelMigrationJob` — the specific relay ADR-0076 names for `model set`
(`src/AiRaccoon.Infrastructure/Maintenance/ModelMigrationJob.cs:7-12`, *"The relay half of a model
migration (ADR-0076)"*) — is unchanged and does not go through the pump; it still drains
`model_migration` directly via `IEntryEmbedder.DrainMigrationAsync` under its own lease. What amends
is the general on-demand-relay-over-a-durable-flag pattern ADR-0076 established for `embed_state`,
not that one specific relay.

## Context

WP11-B1/B2 (`docs/work/2026-08-22-post-delta-3-plan.md:527-826`, PRs #490/#507) extracted the
metrics writer's hand-rolled bounded-queue-with-drop (ADR-0074) into a shared
`AiRaccoon.Core.EventPump<T>` and built a second topic — embed-drain signalling — on it. Two
producer families exist today with different tolerance for a dropped signal: metrics (every
measurement is distinct data, dropping one loses that data point) and embed (the signal is a
wake-up over a durable outbox row, dropping it only delays a poll). Both currently go through the
same `TryEnqueue` contract, and the plan's Finding (c) traced why that is safe for both
(`docs/work/2026-08-22-post-delta-3-plan.md:630-678`). This ADR records that contract as a decision,
not an artifact of what happened to get built first, so a future producer with a real must-not-drop
requirement does not silently start calling `TryEnqueue` in a loop or reach for
`ChannelWriter.WriteAsync` on the same instance.

## Decision

**1. `TryEnqueue` never blocks a producer.** `EventPump<T>.TryEnqueue`
(`src/AiRaccoon.Core/EventPump/EventPump.cs:44-66`) calls `ChannelWriter.TryWrite` only, never
`WriteAsync`. `IEventPump<T>.TryEnqueue` states the contract directly:
*"Never blocks, never throws"* (`src/AiRaccoon.Core/EventPump/IEventPump.cs:22-23`). `WriteAsync`
against a channel built with `BoundedChannelFullMode.Wait` (the channel *is* built that way — see
Decision 2) would suspend the caller until a reader drains a slot; every current producer runs
inline in a request or a maintenance pass (`MetricsRecorder.Record`,
`src/AiRaccoon.Infrastructure/Metrics/MetricsRecorder.cs:14-24`; `WatchDigestExecutor.DigestAsync`,
`src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs:91-95`; `PendingEmbedJob.RunAsync`,
`src/AiRaccoon.Infrastructure/Maintenance/PendingEmbedJob.cs:47-51`), so suspending it would make a
full pump into caller-visible backpressure on a memory write, a watch digest, or a maintenance poll
— exactly the synchronous-write-on-the-hot-path defect ADR-0074's gate G4 exists to keep out.

**2. `BoundedChannelFullMode.Wait` plus `TryWrite`-only reproduces `MeasurementBuffer`'s original
contract byte-for-byte; every `Drop*` mode was rejected, not only `DropWrite`.** `EventPump<T>`'s
constructor (`EventPump.cs:27-36`) builds the channel with `FullMode = BoundedChannelFullMode.Wait`
(`EventPump.cs:29-32`) but that mode only governs `WriteAsync`, which the pump never calls — the
mode is inert by construction, and an `Interlocked` reservation ahead of the channel
(`EventPump.cs:52-58`) is the actual cap enforcement, letting `ApplyCapacity` move the effective cap
at runtime without rebuilding the channel (Decision 3). **Why `Wait` is the only mode that fits, not
a preference among equals.** The pump's contract is: `TryEnqueue` returns `false` on a full pump and
`DroppedCount` is incremented at that call site (`EventPump.cs:56-61`), readable via
`IEventPump<T>.DroppedCount` (`IEventPump.cs:13-17`) — a distinct signal from `MetricsRecorder`'s own
EventId **960**, which fires only when `buffer.TryEnqueue` itself throws
(`src/AiRaccoon.Infrastructure/Metrics/MetricsRecorder.cs:14-23,28-30`; the `bool` it returns is
deliberately ignored there, per the plan's Finding (c),
`docs/work/2026-08-22-post-delta-3-plan.md:647-649` — a dropped-because-full measurement is counted
in `DroppedCount`, not logged per-item). What the plan's Finding (c) named as the property to
preserve is the boolean itself (*"full → `TryWrite` returns `false` immediately, nothing blocks,
nothing is silently discarded behind the caller's back"*,
`docs/work/2026-08-22-post-delta-3-plan.md:673-676`). Three considered alternatives, all rejected:

  a. **`FullMode` = `DropNewest`/`DropOldest`/`DropWrite`, with a `Channel.CreateBounded(options,
     itemDropped)` callback to observe the drop.** `Channel.CreateBounded<T>` does accept an
     `Action<T> itemDropped` overload under every `Drop*` mode, so the dropped item is observable —
     that is not the defect. The defect is `TryWrite`'s own return value: under every `Drop*` mode
     `TryWrite` reports the write as **successful** (Microsoft's own docs:
     `learn.microsoft.com/dotnet/core/extensions/channels#bounding-strategies` — `DropWrite`
     *"drops the item being written"* while the write still completes; `DropNewest`/`DropOldest`
     *"removes and ignores the newest/oldest item in the channel in order to make room for the item
     being written"*, i.e. the incoming write succeeds by evicting a different item — confirmed in
     this project by trial for `DropWrite`: it made `TryWrite`/`WriteAsync` return `true` on a full
     channel while silently dropping the item, took `MeasurementBufferTests` red,
     `docs/work/2026-08-22-post-delta-3-plan.md:676-678`). `TryEnqueue`'s `bool` return is the
     caller-facing contract (`IEventPump<T>.TryEnqueue`, `IEventPump.cs:22-23`) and
     `MetricsRecorder`/`EmbedDrainService`'s callers key off it directly at the call site, not off a
     side channel; wiring `DroppedCount` through an `itemDropped` callback instead would decouple
     the count from the very call that needs to report `false`, for no gain the `Wait` shape doesn't
     already give for free.
  b. **Any `Drop*` mode, independent of the callback question.** `ApplyCapacity`'s runtime-mutable
     soft cap (Decision 3) is enforced entirely by the `Interlocked` reservation ahead of the
     channel — the channel's own bound (`topic.Ceiling`) is fixed at construction and never
     rebuilt. No `Drop*` mode can express a cap that changes after construction; only the
     reservation-in-front-of-`Wait` shape lets `Capacity` move without touching the channel at all.
  c. **`AllowSynchronousContinuations = true`, for throughput.** `EventPump<T>`'s constructor never
     sets this option, so it stays at the BCL default of `false`
     (`learn.microsoft.com/dotnet/api/system.threading.channels.channeloptions.allowsynchronouscontinuations`).
     Kept, not merely defaulted: `true` would let a producer's own thread run
     `WaitForItemAsync`'s continuation — a watch digest or a maintenance-poll thread would then be
     the one executing `EmbedDrainService`'s drain, exactly the caller-visible coupling Decision 1
     exists to keep out.

**3. One pump type, one instance per topic; `PumpTopic` is the topic's whole configuration.**
`PumpTopic(int Ceiling, int Capacity, bool Coalesce)` (`src/AiRaccoon.Core/EventPump/PumpTopic.cs:10`)
is the entire per-topic surface. `Ceiling` fixes the channel's own bound at construction and never
changes; `Capacity` is the starting effective soft cap enforced by the `Interlocked` reservation, and
`ApplyCapacity` (`EventPump.cs:84`) can shrink or grow it at runtime without rebuilding the channel —
proven not to cost pre-allocation: `Channel.CreateBounded<T>` allocates the same 752 bytes whether
built at capacity 10 or 1,000,000 (measured with `GC.GetAllocatedBytesForCurrentThread`, PR #490
body). Two topics exist today: `metrics` (`MeasurementBuffer`,
`src/AiRaccoon.Infrastructure/Metrics/MeasurementBuffer.cs:16-17`,
`PumpTopic(MetricsConfigKeys.MaxBufferCapacity, capacity, Coalesce: false)` — every measurement is
distinct data) and `embed` (`EmbedDrainService.PumpCeiling`/`PumpCapacity` = 8/8,
`src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs:41-44`, `Coalesce: true`). A single pump
with round-robin consumers was offered by the owner and rejected for the two topics that exist: they
have different trigger shapes (metrics is time-triggered drain-all, embed is signal-triggered
drain-one), a shared consumer would put a 30-second metrics flush behind the multi-second embed
drain that triggered this whole work package, and the two do not contend for the inference pool in
the first place — metrics costs one batched SQLite write
(`docs/work/2026-08-22-post-delta-3-plan.md:715-731`). The round-robin shape stays additively
available for a future topic that genuinely shares a scarce resource; it would change the drain
loops, not the pump type.

**4. The embed topic is a coalescing wake-up signal over the durable outbox, not a queue of work.**
`embed_state = 'pending'` on each row is ADR-0076's outbox and the durable record; the pump only
tells `EmbedDrainService` a corpus has rows worth checking
(`EmbedDrainService.cs:33-35`: *"The channel is a wake-up, not the record... a signal dropped because
the pump is full costs at most one poll interval of latency and zero rows"*). Exactly one consumer
reads the topic — `EmbedDrainService.ExecuteAsync`'s single `while` loop
(`EmbedDrainService.cs:60-104`) — so the ONNX inference pool has exactly one caller in the process
regardless of how many producers signal it. Producers enqueue and never call an embedder directly:
`WatchDigestExecutor.DigestAsync` (`WatchDigestExecutor.cs:91-95`),
`PendingEmbedJob.RunAsync` (`PendingEmbedJob.cs:47-51`), `CodeReindexJob.RunAsync`
(`src/AiRaccoon.Infrastructure/Maintenance/CodeReindexJob.cs:43-48`) — each `pump.TryEnqueue(new
EmbedDrainRequest(corpus))`. **This holds regardless of which side re-derives the signal.** WP11-C
(in flight, not yet merged as of this ADR) may move `RowsPerRun` to a bank setting and may change
`PendingEmbedJob`/`CodeReindexJob` to enqueue directly from their poll-side pending-row check rather
than through today's `HasWorkAsync`/`RunAsync` split; either shape is: *the poll's pending-row check
signals the topic*, and the topic stays a signal over the outbox, never itself the record of what is
owed. Coalescing key is the record's own structural equality
(`EmbedDrainRequest.cs:15-19`, `Corpus` alone — no project id, because both
`IEntryEmbedder.EmbedPendingBatchAsync` and `ICodeEmbedder.EmbedPendingBatchAsync` are bank-wide) and
is released at `DrainUpTo`'s take (`EventPump.cs:73-76`), not at drain completion, so a signal
arriving mid-drain queues its own fresh pass instead of folding into rows already read
(`docs/work/2026-08-22-post-delta-3-plan.md:777-783`). A lost or coalesced signal is recovered by the
next poll (`BankMaintenanceHostedService.OnDemandPollInterval`, 15 s) — never a lost row, because the
row's own `embed_state` is what is owed, not the signal.

### A dropped signal loses nothing

Nothing an operator cares about is lost when the embed topic drops or coalesces a signal, because
the channel item was never the work — it is a wake-up over a row that was already durably marked
`embed_state = 'pending'` before the signal was ever raised (ADR-0076's outbox). Three tests pin the
three ways this is true, all in `EmbedDrainServiceTests`/`PendingEmbedJobTests`
(plan §WP11-B2 gate table, `docs/work/2026-08-22-post-delta-3-plan.md:892-902`, shipped in #507):

- **A signal enqueues nothing the row didn't already record.** `PendingEmbedJobTests.RunAsync_EnqueuesInsteadOfEmbedding`
  (E7) asserts `RunAsync` calls the embedder zero times and the pump's `EnqueuedCount` becomes 1 — the
  job's `HasWorkAsync` (`PendingEmbedJob.cs:28-39`) is the thing that reads `embed_state = 'pending'`
  off the row; `RunAsync` only signals it.
- **One signal drains the whole budget for that corpus.** `EmbedDrainServiceTests.OneSignal_RunsExactlyOneDrainOfTheRowBudget`
  (E1) asserts one drain call with `limit == 128` (`RowsPerRun`) per signal — a signal is a trigger to
  check the outbox, not a count of rows.
- **A coalesced or dropped signal costs one poll interval, never a row.** `EmbedDrainServiceTests.CoalescedSignal_IsRecoveredByTheNextPoll`
  (E11, shipped name — the plan's design doc named it `DroppedSignal_IsRecoveredByTheNextPoll`
  before the topic's actual shape was measured, see below) asserts `PendingEmbedJob.HasWorkAsync`
  still reports `true` off the row itself after a signal is coalesced away, and the next 15 s poll's
  own `TryEnqueue` picks the row back up.

**On this topic specifically, `DroppedCount` can never increment at all — by construction, not by
tuning.** The embed topic's item space is exactly two values (`EmbedCorpus.Memory`/`Code`,
`EmbedDrainRequest.cs:9-13`), coalescing is on, and capacity is 8 (`EmbedDrainService.cs:41-44`): a
coalescing topic can never hold more than one queued item per distinct key, so at most 2 items can
ever be in flight against a capacity of 8 — the plan's literal design for E11 ("fill the pump to
capacity so the signal drops") is structurally unreachable for this topic, discovered while building
#507 and recorded there as a disclosed deviation. `CoalescedSignal_IsRecoveredByTheNextPoll` proves
the equivalent case this topic's shape actually has: a coalesced (not overflow-dropped) signal still
costs nothing durable, because `HasWorkAsync` reads the row, not the pump. The metrics topic is the
one where `DroppedCount` is real and expected under sustained overload (capacity 1000, no
coalescing) — the two topics differ here exactly because their tolerance for a lost item differs
(Context), and this is the concrete shape that difference takes today.

**5. `EnqueueAsync`/backpressure is an explicit non-decision.** No blocking enqueue exists on
`IEventPump<T>` today, and none is added by this ADR. It is added only when a producer exists whose
correctness (not merely its latency) requires that an item is never dropped — a case neither current
topic has: metrics tolerates a dropped measurement (counted, not corrected), embed tolerates a
dropped signal (recovered by the next poll against the durable outbox). If that producer appears,
the addition is **a separate method, per topic that needs it** — never a change to `TryEnqueue`'s
contract or to `EventPump<T>`'s existing topics. `TryEnqueue`'s never-blocks guarantee
(Decision 1) must stay true for every topic that has it today.

## Consequences

- No topic may reach for `ChannelWriter.WriteAsync` directly, and no topic may call `TryEnqueue` in a
  spin/retry loop to simulate blocking — either defeats Decision 1 for every existing caller of that
  topic, not just the new one.
- A future must-not-drop producer gets a new `EnqueueAsync` (or equivalent) on its own topic, decided
  and reviewed when that producer is actually proposed — not spent here on a contract nothing
  currently needs (`ask-if-simpler`).
- No second embedder, no semaphore, and no drain-until-empty loop on the embed topic without a
  separate owner ruling (G20, `docs/work/2026-08-22-post-delta-3-plan.md:985`) — the single-reader,
  bounded-per-signal shape (`RowsPerRun`, 128 today) is what keeps the inference pool from saturating,
  which is the defect this whole work package exists to close
  (`docs/work/2026-08-22-post-delta-3-plan.md:527-534`).
- The invariant `EnqueuedCount + DroppedCount + CoalescedCount == attempts` holds for every topic by
  construction of `TryEnqueue`'s three exit paths (`EventPump.cs:44-66`) and is pinned by
  `EmbedDrainServiceTests.ParallelProducers_EveryAttemptIsAccountedForExactlyOnce` (64 concurrent
  `TryEnqueue` calls, PR #507 body) — any future topic added to `EventPump<T>` inherits this for free.
- The whole existing metrics test suite (`MeasurementBufferTests`, six facts) stayed green with zero
  edits through the extraction (PR #490 body), which is the evidence this ADR treats as proof that
  `TryEnqueue`-only is behaviour-identical to the pre-extraction `ConcurrentQueue` contract
  (ADR-0074), not merely similar to it.

## Amendment (2026-08-23) — G1/post-delta-4: the consumer re-signals its own topic on a full row budget

The "no drain-until-empty loop … without a separate owner ruling (G20)" line above is answered:
`docs/work/2026-08-23-post-delta-4-plan.md` WP1, owner ruling **G1** (APPROVE), is that ruling.
`EmbedDrainService.DrainOnceAsync` (`EmbedDrainService.cs:104-143`) now re-enqueues its own
`EmbedDrainRequest` when a pass drains rows `>= rowsPerRun` (`:126-129`) — a full row budget means
the backlog may not be empty, and `EmbedPendingBatchAsync` on both corpora counts only rows whose
UPDATE landed (PR #530 review, finding F1: this was already true for `CodeEmbedder` and made true
for `EntryEmbedder` in the same round), so the re-signal is real progress and cannot spin once a
corpus is actually exhausted. This is **not** the rejected drain-until-empty loop: the consumer
still takes exactly one queued item per iteration (Decision 4 stands — coalescing key, single
reader, bounded-per-signal budget all unchanged), it just no longer waits out the 15s on-demand poll
between passes when the prior pass proved there was more to do.

Line references into `EmbedDrainService.cs` elsewhere in this ADR have drifted with that edit and
the class doc rewrite that went with it: the "channel is a wake-up" quote (Decision 4) is now at
`:29-33`, not `:33-35`; `PumpCeiling`/`PumpCapacity` are now at `:46,49`, not `:41,44`; the
`ExecuteAsync` loop is now `:57-101`, not `:60-104`. `EventPump.cs`'s own line references are
untouched — WP1 does not edit that file.
