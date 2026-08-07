---
name: migrate-documentation
description: >-
  Use when an existing documentation tree must be reorganised wholesale — "migrate the docs",
  "reorganise docs/", hundreds of files with no structure, overlapping documents that contradict
  each other, a docs directory nobody can navigate, or documentation whose accuracy is unknown and
  must be established before anyone relies on it. Also use to resume a migration already in
  progress. Not for creating a tree that does not exist (use scaffold-documentation) and not for a
  single documentation change (use update-documentation).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [documentation, migration, trust, resumability]
    related_skills: [scaffold-documentation, update-documentation]
---

# Migrate the documentation tree

**The terminal deliverable is documentation someone can trust — not a reorganised tree.** A tidy
tree full of unverified claims is a failed migration that looks like a successful one, and that is
the recorded failure mode: a green artefact that proved nothing.

This runs over days and across compactions, so it is resumable by construction. **Everything that
must survive a compaction lives in a committed state file — never in the todo list.** A todo list
dies with the session; a resumed session that trusts one silently re-does or skips work. The state
file lives in the docs tree's `meta/` area; a project may name it via `.ai-badger/config.json`'s
`docs.stateFile`.

References, each owned by the skill whose primary concern it is: read
`../update-documentation/references/placement.md` **when a target path is in doubt**,
`../update-documentation/references/trust.md` **when freezing**,
`../update-documentation/references/amendments.md` **when amending**, and
`../scaffold-documentation/references/structure.md` **when the canonical tree is in question**.
Read them where they live; do not copy them
into a shared directory, which cannot ship.

## Step 1 — always, every session, before anything else

Read the migration state file. **Postcondition:** you know the current phase, the count
(`n/total`), and which single item is in progress. That is the whole context a resumed session
needs — no transcript, no memory file. **If an item is in progress, finish it.** Never hand
yourself a second one; one item at a time is the design, not a constraint to work around.

Then go to the step matching the phase it recorded.

## Step 2 — Phase 0: the ADR

Record the trust model as an ADR. A change of this shape needs a recorded decision.
**Postcondition:** the ADR exists with `Status: Accepted`, carries the normative banner from
`trust.md`, and appears in the project's ADR index.

## Step 3 — Phase 1: link-check and freeze, against the *current* tree

Resolve every relative link in the tree, and derive the freeze list, **before any file moves**.
**Postcondition: do not proceed until every link resolves.** Until it does, nothing else is safe to
run, because nothing else can tell you what you broke. The freeze list is the do-not-touch list —
derived from the build and from live work, never from your judgement (`structure.md`).

## Step 4 — Phase 2: inventory, no moves

Classify every path as `document` / `work` / `frozen` / `non-document` / `excluded` using
`placement.md`, and write the result plus the boundary commit into the state file. Judgement, one
path at a time. **Postcondition:** every tracked path under the docs root has exactly one row, and
the boundary SHA resolves (`git cat-file -e <sha>`). No file has moved yet — classification is
reviewable before anything is irreversible.

## Step 5 — Phase 3: move, content unchanged

`git mv` **in a commit that changes nothing else**, record the move with its old path, then relink.
Cap the batch — roughly 15 files per PR — so a human can read the diff.

**Postcondition:** `git show --stat` for the move commit shows renames only, zero content diff;
every link still resolves; the vacated path is gone from disk and has a recorded edge explaining
where it went. Verify the move landed as a rename — `git log --follow` must cross it. Observed in a
real migration: a PR promised `git mv`, did not use it, and nothing caught that.

**No redirect stub.** The recorded old→new edge *is* the record of the move, and a resolver over
those edges answers for any referrer nobody can edit — merged PRs, issue bodies, commit messages,
frozen ADRs. A stub at a vacated path is a second, hand-maintained answer to a question the record
already answers.

Never copy-then-swap into a shadow tree. It silently discards every edit made during the migration
window, destroys `git log --follow` across the whole corpus (rename detection needs high similarity
and you are rewriting content), and breaks every inbound reference at once — build files, CI
workflows, source-code doc comments, and tests that cite docs as the source of their expectations.
A raw count of inbound references is a ceiling, not a work list: most of it is backticked prose no
scanner reads. Relink what the link check actually reports, and let the move record serve the rest.

## Step 6 — Phase 4: scaffold and baseline

Invoke `scaffold-documentation`, then write the baseline into the state file. **Structure gate: do
not proceed to extraction until the tree matches the canonical one**, and the baseline records
**zero dead links and zero undischarged reference obligations**. Those two start at zero because a
migration must never create one. Extracting into a tree that does not yet match means moving the
extracted text again later, at the cost of its history.

## Step 7 — Phase 5: extract, one item at a time

Take exactly one item. Move **one top-level `##` section per commit**, mark the source span
`processedto`, and update the cursor in the state file **in the same commit as the content it
describes**. **Postcondition:** the cursor advanced by exactly one section, and the state file and
the content landed together. Three failed attempts on the same item is a signal to escalate, not to
retry a fourth time.

