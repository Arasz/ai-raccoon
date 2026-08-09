# Refinement feedback — Nine decisions on the OTLP fix plan

<!-- refinement-form: refinement:2026-08-09-otlp-fix-plan:v1 · saved 2026-08-09T10:43:12.830Z · answered 9/9 -->

Source document: `docs/work/2026-08-09-otlp-fix-plan.md`

> Delivery note (recorded by the agent, not part of the owner's answers): returned by paste rather
> than saved to the watched directory, same as the first gate. Reproduced verbatim.
> Form: `docs/work/2026-08-09-otlp-fix-plan-review.html`.

## D1 — Tool instrumentation moves to a CallToolFilter

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — Adopt mcp.server.operation.duration for the tool duration histogram

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D4 — Background services get one generic telemetry port in Core

**Verdict:** APPROVE

**Notes:**

> A

---

## D5 — The quiet-mode log file ships without rotation

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — Is quiet HTTP/combined mode reachable?

**Verdict:** APPROVE

**Notes:**

> combined, it would be best to have one place when logging is configured, and it should accept transport to adjust details

---

## D7 — Ship the renames as one break, with no transition period

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D8 — dotnet-trace --providers names every AiRaccoon scope, not just the tool one

**Verdict:** APPROVE

**Notes:**

> is there any concept like profile that could tell: this profile should use all those providers we care about

---

## D9 — Split Observability/ into Emission/, Export/, Monitoring/

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D10 — The sampler change stays held until the proxy lane's test is green

**Verdict:** APPROVE

**Notes:**

_(none)_

<!-- end refinement feedback -->
