---
name: multi-lane-report-assembly
description: >-
  Use when assembling parallel lane outputs into one record.
---

# Multi-lane report assembly

Assemble ONE evidence-graded record (or report) from several parallel lane
outputs — the standard shape when you fan out research/review lanes and
integrate their findings yourself. The lanes produce finding blocks; you lift
them verbatim, consolidate, renumber, and gate the result.

## When this fires

- You dispatched 2+ lanes that each returned `### F# — <claim> [GRADE]` blocks
  (evidence-first shape) and you must merge them into a single record.
- You are renumbering or re-sectioning findings from multiple sources into one
  deliverable that a strict renderer/checker will parse.

## Lane output contract (demand this from lanes)

- One `### F<n> — <claim, present tense> [GRADE]` block per finding; grade in
  the closed set {MEASURED, READ, INFERRED, UNVERIFIED} at the END of the claim.
- `**Evidence:**` line REQUIRED for READ/MEASURED (path:line or URL).
- INFERRED states its reasoning basis; UNVERIFIED states why not checked.
- A `## Still open` list and a grade-mix line (`N findings: x READ, ...`).
- Findings carry `Type:` (FLAW/GAP/ACCEPTED/INFO) + severity (L×I) when scored.

## Getting the full lane reports (Hermes)

The consolidated batch message may arrive late, and the LIVE delegation transcripts
(`~/.hermes/cache/delegation/live/<delegation-id>/task-N.log`) ABBREVIATE long summaries —
the final line ends in `…(+N chars)` and is NOT the full report. Do not merge from the
transcripts; you would lift truncated claims.

The authoritative full summaries live in the Hermes state DB (read-only):

```bash
sqlite3 "file:$HOME/.hermes/state.db?mode=ro" \
  "SELECT result_json FROM async_delegations WHERE delegation_id='<id>'"
```

`result_json` → `results[]`, each carrying `summary` (may be truncated), `summary_full_path`
(the full markdown, e.g. `~/.hermes/cache/delegation/subagent-summary-N-<ts>.txt`), plus
`model`, `api_calls`, and `tokens {input, output}`. The live-dir `manifest.json` holds only
paths and status. To record lane tokens in the task tracker without manual counting:
`task_tracker.py subagent <taskId> --delegation <delegation-id> --description "..."` reads the
same row. Use the `summary_full_path` files as the verbatim lift source for assembly.

## Assembly steps

1. **Extract blocks** with `^### (F\d+[ab]?) — ` (MULTILINE). The id may contain
   letters (`F4b` parses); the claim line must end with the grade in brackets.
2. **TRUNCATE every block at the first `## ` header.** Lanes embed their own
   `## Still open` + grade-mix sections at the end of their file — lifted
   verbatim, that header lands INSIDE the last finding's body and ends the
   renderer's `## Findings` capture early. Symptom: the provenance chart counts
   only the first section's findings (e.g. 13 of 41) while the markdown looks
   complete. Strip at `body.find("\n## Still open")` and rstrip separators.
3. **Dedupe cross-cutting themes.** When two or three lanes independently file
   the same gap (rotation cadence, missing diagnostics, shared-key auth),
   consolidate into ONE finding in a dedicated cross-cutting section, with an
   italic provenance note naming the source lane findings. Never keep both a
   lane finding and a consolidated twin.
4. **ACCEPTED items go to a register section only** (never scored, never in the
   scored sections). Each entry keeps decision citation + residual risk +
   revisit trigger.
5. **Renumber sequentially, then run a stale-cross-ref pass.** Renumbering
   breaks every in-body reference ("see F12", "(F5)", "F2's ..."). Grep the
   assembled file for every old id pattern and map each to the new id or the
   register id (E#). A reviewer WILL find these — do the pass before review,
   not after. This is the most common quality-gate finding on assembled
   records.
6. **Section structure that renderers accept:** all `### F#` blocks under one
   `## Findings`; `####` subheaders for clusters are fine; front sections
   (`## Executive summary`, `## Scope & method`) and trailing sections
   (`## Still open`, `## Verification checklist`) must NOT appear between
   finding blocks. State grade mix + severity counts in the exec summary;
   `## Still open` non-empty.
7. **Render as the format gate.** A clean render is a real check: the renderer
   refuses ungraded claims, READ/MEASURED without Evidence lines, and any HTML
   target inside the repo. Render to a temp dir; never commit the HTML.

## Verification checklist

- [ ] No `## ` header inside any finding body (grep for `^## ` between `### F` blocks)
- [ ] Old-id grep pass clean (every "see F#/E#" resolves in the final numbering)
- [ ] Every READ/MEASURED block has `**Evidence:**`
- [ ] Grade mix + severity counts in exec summary match the rendered chart
- [ ] Renderer ran clean to a path OUTSIDE the repo
- [ ] `## Still open` non-empty, or its emptiness defended

## Wave-PR delivery (sequential wave branches under squash merges)

When the work ships as one PR per wave and merges are squash merges, see
`references/wave-pr-hygiene.md` — branch rebasing, draft-gating on owner-side
prerequisites, and the gh CLI quirks that look like failures but aren't.

## Pitfalls

- **Trust the renderer's count, not the markdown.** A truncated capture still
  renders "fine" for the part it saw. Check the provenance chart's total.
- **Consolidation ≠ summarization.** Merge by quoting the lane evidence lines,
  not by rewriting the claim. The consolidated finding must cite what each
  source lane evidenced.
- **Renumbering is not a mechanical sed.** In-body references are prose
  ("the F5-chain", "F2's zero diagnostic settings") — a plain id-replace pass
  misses the prose forms. Grep for the ids with context, then read each hit.
- **Register ids (E#) are cross-referenced too.** Findings reference accepted
  items; those refs also break under renumbering.