**Evidence gate: do not proceed to the next item until every claim you carried forward as verified
has an `evidence=<path>:<line>` you opened.** This is the gate an agent will self-certify — **"I
checked it" is not a check, and a plausible line number is not a line number.** Carrying a legacy
claim across unverified is how the old tree's errors survive the migration wearing a fresh
timestamp.

Source text is **never deleted at processing time.** It is marked `processedto`, which turns "did
the content transfer?" from a judgement into arithmetic.

## Step 8 — the drain gate

A file in the legacy staging area is deleted **only** when both hold:

1. **residual == 0** for it — every block in it is accounted for, and
2. every recorded `processedto` target exists **and contains the span id**.

**Do not delete until you have that report in front of you, for that path** — and paste it into the
deletion PR. This is the one irreversible step in the whole migration; everything else is a commit
you can revert. Deletion also **never ships in the same PR as its replacement** — that converts
"did the content transfer?" into a diff a human can read.

Four checks make this unsatisfiable by writing plausible text:

- **Block conservation.** Every fenced block, diagram and pipe table appears byte-identically
  post-migration, or is listed as a deliberate drop with a reason. A run that scans zero blocks
  fails loudly rather than reporting success.
- **`processedto` targets must resolve** — and name a file that `git diff --name-status
  <base>...HEAD` shows this branch actually wrote.
- **Identifier conservation.** Stable identifiers the project declares (requirement ids, rule ids)
  must all still exist. A vanished one fails.
- **The separate-PR rule** above.

Where the project has no tool, these are counted by hand and the counts go in the PR body. A count
you did not produce is not a check.

## Step 9 — retargeting referrers: never automated blindly

A move leaves references pointing at a path that no longer exists. Split them: a citing file on the
freeze list is left alone and served by the move record; anything else must be rewritten to the new
path. **Read the diff of any bulk rewrite before committing it**, and record the resulting content
change, or the next consistency check goes red on content drift.

**Postcondition:** every link resolves, and every recorded move resolves to a file that exists.

Deleting the vacated file is part of the move commit, not a later step — there is no stub to
retire, and a stub reappearing at a vacated path is a defect.

## Scripts versus judgement

A step whose output is checkable is a script call, where the project has a script. A step needing
judgement is prose followed by a check of its postcondition. **Never script:** Diátaxis
classification, verifying a fact, writing an amendment's reason, or deciding a source file is
drained. A script there produces confident garbage — plausible text that passes review precisely
because it sounds finished.

## The honest exit

**A partial migration is a correct outcome.** Stop at any item boundary, commit the state file, and
report the counts it holds.

**A migration reported complete while the state file shows pending items is a failed run** — a
worse outcome than stopping, because it retires the only signal that work remains.

## Gotchas

No environment-specific gotchas known.

## Red flags — STOP

- Starting a session without reading the migration state file
- Any state that matters living only in the todo list
- A second item in progress
- Deleting a legacy file before the drain report exists for it
- A delete in the same PR as its replacement
- Moving and rewriting content in one commit
- A copy-then-swap shadow tree, in any form
- Renaming a file pinned by project configuration or agent-instruction files inside a migration PR
  — that is its own PR, it rewrites the agent discovery file and every scoped instruction file
- A `git mv` with no matching move record — the move is unrecorded and nothing can answer for it
- Creating a redirect stub at a vacated path
- Reporting completion while the state file shows pending items

### Rationalizations — every one of these means STOP

| Rationalization | Reality |
|---|---|
| "The content is clearly covered by the new page, I'll delete the source." | `residual == 0`, or it is not covered. "Clearly" is the word that precedes every vacuous migration. |
| "I'll write `the state machine is described in explanation/flows.md` and mark it processed." | That sentence is exactly the failure mode this gate exists for. A `processedto` target must contain the span id and be a file this branch wrote. |
| "I'll batch the remaining 40 files, it's faster." | One item at a time. Batching is how a compaction lands mid-batch and nobody can tell what transferred. |
| "The todo list has the remaining work, that's enough." | The todo list does not survive a compaction. The state file is committed. |
| "It's 90 % done, I'll report it complete and file the rest." | The state file will contradict you in the next session. Report the counts. |
| "This file is obviously stale, I'll drop it rather than migrate it." | Dropping needs a recorded row with a reason. Silent drops are indistinguishable from bugs. |
| "The freeze list is over-cautious for this one file." | It is derived from the build. A file the build parses at runtime corrupts from a static initializer in production, not at compile time. |
| "I'll move and rewrite in one commit to save a round trip." | Then the rename is undetectable and `git log --follow` dies across the corpus. Move, then rewrite. |

## Verification Checklist

- [ ] State file committed and shows zero pending
- [ ] A drain report exists for every deleted legacy file
- [ ] No delete shipped in the same PR as its replacement
- [ ] Every move recorded — `git mv` plus a matching move record
- [ ] Freeze list respected
