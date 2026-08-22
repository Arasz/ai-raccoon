# Refinement feedback — Post-delta open items — six decisions

<!-- refinement-form: refinement:2026-08-22-delta-open-items:v1 · saved 2026-08-22T08:11:46.831Z · answered 6/6 -->

Source document: `docs/work/2026-08-21-delta-review-fix-plan.md + docs/work/2026-08-21-delta-review-fix-plan-review.md`

## G1 — The 1.31.0 sync-blob compat note ships in the release notes and how-to; old clients fail loud with no fallback

**Verdict:** APPROVE

**Notes:**

> add it, but I will not pusblish a new version for now

---

## G2 — The project-identity ADR (H-C / O2) is commissioned as a near-term task

**Verdict:** APPROVE

**Notes:**

> - we want to add tool - get project-id-token and CLI option - generate project token id
> - we will add option in CLI to trigger project-id conversion (only from raw text to guid)
> - token will be just sortable guidv7, this is not a measure to create fully secure solution, as we will treat other projects on the same machine as trusted (it makes sense)
> - we only want to eliminate unauthorized access by accident. Attacker will need to put some effort to get access to other project data
> - new projects need to provide guid id - old ones can use old id (we can add a warning)
> - we should add instruction how to store the token, auto add token to ignored files if we decide for file store
> - still each project can enumerate DB until it is not encrypted?

---

## G3 — The five away-mode judgment calls stand (O1, O3, O4, O5, O6)

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## G4 — Carried findings get the cheap ones scheduled and the rest explicitly parked (H20, H21, H16, H1, M5, M10)

**Verdict:** APPROVE

**Notes:**

> Create a continuation plan for them now, create issue that will reference this plan

---

## G5 — The jsaa-memory.db history rewrite (S3b, #414) runs in the current calm window

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## G6 — The unexplained 1.29.0 checklist server-lifecycle failure gets a time-boxed diagnosis (or is closed as superseded)

**Verdict:** APPROVE

**Notes:**

> Park it, create issue - we will handle it as priority if the second case will be observed

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->