# Refinement feedback — Remove stdio full-server mode

<!-- refinement-form: refinement:remove-stdio-full-server:v1 · saved 2026-09-09T09:50:55.659Z · answered 7/7 -->

Source document: `.ai-badger/task-tracking/plans/2026-09-09-air-remove-stdio-full-server-mode.md`

## D2 — Bare --transport http is also removed: bare launch means proxy, full servers come only from `serve`

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D1 — `--transport stdio` dies by this matrix: enum-delete → 9 (+optional stdio hint), explicit guard → 15, new code 26 only if scripts must branch; https is ruled in the same matrix

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D5 — Tests migrate to HTTP-ephemeral hosts; no internal stdio host survives for tests

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D9 — `scripts/` migration is in task scope (hermes-provider-setup probe, manual-fresh-install-test, test_poc_port_gates, run_threshold_eval + poc-parity README + eval plan doc disposition)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D10 — A filled manual checklist in <code>docs/work/checklist/</code> is a P6 exit gate

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D3 — Same-data-root serves on distinct ports are live-probed in P6 (else scoped to documented-allowance without live proof)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — Hermes standardizes on the temp-port proxy recipe (temp <code>--data-root</code> + <code>--port &lt;lease&gt;</code>)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->