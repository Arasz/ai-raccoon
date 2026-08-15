# Refinement feedback — Four deferred findings — rule before merge

<!-- refinement-form: refinement:2026-08-16-perf-metrics-deferred:v1 · saved 2026-08-15T22:19:43.231Z · answered 4/4 -->

Source document: `docs/plans/2026-08-15-performance-metrics-implementation.md`

> Recovered from the reviewer's paste, not written by the form. The browser fell through to the
> clipboard link rather than the filesystem one, so nothing landed at `expectedDir` — the same
> fallback the 2026-08-15 spec gate hit. Content is verbatim and carries its end marker.

## F7 — The flusher stops writing self-metrics when it drained nothing

**Verdict:** APPROVE

**Notes:**

> I didn't meant that - it should be behaind condiotion and emit something only when flush was done - we don't need performance metrics for empty run

---

## F10 — The product and test tool inventories are reconciled on the unnamed-tool case

**Verdict:** APPROVE

**Notes:**

> SDK permits - we don't, in this project tool name is required and must be defined.

---

## F11 — SearchTimings.PhaseNames stops reflecting over any TimeSpan property

**Verdict:** APPROVE

**Notes:**

> Stops reflecting

---

## F13 — IMetricsReportService moves to Core beside the other ports

**Verdict:** APPROVE

**Notes:**

> move now

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->
