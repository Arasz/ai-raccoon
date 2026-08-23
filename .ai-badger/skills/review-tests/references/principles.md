# L0 — Principles (8)

Eight ideas that settle conflicts between L1/L2/L3 rules (see `conflicts.md`) — the "why" a rule
descends from, not a checklist. Invariant-style on purpose: one idea, one paragraph, the failure
that proves it, **no falsifier field, no severity**. A finding never cites an L0 id; only a
*ruling* does, when the review has to explain why one rule beat another. A principle with a check
is just a badly-scoped rule — the moment `T0-02` grew a falsifier it would duplicate `T1-CST-04`.

**`T0-01`** — A check you have not seen fail is not a check.
*Settles:* whether a gate that has only ever passed may be trusted. Source: invariant `prove-the-check-fails`.

**`T0-02`** — Coverage is exposure; what survives a mutation is verification.
*Settles:* arguments that a coverage percentage is itself evidence of quality.

**`T0-03`** — A test's name is a claim, and the body must prove all of it.
*Settles:* whether a partially-proved name is "close enough".

**`T0-04`** — The oracle must not come from the code under test.
*Settles:* whether a mirrored assertion counts as an independent check.

**`T0-05`** — Every double is an unverified claim about production.
*Settles:* whether a fake's behaviour needs its own proof, or is assumed correct by construction.

**`T0-06`** — Determinism is a property of the test, not of the machine.
*Settles:* whether a flake is the environment's fault or the suite's.

**`T0-07`** — Design the suite from failure modes, not from the code's shape.
*Settles:* whether "one test per method" is an adequate design method (it is not).

**`T0-08`** — The cheapest test that can *observe* the defect wins — a runner that cannot observe it is not cheap, it is blind.
*Settles:* FIRST's "Fast" against `T1-CST-05`: speed never buys a blind runner (`conflicts.md`
C20). Amends L3a's original P8 ("the cheapest test that catches the defect wins") to name the
structural-blindness failure mode L3b proved independently in three stacks: a DOM shim with no
layout engine, an in-memory database with no SQL, an emulator that does not model RU accounting.
