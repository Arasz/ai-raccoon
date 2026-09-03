---
name: update-documentation
description: >-
  Use whenever documentation must change to match something that already changed — after a code
  change, ADR, schema change or PR lands, and when the user says "update the docs", "document
  this", "add a how-to for X", "the README is wrong", "this doc is stale", or a reviewer reports
  docs drift. Also use before creating any new document, to decide where it belongs. Triggers
  include a doc that contradicts the code, a fact nobody can source, and a new page with no
  obvious home. Not for creating the docs tree (use scaffold-documentation) and not for
  reorganising it wholesale (use migrate-documentation).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [documentation, diataxis, evidence, amendments]
    related_skills: [scaffold-documentation, migrate-documentation]
---

# Update documentation

The hot path: one documentation change, correctly placed, evidence-backed, and recorded.

**What a finished update IS** — five parts, all present:

1. Exactly one target path, chosen by `references/placement.md`.
2. Prose in which **every statement about the running system carries `evidence=<path>:<line>`**.
3. Untrusted spans inside the blocks you edited, resolved — not left for later.
4. An amendment row if you corrected something that was false.
5. A ledger entry, if the project keeps a ledger.

For a substantial **new** document, one more: a reader test (step 9). Not for edits.

References: read `references/placement.md` **when choosing a target path**,
`references/trust.md` **when an evidence line is challenged**, `references/amendments.md` **when
phrasing an amendment's reason**. The tree
and filename grammar live with the skill that creates them —
`../scaffold-documentation/references/structure.md` — because structure is that skill's primary
concern; read it **when the canonical tree is in question** — do not copy it into a shared
directory, which cannot ship.

## Steps

1. **Name the change in one sentence** — what became untrue, or what is now true that no document
   says. **Postcondition:** you can point at the commit, ADR, or diff that caused it. If you
   cannot, you are documenting an intention, not a change; stop and go write the ADR instead.
2. **Choose the target path** by running `placement.md`'s gates in order. Judgement — a script
   cannot classify Diátaxis and would produce a confident wrong answer. **Postcondition:** one
   path, and its parent directory exists (`test -d "$(dirname <path>)"`). If the directory is
   missing, invoke `scaffold-documentation` first; do not create a directory by hand.
3. **Read the target in full** and list its trust markers:
   `rg -n 'trust:(untrusted|trustchecked|processedto)' <path>` (or `grep -nE`). **Postcondition:** you know which
   spans you are about to edit are untrusted. Editing around an untrusted span you touched is how
   a false claim gets a fresh timestamp and looks verified.
4. **Resolve the untrusted spans inside the blocks you edit**, using the obligation ladder in
   `trust.md`. Hard cap **2 spans per task**; anything beyond goes to the docs tree's trust-debt
   file with `blocked="…"`. **Postcondition:** a search for `trust:untrusted` in `<path>` returns no marker
   inside a block you edited. A span left `untrusted` in edited text is a false claim with a fresh
   timestamp.
5. **Write the change with visual focus & humanizer discipline.**
   - **Visual-first preference:** Represent complex flows, architectures, state transitions, or component relationships with visual diagrams (Mermaid flowcharts, sequence diagrams, mindmaps) instead of dense paragraphs.
   - **Low-noise root READMEs:** Keep root and index READMEs concise (high-level architecture diagram + feature matrix + quick start) and link out to dedicated Diátaxis pages for deep dives.
   - **Humanizer pass:** Apply `humanizer` rules to strip AI writing tells (`serves as`, em-dashes `—`, filler connectors `Additionally`, `Furthermore`, `At its core`). Keep sentence lengths bursty and active.
   - Each factual statement about the running system gets a `trustchecked` marker with `evidence=<path>:<line>` you actually opened.
   **Postcondition:** every claim you would defend in review has a marker; anything you would not defend is either deleted or written as unverified.
