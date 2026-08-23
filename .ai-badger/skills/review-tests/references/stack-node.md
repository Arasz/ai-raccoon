# Stack — Node

No proven rules yet — see `governance.md` for the bar a rule must clear before
it is added here. This is not an empty checklist: every L1 (`T1-*`) and L2
(`T2-*`) rule already applies unchanged to a Node/Bun codebase — isolation,
oracles, doubles, structure, cost and proof do not vary by runtime. What is
missing is the Node-specific *residue* those rules would specialise (test
runner quirks, module-mocking traps, event-loop determinism) — nothing proven
locally licenses inventing one.

See `node.instructions.md` for the project's existing Node/Bun conventions
(package-manager discipline, native-API preference, single-writer datastore
access) — cited here rather than restated as a test rule.

## How to contribute a rule

Read `governance.md` §"Adding a rule" before adding anything: a stack rule needs a `parent:`
L1/L2 id, a real proven failure (a repro, an incident, or an authoritative
source read directly — not a vendor-blog paraphrase), and a falsifying `check:`.
An empty stack file is better than an invented one.
