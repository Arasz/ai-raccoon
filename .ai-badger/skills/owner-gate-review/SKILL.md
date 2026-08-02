---
name: owner-gate-review
description: >-
  Use when a design, refactor or review document needs a per-decision ruling from one human
  reviewer and the answers must come back attached to the decision they belong to. Triggers:
  pasting a long document into chat and getting a wall of prose back, an answer that can't be
  matched to its question, a reviewer hand-editing answer slots in markdown, or a set of
  decisions that must each be approved, changed, rejected or deferred before work is scoped.
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [review, decisions, design, feedback]
    related_skills: [differential-feature-refactor]
---

# Owner gate review

Generate a **review form** — one card per decision, a verdict control and a notes box — that
emits structured markdown. The reviewer clicks and types; the agent gets back a file where every
answer is bound to its decision by construction, not by paragraph order.

**Why this exists.** Prose feedback loses the mapping from answer to question. Answer slots
hand-edited into markdown are better but still lose content: a line-shaped slot hid multi-line
answers, and a reader parsing only the marker line reported six points unanswered when every one
had a written answer beneath it. A form cannot lose the mapping — the DOM enforces it.

## Two variants, one interface

| Variant | Reviewer does | Agent gets it back by |
|---|---|---|
| **Local file** (`file://…/<slug>-review.html`) — **prefer this** | opens the file, answers, clicks **Save feedback** | watching for the result file on disk; no copy-paste |
| **Artifact** (published to claude.ai) | answers, clicks **Copy feedback**, pastes into chat | reading the pasted block |

Same template, same markdown output. Use the Artifact only when the reviewer is not on a machine
you can watch — an artifact page has no filesystem and cannot write next to itself.

## Agent-side protocol

1. **Write the form** from `references/form-template.html` to `<docs>/work/<date>-<slug>-review.html`
   — the dated-work-record directory of the canonical tree, where the docs root comes from
   `.ai-badger/config.json`'s `docs.root` and defaults to `docs/`. A project that keeps work
   records somewhere else uses that instead. Fill the `DECISIONS` array and all per-review
   `CONFIG` fields: `title`, `subtitle`, `source`, `outName`, `expectedDir`, and a unique
   `storageKey` such as `refinement:<slug>:v1`.

   **If `work/` does not exist, create it and give it a README before writing the form** — run
   `scaffold-documentation` if the project has it. Do not fall back to a scratch directory. This
   step named `docs/designs/` until 2026-08-01, a directory the canonical tree does not have; in a
   repo that did not have it either, two real review gates landed in `.tmp/` instead — gitignored,
   hidden, and unreachable by the ripgrep every agent's search is built on. A form nobody can
   grep is a review that did not happen.
2. **Pre-create nothing that could read as a real answer.** Do not write a stub result file, do
   not seed `localStorage`, do not fill any verdict "as an example". A pre-created result file
   makes the watch fire instantly and gets ingested as a review that never happened.
3. **Start the watch** (below) before telling the reviewer the form is ready.
4. **Tell the reviewer to press Clear first** if they have used a review form before — see the
   shared-origin hazard below. The Clear button exists for exactly this.
5. **Ingest** when the file appears: **read the file**, do not trust the notification's timing.
   Check the trailing `<!-- end refinement feedback -->` marker; without it the file was caught
   mid-write — re-read.
6. **Read every note in full.** A note may be a counter-question. That is a legitimate answer and
   means the decision stays open. Verdict alone is never the whole answer.
7. **Reconcile the `## Not answered` list explicitly.** Silence is not consent. Ask again or
   record the item as still open — never resolve it yourself.

### The watch

```bash
OUT="/abs/path/docs/work/2026-01-15-import-pipeline-feedback.md"
for i in $(seq 1 720); do [ -f "$OUT" ] && break; sleep 5; done   # capped: 720 × 5s = 1h
[ -f "$OUT" ] && echo "feedback landed at $OUT" || echo "timed out, no feedback"
```

Run it with `run_in_background: true`. The cap is mandatory: a watch with no stopping condition
is a loop nobody can answer "what ends this?" for, and it outlives the session that started it.
The example uses POSIX shell syntax and is intended for macOS/Linux; on another platform, use an
equivalent finite watcher and preserve the same one-hour cap.

**Caveats.** It fires on file *creation*: a reviewer who saves twice produces one notification,
not two, so the notification tells you a review exists, never that it is the final one — re-read
the file at ingest and re-read again if the reviewer says they changed something. If the reviewer
fell back to `a[download]`, the file is in the browser's download directory, not `$OUT`; the form
tells them so, but also check `~/Downloads/<OUT_NAME>` before declaring a timeout.

## What the save chain guarantees

The template's save chain is **remembered directory → one-time directory grant → `a[download]` →
clipboard → always-visible textarea**, and the UI names which link it used and where the file
went. Four consequences bind the protocol above:

- **The folder is granted once, not per save** — persisted as a `FileSystemDirectoryHandle` in
  IndexedDB under `<storageKey>:dir`. Point the one dialog at the folder holding the HTML.
- **Only Chromium has a picker.** Firefox and WebKit fall through to `a[download]`, so never tell
  a reviewer the file "will be" at `expectedDir` — read back what the UI reports.
- **An `AbortError` means the reviewer cancelled.** The chain stops there.
- **The storage key must be unique per review.** Every `file://` page shares one origin, which is
  why step 4 exists.

The measurements these rest on — three engines, two passes, and the four things still unverified —
are in `references/browser-capabilities.md`. Read it before changing the chain or blaming it.

## Writing decision cards

A card the reviewer cannot decide from without opening the source document has failed.

Each card carries exactly three things:

1. **Claim** — one line, present tense, stating what will be true if approved.
   "`X-Source-System` is the discriminator", not "Discriminator options".
2. **Detail** — the minimum needed to rule on it: the specific numbers, the names, the mechanism.
   Two short paragraphs at most.
3. **Why this matters** — the consequence of getting it wrong, or the thing that changed. This is
   what turns a shrug into a verdict.

Group cards under headings when the kinds differ (corrections / design / open items). Give every
card a short stable id (`D1`, `C2`, `O4`) — it is the join key in the result file.

## Common Pitfalls — STOP

- A result file that exists before the reviewer has opened the form
- Reading only the verdict and skipping the note
- Treating an item under `## Not answered` as agreement
- A storage key that isn't unique to this review
- A card whose detail is "see §4 of the design doc"
- An uncapped watch loop
- Claiming the save worked because the code path exists — the UI reports the outcome; believe the
  reported outcome, and if the reviewer says nothing, check the file

## Verification Checklist

- [ ] `CONFIG.storageKey` is unique to this review and does not retain the template example
- [ ] `CONFIG.outName` and `CONFIG.expectedDir` match the watch path
- [ ] No result file was pre-created and the watch has a finite stopping condition
- [ ] The saved result ends with `<!-- end refinement feedback -->`
- [ ] Every answered decision's verdict and complete note were read before reconciliation

## Files

- `references/form-template.html` — the generator template, parameterised by `DECISIONS`.
- `references/result-template.md` — the exact markdown shape the form emits, so the parser knows
  what to expect.