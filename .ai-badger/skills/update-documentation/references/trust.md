# Trust — three axes, markers, and the obligation ladder

## Why not one `trust` scalar

A single trust flag conflates *where a claim came from* with *whether it is true*, and a tag that is
constant across the whole corpus carries no information. Three independent axes:

| axis | values | who sets it | cost |
|---|---|---|---|
| **provenance** | `migrated` \| `authored` | the boundary commit, mechanically | free |
| **modality** | `descriptive` \| `normative` \| `mixed` | the author, once per document | seconds |
| **verification** | per claim: unverified \| checked-with-evidence | whoever **uses** the claim | minutes, lazy |

Frontmatter carries **`provenance` and `modality` only**. Any `trust:` field is a derived echo,
recomputed from the markers — writing it by hand asserts a value nothing can justify.

`modality` is what makes the verification backlog finite: **a normative document owes no
verification.** It is a decision, not a description. There is nothing to check it against.

## The boundary is a commit, not a decision number

Provenance splits on a **commit SHA plus ISO date**, recorded in the docs tree's `meta/` area. It
totally orders every file, survives clone, is verifiable with `git cat-file -e`, and cannot be
renumbered.

Do not use "everything after the last ADR is trusted". Observed in a real corpus: nine consecutive
ADRs each carried a verbatim disclaimer that they *ratify a design and do not describe the running
system* — exactly the block an ADR-number boundary would stamp trusted — while the oldest ADR in
the tree was still exactly true and enforced by a CI test. The boundary as commonly proposed marks
the least reality-tracking documents trusted and the most reality-tracking suspect. Decision
numbers are not necessarily monotone either: renumberings and reserved-but-unwritten numbers both
occur.

## Marker grammar

The machine layer is HTML comments: greppable in visible files, invisible in render, and impossible
to paste into prose as content. Emit them exactly in these shapes.

```markdown
<!-- trust:untrusted id=u-0142 scope=block src="legacy/architecture.md#L88" -->
Claim text carried over from the legacy document.

<!-- trust:trustchecked kind=claim id=u-0142 date=2026-01-15 rev=1afb2ca6
     evidence="src/storage/profile_repository.ext:41" -->
Every profile is partitioned by its owner key.

<!-- trust:trustchecked kind=ref target="<docs>/adr/0062-pipeline-extension-points.md"
     date=2026-01-15
     evidence="target is modality:normative; cited as a decision, not as behaviour" -->

<!-- trust:processedto id=u-0142 scope=begin target="<docs>/reference/data-at-rest.md" --> … <!-- trust:end id=u-0142 -->
```

Rules to *not fight*:

- `kind` is mandatory. `kind=claim` requires `id` and forbids `target`; `kind=ref` is the reverse.
  Without it, nothing can tell which obligation a marker discharges.
- **A per-link obligation arises from an `aspirational` target only** — "cite me as a decision,
  never as a description" is a property of the citation, so a receipt can discharge it. A `limited`
  target carries a per-*claim* duty instead, discharged by verifying the claim, not by a receipt.
- **Obligations come from the link graph, not from markers.** A marker is a receipt, never a claim
  that an obligation exists. You cannot make work disappear by deleting a marker.
- **Links into the legacy staging area are forbidden outright.** Provenance travels in `src=`,
  which is not a link.
- `evidence=` is `<path>:<line>` for a claim about the running system, or a one-clause justification
  for a `kind=ref`.

## The mandatory banner

Every `modality: normative` document carries this line immediately after its H1:

> **This document records a decision. It does not describe the running system.**

The HTML comments are invisible in render; a human reading the page needs one visible signal, and
this is it. A normative document without the banner is not finished.

## The ratcheted baseline

Nothing **fails on the pre-existing backlog** — only on malformedness and on regression. Backlog
counts ratchet against a recorded baseline and may only decrease. Dead links and undischarged
reference obligations start at **0**, because a migration must never create one.

This is not leniency, it is survival: **a gate that is red on day one gets suppressed within a
week**, and then nothing is gated at all.

## The obligation ladder — lazy trust

**The trigger is use, not sight.** Reading a file owes nothing. *Citing a claim* owes something.

- **Rung 0 — don't use it.** Free, and explicitly permitted. Say the claim is unverified and move on.
- **Rung 1 — verify in ≤2 tool calls**, stamp `trustchecked` with `evidence=`. This is the intended
  rung and where nearly everything should land.
- **Rung 2 — it is wrong.** Correct it and add an amendment row naming what was wrong
  (`amendments.md`).
- **Rung 3 — you cannot verify it cheaply.** Add `blocked="<what would settle it>"` and one row in
  the docs tree's trust-debt file. Not an issue tracker; issues about documentation die unread.

**Hard cap: 2 spans per task.** More than two and the task becomes a verification project and the
original work never lands. Against the opposite failure — never verifying anything —
`update-documentation` must resolve untrusted spans **inside the blocks it edits**, which is not
subject to the cap being "used up" elsewhere.

**The backlog will never reach zero, and that is correct.** An untouched untrusted span is a claim
nobody relies on. Driving it to zero means verifying claims no reader ever asked about, at the cost
of the ones they did.

## Verifying evidence by hand

Where a project has no checker, the obligation does not lapse — it costs you the manual pass.
**Every `evidence=<path>:<line>` you wrote must resolve to a line you actually opened.** Re-open
each one and confirm the line still says what you cited it for. "I checked it" is not a check, and
a plausible line number is not a line number: the failure this catches is a line number inferred
from a symbol name, which is right often enough to be trusted and wrong often enough to matter.
