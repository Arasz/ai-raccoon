# Refinement feedback — Delta review 2026-08-21 — 12 owner rulings

<!-- refinement-form: owner-gate:2026-08-21-delta-review:v1 · saved 2026-08-21T18:29:47.208Z · answered 14/14 -->

Source document: `docs/reviews/lanes/2026-08-21-delta-*.md + docs/reviews/2026-08-21-delta-review-ground-truth.md`

## D1 — Activation re-verifies manifest sha256 pins against on-disk files

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — Non-LFS provenance files get integrity pins before arbitrary models ship beyond bundled defaults

**Verdict:** APPROVE

**Notes:**

> B

---

## D3 — Dimension reconcile also runs at open when engine dim ≠ vec dim

**Verdict:** APPROVE

**Notes:**

> What about performance hit for dimension check? Will this be server only? (HARD INVARIANT)

---

## D4 — Remove ConfigureAsync/ConfigureEmbeddingAsync from IMemoryStore

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D5 — StampSchemaDigestAsync moves after StampAsync(CurrentVersion)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — HasWorkAsync and the ledger read join RunAsync inside the per-job guard

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## S1 — promotion_list without projectId requires a global read-all mode instead of skipping the gate

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## S2 — Sync remote-blob authenticity: signature/keyed hash, or accepted risk documented

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## S3 — jsaa-memory.db leaves git history (19 MB, PII), not just HEAD

**Verdict:** APPROVE

**Notes:**

> but we need to create issue for it now and wait for the calm machine

---

## Q1 — StepUntilAsync wraps each iteration's awaits in a wall-clock-linked token

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Q2 — Nightly-tagged quality gates get a workflow_dispatch PR-gate leg

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C1 — Auto-start detects unpackaged invocation and fails with instructions instead of 'serve exit 1'

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C2 — Server-side 5xx on the settings channel gets its own exit code, not InvalidArgument=15

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## C3 — doctor distinguishes 'no bank' from HEALTHY

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->