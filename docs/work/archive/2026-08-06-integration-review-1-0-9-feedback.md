# Refinement feedback — Integration review 1.0.9 + hosted-service #55 — 8 owner decisions

<!-- refinement-form: refinement:2026-08-06-integration-review-1-0-9:v2 · saved 2026-08-06T14:20:18.955Z · answered 8/8 -->

Source document: `docs/reviews/2026-08-06-integration-review-1-0-9.md`

## D1 — The hosted-service shape (#55) is ratified despite the adoption plan deferring it

**Verdict:** APPROVE

**Notes:**

> Ratify, it is mostly used as a part of memory validation

---

## D2 — Unattended promote mode (warning-only, no confirm gate) is the accepted posture

**Verdict:** APPROVE

**Notes:**

> Stronger confirmation, but it is a part of my memory testing suite

---

## D3 — The interval knob gets a setter: add `extract interval <minutes>`

**Verdict:** APPROVE

**Notes:**

> add verb

---

## D4 — In-pass dedup is fixed: shared index refreshed per project (S3)

**Verdict:** APPROVE

**Notes:**

> fix

---

## D5 — Host-kill robustness: interval reads move inside the exception shield (S1)

**Verdict:** APPROVE

**Notes:**

> fix

---

## D6 — Test-honesty fixes land: call-counter fakes + real-SQL port test + DI smoke (S6-S9)

**Verdict:** APPROVE

**Notes:**

> include

---

## D7 — Docs drift is fixed in the bump PR: tool count 19→20 + extract family in READMEs

**Verdict:** APPROVE

**Notes:**

> fix

---

## D8 — Version bumps to 1.0.10 in this cycle (owner f:), carrying the selected fixes

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->
