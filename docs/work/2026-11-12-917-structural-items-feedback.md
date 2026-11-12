# Refinement feedback — Issue #917: 7 decisions before the structural fixes

<!-- refinement-form: owner-gate:2026-11-12-917-structural-items:v1 · saved 2026-11-12T08:44:18.136Z · answered 7/8 -->

Source document: `https://github.com/Arasz/ai-raccoon/issues/917 · docs/work/2026-10-23-2x-release-review.md`

## G1 — The task runs the low-effort loop: 7 small PRs, one reviewer each

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D1 — Schema v24 clears the TTL on every non-memory row, then adds guard triggers

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — The guard is a BEFORE INSERT/UPDATE trigger, not a column CHECK

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D3 — get_entry adds a <code>corpus</code> output field now; the output <code>entryType</code> is deprecated and removed in 3.0.0

**Verdict:** APPROVE

**Notes:**

> also add issue to clean that up

---

## D4 — A re-ingest keeps a row's declared kind and its TTL

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — Workspace consolidation and share_memory keep each row's declared kind

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D5 — Delete the dead <code>SetSweepThresholdAsync</code> and its <code>trigger_memory_cleanup_threshold</code> gate

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

- G2 — The 7 PRs ship together as 2.8.0 (minor) (note only, no verdict)

  > 2.9.0 - there will be dotnet 11 bump merged first

## Agent reconciliation

- G2 is read from its note: the batch ships as **2.9.0** after the .NET 11 bump (expected 2.8.0) merges. Each lane PR leaves `VERSION` alone; the last #917 lane bumps to 2.9.0 if main is then at 2.8.0, else re-asks.
- D3 note: the 3.0.0 removal of the output `entryType` is tracked in its own issue.

<!-- end refinement feedback -->
