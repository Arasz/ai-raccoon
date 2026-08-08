---
name: differential-feature-refactor
description: >-
  Use when a feature already exists in code but has drifted from — or was never reconciled
  with — its intended design, and someone must decide what changes before a refactor is
  scoped. Triggers: two parallel implementations of the same thing, code that reads as dead
  but may be a ratified extension point, an architecture nobody can tell from accumulated
  cruft, or a refactor about to be scoped off review documents instead of decisions.
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [refactoring, architecture, decisions, design]
    related_skills: [owner-gate-review]
---

# Differential Feature Refactor

Produce a **differential document** — a side-by-side of *what we have* versus *what we will
have* — that a human annotates, and that then becomes the input to a refactor specification
and an implementation plan.

**Why this exists.** A ratified architecture — an interface with an ADR behind it — was
reimplemented in parallel 36 minutes after it landed, with no ADR of its own. Five days later the
original read as "dead code" and was nearly deleted, which would have destroyed both a live
integration path and a requirement-level extension point. Twenty-plus documents described that
architecture and at least one described it falsely. Nobody could tell design intent from
accumulated cruft. This skill makes the difference legible **before** someone acts on it.

## The one rule that matters

**Never infer design intent from code shape.**

Requirements (`FR-`/`NFR-`), ADRs, and recorded rulings are the *only* authority on intent.
Code is evidence of what was built, never of what was meant. Review docs and plans are
proposals, not authority.

Establish the authority set **before** reading any implementation. If a thing in the tree has
no authorising decision, that is an undefined point for the human — not a finding you resolve.

### Rationalizations — all of these mean STOP

| Rationalization | Reality |
|---|---|
| "No callers, so it's dead code." | No callers is evidence about wiring, not intent. That exact reading nearly deleted a ratified extension point. Name the decision that authorised it before you call anything dead. |
| "The newer implementation is obviously the intended one." | Recency is not authority. Two parallel implementations mean an *unrecorded* decision — an undefined point, not a finding. |
| "The improvement plan says the target is X." | A plan is a proposal. Only an ADR, a requirement, or a ruling authorises a target state. Otherwise mark it `[UNVERIFIED]` and raise a UP. |
| "Twenty docs describe this; I'll synthesise them." | At least one of them is false. Docs give candidate *intent*; the tree gives *current state*. Never the reverse. |
| "I cited `09-improvement-plan.md`, that's grounded." | A citation to another document is not a citation to the tree. Current-state claims need `path:line`. |
| "The open questions read better collected at the end." | They get skimmed and lost there. Each one lives in the section it belongs to. The template has no Open Questions heading. |

## Workflow

1. **Authority set first.** Collect the ADRs, requirements, and rulings that govern this
   feature. Nothing else may justify a target state. Do this before opening the implementation.
2. **Ground current state in the tree.** Where the project has a code-graph MCP server, prefer it
   over grep — callers, callees, tests-for and impact radius are the questions this step asks, and
   a graph answers them structurally. Fall back to Read/Grep where there is none, or where the
   graph does not reach. Every current-state claim carries `path:line`; anything you could not
   verify is marked `[UNVERIFIED]` inline.
3. **Write the document** from `references/differential-template.md` to
   `<docs>/work/YYYY-MM-DD-<feature>-differential.md` — the dated-work-record directory of the
   canonical tree, with the docs root read from `.ai-badger/config.json`'s `docs.root` (default
   `docs/`). If `work/` does not exist, create it with a README first (`scaffold-documentation`
   does this); do not invent a directory beside it and do not fall back to a scratch path.
4. **Collect the rulings with a generated review form** — invoke `owner-gate-review`,
   one card per undefined point. The reviewer clicks a verdict and types notes; the result lands
   as a markdown file the agent watches for, so nothing is hand-edited and nothing is pasted.
   Then **stop**. Do not proceed, do not answer your own undefined points, do not start planning.
   *No browser available?* Fall back to the in-document `<answer>` blocks below.
