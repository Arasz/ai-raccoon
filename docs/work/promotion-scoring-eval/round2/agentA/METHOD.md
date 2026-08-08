# Scoring v3 (Agent A, round 2) — baseline evolution

One file (`scorer.py`), stdlib only, deterministic. Same architecture as the
round-1 winner: **provenance archetype prior → clamped content-shape evidence →
note-refinement layer for agent-authored entries.** The baseline's two files
(`base_scorer.py` + `refine.py`) are merged; all regex feature extractors from
the baseline are kept verbatim unless listed below.

## What changed vs baseline, and why

The baseline's per-slice rank correlation was fine (organic +0.69, doc-sourced
+0.73 on train) but the combined rho was only +0.62: the two slices were
individually well ordered yet **miscalibrated against each other** — organic
notes floated systematically above equally-useful doc chunks. Almost all of the
gain comes from fixing provenance handling, not from new content heuristics.

1. **New archetypes for the fresh-sweep path families** (baseline had none, so
   they fell into `organic_note` prior 3.45 or `work_note`):
   - `status_log` (prior 0.35): `.remember/` done-files, `session-*` /
     `*status*` / checkpoint files — write-once progress ledgers.
   - `auto_memory_index` (0.70): a Claude auto-memory `MEMORY.md` — an index of
     links to topic files, not a fact.
   - `auto_memory_note` (1.60): other Claude auto-memory topic files — often a
     distilled durable lesson; runs through the note-refinement layer so
     measured gotchas rise (a "Measured <date>: … how to apply: …" reference
     note reaches ~3+) and setup logs stay low.
2. **Organic notes split by the doc path they mirror.** An agent write whose
   recorded path embeds `/docs/plans/` is plan output that happened to be
   written as a note (train labels 0–1), not a durable fact: new
   `organic_plan_note` prior 1.55. `/docs/work/` + `/docs/reviews/` mirrors get
   `organic_work_note` 2.35 (mixed labels, content decides). Pure agent writes
   (hex names / no docs path) keep a high prior, retuned 3.45 → 2.95 to sit
   correctly against the doc scale.
3. **Note-refinement layer extended** (baseline `refine.py` logic kept:
   status openers −1.2, status-vocabulary −0.15/hit, second-person −0.5,
   commit-hash −0.3, test-count exclusion from measured evidence, durable-fact
   bonuses, dated-fact bonus, short-definitional exemption):
   - more status openers ("Reviewer…", "Still working", "Confirmed",
     "Update:", "Progress", "Current state", "Handoff", …) and status vocab
     ("nothing left pending", "checklist", "all green", "next step", …);
   - hard cap at 1.10 when a status opener fires and the note contains **zero**
     durable-fact markers — a progress report can't ride the organic prior;
   - a note that is one big table (`table_frac ≥ 0.8`) is a log/test matrix,
     not a fact: −0.9;
   - proposal vocabulary ("proposed", "recommendation", "option A", "phase 2",
     "next steps") −0.25/hit capped at −0.9 — not yet true, hence not durable;
   - completion-announcement first line ("… complete.", "… and committed")
     −0.8 when the full opener regex didn't already fire. *Train-neutral* (no
     labeled entry matches); added because its labeled siblings in the opener
     family ("Cleanup complete", "Task closed", "Plan written") are all 0–1.
   - DURABLE gains a few generic markers ("failure mode", "the fix was",
     "workaround", "mitigation").
4. Small prior touches: `adr` 3.00→2.85, `plan` 0.85→0.80, `review` 0.80→0.75.

No id lookups, no memorized strings tied to specific candidates; every rule is
a path convention or a content shape.

## Measured results (train, n=83)

| scorer | overall rho | organic (25) | doc (58) | fresh id≥10k (42) | old (41) |
|---|---|---|---|---|---|
| baseline (base+refine) | +0.6247 | +0.6927 | +0.7264 | +0.7271 | +0.6002 |
| **v3** | **+0.8004** | +0.8211 | +0.8021 | +0.8274 | +0.7927 |

Both slices improve independently — the gain is not only recalibration.

## Overfit check

Leave-one-project-out (rho of the final scorer on the remaining ~58–73 labeled
rows when each project's rows are removed): +0.8096 / +0.8309 / +0.7938 /
+0.7953 / +0.7703 (dropping ai-badger, ai-raccoon, arasz-home-page,
hermes-default, jsaa respectively). Per-project rho ranges +0.62…+0.87 with no
project below the baseline's overall figure, so no single project's labels
carry the result. Weakest slice is ai-badger (n=10, +0.62), same weakest slice
as the baseline.

Contract verified: `python3 scorer.py candidates_all_unlabeled.json` exits 0
and emits a valid JSON array of 292 `{"id", "score"}` rows.
