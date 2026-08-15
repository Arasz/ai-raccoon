# 0074. A capped buffer satisfies the channel rule, and reshapes G4

Date: 2026-08-15

Status: Accepted

## Context

`docs/work/specs/PerformanceMetrics.feature`'s owner-stated rule for the metrics writer names a
mechanism, not just a behaviour: *"use channels, save the metric to the channel then process them
in the background… A full channel drops the incoming measurement and counts the drop… Channel
holding 1000 measurements at most."* spec.json's gate G4 for the same rule reads *"Count bank
writes during the call; assert zero."*

Two problems surfaced building phase one
(`docs/plans/2026-08-15-performance-metrics-implementation.md`, ruling 2 and §5, item D2):

**G4 can never pass as written.** `SqliteMemoryStore.SearchAsync` ends with
`await BumpAccessAsync(…)` on every search — a write, by design (the access-count/rating bump).
And SQLite's `total_changes()` is scoped **per connection**, so a before/after read on a
test-held connection reports `0` regardless of what the search under test did. As stated, G4 is a
gate that passes on every implementation — the exact vacuity ADR-0056 exists to prevent,
reintroduced inside this one.

**The full `Channel.CreateBounded` + `DropWrite` + `itemDropped`-callback machinery is
disproportionate to what this server needs.** It buys an arrival-rate pressure estimator, a 60%
occupancy aim and a 4-second rate-limit floor (spec scenarios 35/36) for a capacity of 1000 fed by
one developer's own agent traffic on a local SQLite file. The repo's only existing channel
(`Watch/WatchPipeline.cs:56`) is `CreateUnbounded` with no drop path — there is no local precedent
to lean on either.

## Decision

**The measurement writer is a capped `ConcurrentQueue<Measurement>`
(`AiRaccoon.Infrastructure.Metrics.MeasurementBuffer`), not a `System.Threading.Channels.Channel`.**
A slot is reserved with `Interlocked.Increment` before the item is enqueued, so two concurrent
callers cannot both pass a stale count check and overrun the cap — the reservation *is* the
enforcement. Past capacity, the measurement is dropped and `DroppedCount` — read by
`MetricsFlusher` into a self-metric — counts it. The buffer sits behind `IMeasurementRecorder`
(port) and `IMeasurementBuffer` (its own seam), so **D2** — swapping in `Channel.CreateBounded` if
the arrival-rate-adaptive flush (D1) is ever built — changes the implementation behind those
interfaces and none of the tests or call sites that assert the *behaviour*: bounded capacity,
whole-batch flush, drop-and-count under a burst, no block on the caller's thread.

**G4 is reshaped to what the rule actually needs, not what it named.** The gate now counts rows in
the `metrics` table immediately before and after `memory_search` returns, with `MetricsFlusher`
paused (never constructed) — proving *"no synchronous bank write on the search hot path to record
a measurement"* (spec.json non-functional #2), which is the property the rule exists to protect.
*Watch red:* write one measurement synchronously inside `SearchAsync` and watch the count go
non-zero (`SearchMetricsIsolationTests`, `docs/plans/…§Ruling 2`).

**`spec.json` still states G4 in its original, unimplementable form.** This ADR is the record that
supersedes it for any future reader; the next person to change the metrics writer should read this
file, not re-implement the vacuous version by trying to satisfy the wording literally.

## Consequences

- A future D2 (adaptive flush aim, occupancy pressure, rate-limit floor) is a drop-in behind
  `IMeasurementBuffer`/`IMeasurementRecorder` — no caller, and none of `MeasurementBufferTests`'
  capacity/drop/whole-flush assertions, needs to change to accommodate it.
- `MetricsFlusher.RecordSelfMetricsAsync` writes flush duration, batch size and drop count
  *directly* to `SqliteMetricsStore` — never through the buffer — so the writer measuring itself
  cannot recurse into itself, and self-metrics land even when the buffer is at capacity.
- Any other place in this codebase that reasons about "bank writes during a call" (the phrasing
  G4's brief used) should read it as "writes to the table the gate names", not literally "any
  write on the connection" — `total_changes()` is per-connection and cannot distinguish the two.
- This is a deliberate case of **not** building the literal design when a simpler mechanism
  satisfies the same observable contract (ask-if-simpler) — recorded here rather than left to be
  rediscovered the next time someone reads the spec's `Channel` wording and assumes it shipped.