5. **On feedback return:** read every answer in full — verdict *and* note, and on the fallback
   path the whole `<answer>` block. Nothing is open by the form path's `## Not answered` list or
   by a remaining `UNANSWERED` token on the fallback path; both must be resolved before you go
   further. Review the answers as answers — challenge contradictions on the spot ("in §3 you
   chose A, this answer implies B; which wins?"), and any answer that opens a new gap becomes a
   new UP block, sent back. Silent deferral is forbidden.
6. **Brainstorm what the feedback opened up.** Where the project has a brainstorming skill —
   `superpowers:brainstorming` if it is installed — use it, and its design doc is the
   **refactor specification**. Otherwise write that specification yourself in the same shape:
   one decision per open question, each with the options considered and the one chosen. The
   reconciled verdicts are its input either way.
7. **Plan it.** Where the project has a planning skill — `superpowers:writing-plans` if
   installed — hand the specification to it. Otherwise write the plan directly: numbered,
   bite-sized tasks, each naming the files it touches and the test that proves it. Both
   documents join the differential in `<docs>/work/`, dated the same way; do not create a new
   home for them.

**The differential document is the midpoint, not the finish.** The skill's terminal output is a
refactor specification plus an implementation plan. Reporting the document as the deliverable is
an incomplete run.

## Undefined-point block — fixed, greppable shape

Every undefined point sits **inside the section it belongs to**, and looks exactly like this. The
`<answer>` slot is the **fallback** collection path, for when no browser is available — the
default is the generated review form (step 4). The block shape itself is not optional either way:
it is what the form's cards are built from, one card per UP, ids matching.

```markdown
#### UP-3 — Which of the two ingest paths survives?

**Question:** One precise, answerable question. No compound questions.

| # | Proposition | Trade-off |
|---|-------------|-----------|
| A | ... | ... |
| B | ... | ... |
| C | ... | ... |

**Feedback:**
<answer question="3">
UNANSWERED
</answer>
```

- Heading matches `^#### UP-\d+ — `, numbered sequentially across the whole document.
- **Exactly three** propositions, lettered A/B/C, each with a stated trade-off. Not two. Not four.
- The answer slot is a **delimited block**, not a line. Humans write multi-line answers, follow-up
  questions and half-decisions; a single-line slot silently loses everything after the first
  newline. Learned the hard way: a first pass used a line-shaped slot, and a reader parsing only
  that line concluded six points were unanswered when every one had a written answer beneath it.
- `question="<n>"` repeats the UP number so a block is self-identifying even when moved.
- The human replaces the `UNANSWERED` token inside the block. It appears **nowhere else** in the
  document, so answered and unanswered are distinguishable by inspection.
- **Read the whole block, never just the marker line.** An answer may be a counter-question — that
  is a legitimate answer and means the point stays open until you resolve it.

Fallback-path checks (the form path reports its own counts and a `## Not answered` list):

The commands below use POSIX shell features and are intended for macOS/Linux. On another
platform, use equivalent finite commands that preserve the same checks.

```bash
D=docs/work/YYYY-MM-DD-<feature>-differential.md
grep -c '^#### UP-' "$D"                                       # total points
grep -c 'UNANSWERED' "$D"                                      # still open — must reach 0
diff <(grep -o '^#### UP-[0-9]*' "$D" | grep -o '[0-9]*$') \
     <(grep -o '<answer question="[0-9]*"' "$D" | grep -o '[0-9]*')  # every point has a slot
# print every answer in full — this is how you read them, not by grepping one line
awk '/<answer question=/{f=1} f{print} /<\/answer>/{f=0}' "$D"
```

## Diagrams come first

The reader's first contact with the document must be a diagram, not prose. Open with a single
**end-to-end pipeline view** — the whole path from raw input to final artefact, with every step
between — before the rulings, before the current-state inventory. The detailed have-vs-will-have
views follow later.

Rationale from the first real run: the reviewer read the four flow views last, and on seeing them
immediately restructured a decision they had already answered. A diagram surfaces a modelling
error in seconds that prose hides for pages.

## Repo constraints this output must respect

- **Not a system description.** The document is a *decision artefact*, dated and scoped. It goes
  stale the moment work lands and is **not authoritative about the running system** — same hazard
  as a parked-feature doc. Say so in the header. Never let it become a competing home for
  architecture truth; the canonical docs stay canonical.
- **No new doc home.** `<docs>/work/` takes the differential, the spec and the plan alike — all
  three are dated work records, and kind is not a subject. Do not add a numbered file to an
  existing review corpus, and do not group `work/` into `designs/`, `specs/` and `plans/`
  subdirectories: that grouping is how twenty overlapping documents happened, and it was removed
  from this project in PR #111.
- Architecture-level target states need an ADR; the plan must include writing it.
- TDD is mandatory and one task = one PR — the plan reflects both.

## Relationship to a spec skill

Where the project has one — a `/spec` skill that turns a vague idea into an agreed specification
by relentless questioning — borrow its discipline: never assume, every gap becomes a question,
nothing advances while open questions remain. Do **not** re-run its full questionnaire: a drifted
feature already has answers in the tree, and the UP blocks are that questioning applied to a
differential. If the refactor turns out to require a genuinely new sub-feature with no existing
code, hand that sub-feature to the spec skill rather than growing this document.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- About to write "dead code", "unused", "legacy", or "safe to delete" without naming the decision
  that authorised the thing
- Deriving a target state from a plan or review document instead of an ADR/requirement/ruling
- An "Open Questions" section forming at the end
- Two propositions, or four
- A current-state claim with no `path:line` and no `[UNVERIFIED]`
- Hand-editing answer slots when a browser was available — that is the fallback, not the default
- Reading only an answer's first line, or only a card's verdict
- No Mermaid diagram for one of the four flow views
- Treating the differential document as the deliverable

## Verification Checklist

- [ ] The authority set was collected before implementation code was read
- [ ] Every current-state claim has a `path:line` citation or `[UNVERIFIED]`
- [ ] Every undefined point has exactly three propositions and a delimited answer block
- [ ] The generated review form uses a unique `CONFIG.storageKey` and finite watch
- [ ] Every returned verdict and note was reconciled before brainstorming or planning

## Token efficiency

Where a code-graph server is available, ask it for the minimal context for the task before any
other query, and start at its least verbose detail level — escalate only when that is
insufficient. A differential touches a lot of tree; reading it all is how the budget goes.