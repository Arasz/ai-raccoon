---
name: evidence-first-research
description: >-
  Use when a question needs investigating and the answer will be acted on — "research X", "look
  into whether Y", "find out how Z works", "is this worth doing", "compare these options", a
  benchmark someone will quote, or a claim that has to survive being challenged. Produces a dated
  record where every finding carries how it is known — measured, read, inferred, or unverified —
  plus a self-contained HTML view. Not for locating code (that is explore-codebase), tracing one
  symptom (debug-issue), or judging a diff (review-changes); reach for this when the output is a
  set of findings someone else will rely on.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [research, evidence, provenance, reporting]
    related_skills: [explore-codebase, debug-issue, owner-gate-review]
---

# Evidence-first research

Answer the question, and make how you know it inseparable from what you found.

**Why this exists.** A report where *"the gate takes 75 seconds, measured"* and *"the gate takes
about five minutes, roughly"* look identical is a report that will eventually be quoted wrong.
That happened here: a cost claim of "3-5 minutes per push, about an hour across fifteen pushes"
reached a changelog and a PR body. Measured, it was 75 seconds and nineteen minutes — off by
four times, and caught only because someone re-derived it by hand. Nothing in the format made the
unmeasured number look unmeasured.

So every finding is graded, the grades are a closed set, and the renderer **refuses a record that
grades badly** rather than producing a page that looks finished.

## The four grades

| Grade | Means | Needs |
|---|---|---|
| `MEASURED` | You ran it and read the result | The command, the machine, the conditions |
| `READ` | You read it in a source that is authoritative for this | `path:line`, a spec section, a doc URL |
| `INFERRED` | You reasoned to it from things above | Say what from — reasoning is not a citation |
| `UNVERIFIED` | You did not check | Nothing. Saying so *is* the finding |

`MEASURED` and `READ` are refused without an `**Evidence:**` line. `UNVERIFIED` is deliberately
free: demanding a citation for "nobody looked" pushes writers to leave the gap silent instead,
which is the failure this is built to prevent. Full rules in `references/provenance.md` — read it when
grading or when a grade is disputed.

**The grade is about you, not the claim.** A true fact you did not check is `UNVERIFIED`. A number
someone else measured and you copied is `READ`, not `MEASURED` — and if you cannot cite where you
read it, it is `INFERRED`.

## Steps

1. **Write the question down first**, as one sentence, before looking anything up. A question that
   changes shape mid-investigation produces findings that answer neither version.
2. **Investigate.** Prefer running something over reading about it; prefer reading the source over
   reasoning about it. Every time you drop a level, that is the grade.
3. **Write the record** from `references/report-template.md` into the project's dated-work
   directory — `<docs>/work/YYYY-MM-DD-<slug>.md`, per the canonical tree. Grade every finding as
   you write it, not in a pass at the end: a grading pass is where "I think I measured that"
   happens.
4. **Render the view:**
   `python3 .ai-badger/skills/evidence-first-research/scripts/render_report.py <docs>/work/YYYY-MM-DD-<slug>.md`
   It writes to a temp directory and prints the path. It **refuses** a target inside the
   repository — the record is the artefact, and a committed HTML view becomes a second source of
   truth nobody can date.
5. **Read your own `## Still open` section before reporting.** If it is empty, you either answered
   everything or you stopped noticing. The second is more common.
6. **Report the grade mix, not just the conclusion.** "Four findings, one measured" is a different
   answer from "four findings, all measured", and the person acting on it needs to know which.

## Charts

Declare one in the record with a fenced block; the renderer turns it into inline SVG. Five kinds,
and an unknown kind is refused rather than skipped:

| Kind | For | Body |
|---|---|---|
| `provenance` | The grade mix | Automatic — always drawn, never declared |
| `bars` | Comparing measured values | `label: value` per line |
| `line` | A quantity across a sequence | `label: v1,v2,v3` |
| `matrix` | Options against criteria | Header row, then `label, v, v` |
| `range` | A measurement's interval | `label: low..measured..high` |

    ```chart:range
    title: push seconds by change shape
    docs-only: 12..14..19
    full: 68..75..91
    ```

**Prefer `range` to `bars` for anything timed.** A single bar asserts a precision three runs will
not support, and the spread is usually the interesting part.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- A number in the record with no grade on its finding
- `MEASURED` on something you read rather than ran
- A benchmark without the machine and the conditions
- An empty `## Still open` on a question that took more than an hour
- Rendering the HTML into the repository, or committing it
- Reporting the conclusion without the grade mix
- A `bars` chart of timings, where the spread was never recorded

## Verification checklist

- [ ] The question in the record is the question that was asked
- [ ] Every `MEASURED` finding names the command and the conditions
- [ ] Every `READ` finding cites `path:line` or a spec section
- [ ] Every `INFERRED` finding says what it reasons from
- [ ] `## Still open` is non-empty, or its emptiness is defended
- [ ] The renderer ran clean — it refuses badly graded records, so a clean run is a check
- [ ] The HTML is outside the repository and uncommitted

## Files

- `references/provenance.md` — what each grade means, what disqualifies one, worked examples. **Read it when grading a finding, or when a grade is disputed.**
- `references/report-template.md` — the record shape the renderer parses. **Read it when
  writing the record (step 3).**
- `scripts/render_report.py` — record → self-contained HTML. No network, inline SVG, no scripts.
