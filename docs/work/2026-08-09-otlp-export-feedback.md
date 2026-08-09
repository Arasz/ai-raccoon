# Refinement feedback — Eighteen decisions on OTLP export

<!-- refinement-form: refinement:2026-08-09-otlp-export:v1 · saved 2026-08-09T10:04:11.915Z · answered 18/18 -->

Source document: `docs/reviews/2026-08-09-otlp-export-review.md`

> Delivery note (recorded by the agent, not part of the owner's answers): the owner returned this
> by pasting it into the session rather than saving it to the watched directory. Content is
> reproduced verbatim below, including the closing marker. The form itself is
> `docs/work/2026-08-09-otlp-export-review.html`.

## F1 — Queue depth and utilization become observable instruments read from the store

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F2 — The bare-launch host path disposes the host, so telemetry flushes at exit

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F3 — A malformed OTLP endpoint disables export with a warning — it never kills the server

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F4 — Signal-path composition uses the SDK's own rules instead of the hand-rolled copy

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F5 — Exported spans stop being orphans — pick which of the two remedies

**Verdict:** APPROVE

**Notes:**

> B

---

## N1 — Metric names move to the dotted namespace, and every instrument gets a unit

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## N2 — Span conventions: error.type, a recorded exception, and ActivityKind.Server

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## N3 — Adopt the MCP semantic convention rather than inventing our own shape

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## N4 — Resource gets service.version; meters and sources get a version

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## T1 — Adopt System.Net.Http metrics and traces — two lines, no package

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## T2 — Reopen the “no ASP.NET/HTTP auto-instrumentation” non-goal?

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C1 — ADR-0009 gets four corrections and the README one

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C2 — Stop failing silently in the three places where we currently do

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R1 — The promotion-queue histograms get the two tags that cost nothing

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R2 — The metric's project_id is set after the access gate, not before

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R3 — Background services get instrumented

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R4 — Close the four test gaps the review found

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R5 — Four pieces of hygiene, batched

**Verdict:** APPROVE

**Notes:**

_(none)_

<!-- end refinement feedback -->
