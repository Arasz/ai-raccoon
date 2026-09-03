# Placement — where does this text belong?

Run the gates in order. **Stop at the first gate that fires**; do not run a later gate to
double-check an earlier one. Only text that survives all four gates gets a Diátaxis quadrant.

None of this is scriptable. Classification is judgement — a script here produces a confident,
plausible, wrong answer and nobody re-opens it. What *can* be checked mechanically is the
postcondition: once you have chosen a path, its directory either exists in the canonical tree or
it does not.

## Gate 1 — the falsification test

> If the code changed tomorrow so this document became untrue, would you **edit it**, or would
> editing it be **falsifying a record**?

- **Edit** → documentation. Continue to gate 2.
- **Falsify** → work record. `<docs>/work/YYYY-MM-DD-<slug>.md`. Stop.

**Mechanical corollary: a work record's filename carries its date; a documentation filename never
does.** A dated file in a quadrant is misplaced. An undated plan is a work record wearing
documentation's clothes — observed in a real corpus, where plans, design specs and review rounds
sat at the docs root and read as current for months.

Work records are not demoted and not untrusted. A plan is accurate about its moment. They carry
`modality: normative` (see `trust.md`) and are never citable as descriptions of the running
system. In a repo that has been worked on for a while, work records are usually the *majority* of
the corpus by line — `work/` is the main event and the four quadrants are the footnote.

## Gate 2 — the four forcing conditions

Any one of these sends the text to `<docs>/work/` with **no deliberation**. Do not run the compass.

1. **It names a moment** — a checkpoint, a review round, "as of", a session handoff.
2. **It proposes** — plan, spec, design, backlog. It says what *should* happen, not what is.
3. **It reports a finding** — research, measurement, incident post-mortem.
4. **It judges other work** — a review, an audit, a feedback form.

## Gate 3 — the ADR gate

Does the text record a **decision**, with the alternatives considered and the consequences
accepted? Then it is an ADR: `<docs>/adr/NNNN-<slug>.md`, via the project's ADR process, and the
quadrant document *cites* it rather than restating it. Most projects mandate an ADR for
architecture-level change (a new cross-layer dependency, a layering change, a tech swap); writing
the explanation instead of the ADR is skipping a required gate, not a stylistic choice.

ADRs are immutable and frozen: never moved, renamed, reformatted, or reclassified into a quadrant.

## Gate 4 — the not-documentation gate

Some files under the docs tree are not documents at all. They get no frontmatter, no trust
markers, no quadrant, and no ledger entry:

- **Runtime or build input** — files the build embeds or the application parses at runtime.
  Adding frontmatter to one silently corrupts production data. Frozen; see
  `../../scaffold-documentation/references/structure.md`.
- **Assets** — images, diagrams as binaries, and the README that indexes them.
- **Machine state and tooling** — anything under `<docs>/meta/`. Tooling lives in the project's
  scripts directory, never under the docs tree.
- **Non-`.md` files** — excluded from the trust system entirely. HTML comments are meaningful
  markup in an `.html` file, so a marker grammar built on them cannot be applied there.

## The compass — two axes, four quadrants

Diátaxis crosses two axes: **practical ↔ theoretical** (does the reader act, or think?) and
**study ↔ work** (is the reader acquiring skill, or applying it?).

| | study | work |
|---|---|---|
| **practical** | `tutorials/` — learning by doing, we choose the goal | `how-to/` — the reader's goal, our steps |
| **theoretical** | `explanation/` — why it is like this | `reference/` — what it is, looked up mid-task |

Ask these in order. **First yes wins** — do not keep reading for a better fit.

1. Does the reader arrive with a goal of their own and need the steps to reach it? → **`how-to/`**
2. Would the reader look this up mid-task to check a name, value, field, signature, or limit? →
   **`reference/`**
3. Is this a guided first experience where *we* pick the goal and guarantee it succeeds? →
   **`tutorials/`**
4. Does the reader want to understand why something is the way it is, with no task in hand? →
   **`explanation/`**
5. No yes at all → it is a work record. Go back to gate 1; you answered it wrong.

## Tie-breaks — deterministic, no discretion

- **Steps containing choices are a how-to, not a tutorial.** "If you use X do Y, otherwise Z" means
  the reader brought their own goal. A tutorial has no branches.
- **A table inside a why-narrative is reference.** Extract it to `<docs>/reference/` and link to it
  from the explanation. A lookup table buried in prose is a lookup nobody finds.
- **Under 200 lines spanning two types → place by majority**, and record the deferred split so the
  next editor inherits the decision rather than re-litigating it: one HTML comment immediately
  above the minority section,
  `<!-- split-deferred: reference -> <docs>/reference/<name>.md when this file passes 200 lines -->`.
- **200 lines or more spanning two types → split. Always.** The majority rule does not apply above
  200 lines; a file that large is read by two different readers who want two different things.

## Postcondition for any placement decision

You have named exactly one path, and its directory exists in the canonical tree. Verify the
directory yourself before writing (`test -d "$(dirname <path>)"` or your platform's equivalent);
if it is missing, invoke `scaffold-documentation` rather than creating it by hand. **A placement
you cannot state as a path is not a decision** — it is a deferral, and the next agent will make a
different one.
