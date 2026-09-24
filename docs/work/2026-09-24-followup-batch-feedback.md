# Refinement feedback — Seven decisions for the retrieval & reliability follow-up batch

<!-- refinement-form: refinement:2026-09-24-air-followup-batch:v1 · saved 2026-09-24T01:46:50.411Z · answered 7/7 -->

Source document: `scratchpad/plan/PLAN-rev2.md (section Decisions)`

## D1 — Defer item 1 (wrong key 21 vs corrupt bank 32) to its own task, filed as an issue

**Verdict:** CHANGE

**Notes:**

> just add - wrong key or corrupt bank for now

---

## D2 — A rejected API key (401/403) keeps exit 77 but gets its own message; a bad base-url starts returning the unused 78

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D3 — Exits 50/51 get precise messages, no new codes

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D4 — Exclude query A9 from the RRF no-regression gate, and pin its measured hybrid rank as a ceiling

**Verdict:** APPROVE

**Notes:**

> fussion fix task

---

## D5 — Measure the byte-identical-chunk dedupe loss first; file the fix only if it loses more than 0.5% of lines

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D6 — Accept the FTS tie-break gap (F1) with a record; no code change

**Verdict:** CHANGE

**Notes:**

> hash tie-break

---

## D7 — Harness A1: accept the embedding seam as documented (A3) and file “re-baseline the harness on granite”

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->