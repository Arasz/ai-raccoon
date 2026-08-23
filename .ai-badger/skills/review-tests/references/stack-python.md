# Stack — Python

No proven rules yet — see `governance.md` for the bar a rule must clear before
it is added here. Every L1 (`T1-*`) and L2 (`T2-*`) rule already applies
unchanged to a Python codebase; no researched Python ruleset exists in this
project's lanes to draw a stack-specific residue from (pytest fixture scoping,
`freezegun`/time-seam conventions, async-test-marker traps). Nothing proven
locally licenses inventing one.

See `python.instructions.md` for the project's existing Python conventions
(typed signatures, pytest, guard clauses, dependency pinning) — cited here
rather than restated as a test rule.

## How to contribute a rule

Read `governance.md` §"Adding a rule" before adding anything: a stack rule needs a `parent:`
L1/L2 id, a real proven failure (a repro, an incident, or an authoritative
source read directly — not a vendor-blog paraphrase), and a falsifying `check:`.
An empty stack file is better than an invented one.
