# Refinement feedback — Post-delta session 3 — WP11 embed/ingest load governor, four decisions

<!-- refinement-form: refinement:2026-08-22-post-delta-3-wp11:v1 · saved 2026-08-22T15:23:36.786Z · answered 4/4 -->

Source document: `docs/work/2026-08-22-post-delta-3-plan.md § WP11 + § Owner gate (G16–G19)`

## G16 — The ONNX intra-op thread pool is capped at half the physical cores by default, via a bank setting embedding.threads

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## G17 — Only one embed drain runs at a time, and the watch digest stops draining inline

**Verdict:** CHANGE

**Notes:**

> We want to use one system, based on bounded channels - not a semaphore - exactly the same solution as for metrics. It should be a separate 'topic' than for metrics - but I want you to extract the channel based events pump. We can even use single pump with round robin consumers with a limited processing budget

---

## G18 — Both drains take their rows-per-run from one bank setting, maintenance.embed-rows-per-run.global, defaulting to today's 128

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## G19 — Both knobs are bank settings written by the server, not CLI flags or environment variables

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->