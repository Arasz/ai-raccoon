# kind-architecture

Applies to a fitness-function / dependency-rule test — ArchUnitNET, a reflection-based assembly
scan, a forbidden-namespace guard. The runner is the rule engine itself, and the central hazard is
that these rules fail silently: a matcher over an empty set, a rule hosted where it cannot fire, a
finer rule doing nothing a coarser one does not already cover. `T2-ARCH-01`/`02` are this kind's
instance of `T1-PRF-01`/`T1-PRF-03` — the pointer, not a restated body.

**`T2-ARCH-01` — Plant a real violation and watch the rule go red. An architecture rule you have not seen fail is presumed inert.**
- *design:* n/a — proof step, not a writing step.
- *review:* the only way an architecture rule is provable at all; a vacuous architecture gate silently licenses an entire forbidden dependency class, which is worse than one missed unit test.
- *check:* the PR that adds or edits the rule shows the planted violation and the red run.
- **severity:** blocker · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-01`
- *cites:* `evidence.md` (archunitnet-rules-pass-vacuously).
- *meta:* pass=8 order=30

**`T2-ARCH-02` — A rule over an empty matched-type set passes: assert the subject count is non-zero first.**
- *design:* precede every rule with an assertion that its subject set is non-empty.
- *review:* the matcher silently filters to nothing when the named type is not in the loaded architecture — no error, no zero-match diagnostic.
- *check:* `grep -B2 'Types().That()' <rule file>` — does a subject-count assertion (e.g. `.Should().HaveAtLeastOneType()`) precede the rule's evaluation call? Its absence is the violation.
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-PRF-03`
- *cites:* `evidence.md` (archunitnet-rules-pass-vacuously).
- *meta:* pass=1 order=9

**`T2-ARCH-03` — Where the rule lives determines whether it can fire.**
- *design:* host the rule in a test project that references the assemblies it constrains — a rule cannot fire against a dependency it structurally cannot see.
- *review:* three cross-cutting rules in one project could not have fired from their host project at all, because it referenced neither the logging nor the persistence SDK they forbade.
- *check:* does the test project hosting the rule reference the assemblies that could violate it?
- **severity:** blocker · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-03`
- *cites:* `evidence.md` (archunitnet-rules-pass-vacuously).
- *meta:* pass=1 order=9

**`T2-ARCH-04` — Prefer the coarser mechanism when it is airtight; keep the finer one only if it demonstrably fires.**
- *design:* if a reflection-based assembly-reference check already makes a violation structurally impossible, delete the sibling rule that adds nothing.
- *review:* a forbidden-namespace list enforced only by an inert finer rule reads as coverage that is not there.
- *check:* has the finer rule ever gone red under a planted violation (`T2-ARCH-01`'s proof)? Evidence: if not, and a coarser reflection-based assembly-reference check already forbids the same dependency, the finer rule is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-04`
- *cites:* `evidence.md` (archunitnet-rules-pass-vacuously).
- *meta:* pass=8 order=32

**`T2-ARCH-05` — The forbidden/allowed list is derived from what the repo actually contains, and drift fails the build.**
- *design:* generate the list from the live dependency manifest rather than typing it once and forgetting it.
- *review:* a list still naming a package a decision already deleted is a guard that can never fire against something that appears nowhere in the repo.
- *check:* auto — diff the list against the live dependency manifest.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-03`
- *cites:* `evidence.md` (archunitnet-rules-pass-vacuously); invariant `derive-or-delete-the-list`.
- *meta:* pass=0 order=3

**`T2-ARCH-06` — Do not reach for an architecture rule to express a data-flow property inside a method body.**
- *design:* where the defect is "a transition without its companion side effect," make the companion a required argument on the transition instead of writing a rule that vocabulary cannot express.
- *review:* an architecture-rule vocabulary cannot see inside a method body; a rule attempted there stays inert by construction, not by oversight.
- *check:* does an architecture rule's matcher attempt to express a data-flow property inside a method body — e.g. "this call site must also call that one" — rather than a type/assembly/namespace dependency? Evidence: read the rule's predicate; if it cannot be expressed over types and references alone, it is the violation regardless of whether it currently passes.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-04`
- *cites:* `evidence.md` (incomplete-state-transitions).
- *meta:* pass=6 order=23
