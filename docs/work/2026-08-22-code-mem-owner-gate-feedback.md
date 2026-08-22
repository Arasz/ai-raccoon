# Refinement feedback — Eight decisions from the code-corpus task

<!-- refinement-form: refinement:2026-08-22-code-mem-owner-gate:v1 · saved 2026-08-22T07:43:34.996Z · answered 8/8 -->

Source document: `docs/work/2026-08-21-code-search-implementation-plan.md §12 + issues #422/#435/#436`

## R1 — CurrentVersion stays 10; the watch-overlap prune runs unconditionally at every bank open (the plan's v11 ladder bump is reverted)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R2 — memory_ingest_file routes code files to the code corpus (OQ4)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## R3 — The WP8 eval floor (nDCG@5 ≥ 0.50 + negative controls) is deferred to a follow-up task and was NOT a ship gate for 1.30.0

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D1 — #422 is resolved by re-measuring the ONNX graph's true token cap, and the S4 activation gate + chunk budget follow the measurement

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — You name the 2-3 vendored eval repos (≥1 Python, pinned commits, permissive licenses) for the full code-retrieval eval (OQ5)

**Verdict:** APPROVE

**Notes:**

> - https://github.com/microsoft/vscode
> - https://github.com/dotnet/aspnetcore
> - https://github.com/deepseek-ai/deepseek-harness
> - https://github.com/semantica-agi/semantica

---

## P1 — Owner-authorized in-session merges may use --admin past the same-account review-required gate, as standing policy

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## P2 — #436 (code-corpus prune gap: a shrinking code file strands code_entries rows on direct re-ingest) is scheduled as a near-term task, not backlog

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## P3 — #435 (bge-m3 weak-vector-leg / pooling lead) proceeds only if ai-raccoon-cc's cheap vector comparison supports the hypothesis; the 2.5h re-embed needs your go

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->