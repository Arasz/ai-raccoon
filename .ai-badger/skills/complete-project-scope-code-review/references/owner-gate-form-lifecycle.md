# Owner-gate form lifecycle — pitfalls from a live MoE review (the project Part 2, 2026-08-06)

Worked case for the owner-gate handoff inside a multi-expert plan review. The form was
generated programmatically (9 cards) and opened in the user's browser. Then the 4th expert
(code-reviewer join-gate) returned TWO additional owner questions AFTER the form was open.

## Pitfall 1 — finalize the decision set BEFORE the reviewer opens the form

Sequence that bit: wave-1 experts returned → 9 cards written → form opened → code-reviewer
wave returned with 2 new owner questions (Q1/Q2) → cards added to the HTML on disk → user
saved 9/9 → the 2 new cards were NOT among them (the browser holds the card set it loaded).

Consequence: the late cards had to be re-routed through `clarify` (one question per call),
and the answers appended to the feedback record as an addendum. It worked, but it split the
owner interaction into two channels and renumbered the record.

Rule: dispatch ALL expert waves (including the join-gate) BEFORE generating the form. If a
question surfaces after the form is open, ask via `clarify` — never regenerate the HTML mid-
review, and never silently drop the late question.

## Pitfall 2 — a pasted feedback block is authoritative; don't wait for the file

The save chain fell back (no disk copy at expectedDir or ~/Downloads — clipboard/textarea
path). The user pasted the generated markdown instead. The paste carried the trailing
`<!-- end refinement feedback -->` marker, so it was complete.

Rule: ingest the paste verbatim (check the end marker + "answered n/n"), write it to the
watched path yourself as the canonical record, and kill the file watch — the paste IS the
review; the file is just storage.

## Pitfall 3 — the same-storageKey hazard bites on regeneration

If you DO regenerate the form, keep the storageKey identical so in-progress answers survive
reload — but note the browser still needs a manual reload to see new cards. Unique storageKey
per review remains mandatory (shared file:// origin).

**Regeneration mechanics trap (hit live):** the generated file's `var DECISIONS = [...]` array
ends with a bare `]` (json.dumps), NOT `];` — the template's original `];` is gone after the
first generation. A boundary search with `content.index('];', start)` sails PAST the array
into the second script block's `var VERDICTS = ["approve", ...];` and slices raw HTML/CSS in
between — `json.loads` then fails with `JSONDecodeError: Extra data` at some line column.
Locate the array close instead by finding the standalone `]` line (the line whose strip() ==
']' after the last decision object, before `</script>`), then rebuild
`'var DECISIONS = ' + json.dumps(decisions, indent=2, ensure_ascii=False)`.

## What worked

- Programmatic form generation (json.dumps over the decisions array + assertions on
  storageKey/outName/expectedDir) — one pass, no fragile template hand-edits.
- HTML-escaping angle brackets in detail strings (`I&lt;Verb&gt;Commands`) — the template
  fields accept inline HTML.
- Rulings table in the integrated review (ID | Verdict | Notes) + "11/11 APPROVE" final
  state line — one glance tells a future session the gate is closed.