6. **Evidence gate. Do not proceed until every `evidence=` path and line resolves to a line you
   opened.** Re-open each one and confirm it still says what you cited it for. This is the gate an
   agent will self-certify — **"I checked it" is not a check, and a plausible line number is not a
   line number.** A failure here means the evidence is wrong, never that the check is too strict.
7. **Add an amendment row** if step 5 replaced a false statement — `| Date | Commit | Reason |
   Change |`, per `amendments.md`. Substantial corrections get the prose block beneath it too.
   **Postcondition:** the row's Reason names the false statement in the past tense. If you cannot
   name one, you were editing, not amending; remove the row.
8. **Record gate.** Append the ledger entry and regenerate the projections the project derives from
   it (changelog, index, frontmatter `version:`/`updated:`). **Postcondition:** the entry exists and
   the projections are current. **Reporting this task complete without the record is a failed run**:
   the projections are stale and the next person inherits the failure.
9. **Reader test — substantial *new* documents only.** Skip it for an edit to an existing
   document; this gate asks whether a page nobody has read yet can actually be used, and step 6
   does not answer that — evidence proves the claims are true, not that a reader can act on them.
   Write 5–10 questions a reader would realistically arrive with, then dispatch each to a **fresh
   subagent given only the document** — no repo access, no conversation context, because the point
   is to surface what only the author knows. Ask each for its answer plus anything it found
   ambiguous or had to assume. **Postcondition:** every wrong answer and every reported ambiguity
   is either fixed in the document or recorded as out of scope with a reason. One round, capped
   like step 4: a second round means the document needs rewriting, not re-testing.
10. **Final check.** **Postcondition:** the file's recorded content hash matches what is on disk —
   if you edited after recording, record again — every relative link in the file resolves, and
   `version:` is whatever the ledger says, not a number you typed.

## Scripts versus judgement

A step whose output is checkable is a script call, where the project has a script. A step needing
judgement is prose followed by a check of its postcondition.

**Never script:** Diátaxis classification, verifying that a fact is true, or writing an
amendment's reason. A script here produces confident garbage — a sentence shaped like a
justification that justifies nothing, which is worse than a blank, because a blank gets noticed.

**Always check mechanically:** does the path exist, does the evidence line resolve, does the hash
match, did the ledger accept the entry. Unautomated does not mean optional — it means you run the
check by hand.

## Placement, in one line

Falsification test first: if the code changed tomorrow and this became untrue, would you **edit**
it (→ a quadrant) or would editing it be **falsifying a record** (→ `work/`, dated filename)?
Everything else is in `references/placement.md` — read it **when the one-line test does not settle
the target**.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- Writing "the system does X" with no `evidence=` — that is the claim the evidence gate exists to catch
- An `evidence=` line number you inferred from a symbol name instead of opening the file
- Creating a new document before running the placement gates
- A new file in a quadrant whose name carries a date, or in `work/` whose name does not
- Editing a frozen build-input file, or adding frontmatter to one — it corrupts at runtime
- Hand-bumping `version:` because a check complained about it
- Linking into the legacy staging area — forbidden; provenance travels in `src=`
- More than 2 verification spans in one task, or zero when you edited an untrusted block
- Saying "docs updated" in your report when the change was never recorded
- Declaring a substantial new document done with no reader test — or rewriting the questions until
  the subagent gets them right, which tests the questions, not the document
- Reader-testing an edit: step 9 is gated to new documents on purpose, and running it on every
  change is how a per-task verification budget stops being a budget

> Step 9 carries over Stage 3 of the `doc-coauthoring` skill, authored by Anthropic
> ([anthropics/skills](https://github.com/anthropics/skills)). No licence file accompanied the
> captured copy, so its terms are unestablished here — the step is a restatement of the practice,
> not copied text.

## Verification Checklist

- [ ] Every `evidence=` line resolves to a real `path:line` — opened, not inferred
- [ ] The ledger accepted the entry
- [ ] No frozen build-input file touched
- [ ] Verification-span budget respected (≤2)
- [ ] Report matches what was recorded
