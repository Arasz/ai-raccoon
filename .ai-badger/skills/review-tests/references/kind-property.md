# kind-property

Applies when a pure function or law-bearing type gets a generated-input test — FsCheck, CsCheck,
Hedgehog. The runner is the property-testing framework's own generator/shrinker; what it can prove
depends entirely on the generator being bounded to the domain and the seed being pinned, which is
why `T2-PRP-05` points straight back at `T1-ISO-03` rather than restating it.

**`T2-PRP-01` — Reach for a property test when a law exists; otherwise write examples.**
- *design:* name the law in one sentence before writing the generator — round trip, invariant, idempotence, commutativity, an independent oracle, a metamorphic relation.
- *review:* the test cannot name its law → it is a random-input smoke test wearing a property-test's clothes; use examples instead.
- *check:* argued — the test names the law it encodes.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-ORC-02`
- *cites:* FsCheck/CsCheck/Hedgehog docs; ploeh's example-to-property refactoring series.
- *meta:* pass=4 order=16

**`T2-PRP-02` — A property must be shown to fail on a seeded bug, like any other check.**
- *design:* n/a — proof step.
- *review:* a property test that has never been watched failing on a planted defect is unproven in the same way any other check is.
- *check:* argued, verified by execution.
- **severity:** blocker · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-01`
- *meta:* pass=8 order=30

**`T2-PRP-03` — Bound the generator to the domain; an unbounded generator tests the framework's arithmetic instead of the code.**
- *design:* constrain the generator to legal domain values, then add the true boundary as a separate example test.
- *review:* an unbounded generator that produces `int.MinValue` for a salary finds an overflow nobody will ever hit and hides the boundary that matters.
- *check:* does the generator produce values outside the domain's legal range (e.g. a negative or `int.MinValue` salary) with no explicit bound/filter? Evidence: read the generator definition — an unconstrained numeric/collection generator feeding a domain-typed input is the violation.
- *rationale:* weak evidence — widely-held practice, no locally-proven instance; caps at `major`.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-STR-02`
- *meta:* pass=1 order=8

**`T2-PRP-04` — On failure, capture the shrunk counterexample and commit it as a normal example test.**
- *design:* when a property fails, add the shrunk case to the example suite before moving on — do not rely on the generator to rediscover it.
- *review:* shrinking turns a sprawling random failure into a readable repro; the shrunk case is a regression test the next reader can understand without running the generator again.
- *check:* after a property test has failed at least once, is the shrunk counterexample committed as a standalone example test? Evidence: search the example-test file for the specific failing value the shrinker reported — its absence after a known failure is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-01`
- *cites:* framework docs (Hedgehog integrated shrinking; FsCheck separate shrinkers; CsCheck composed).
- *meta:* pass=8 order=30

**`T2-PRP-05` — Fix the seed and the case count in CI, and print both.**
- *design:* pin the seed for CI runs; print it and the case count on every run, not only on failure.
- *review:* a property test with a fresh random seed each CI run is a flaky test with good manners.
- *check:* `grep -rn 'Gen.Sample\|StartSize\|Arb.From\|seed' <property test config>` for a pinned seed, and does the CI run print the seed and case count on every run, not only on failure? Evidence: the printed run header.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-03`
- *meta:* pass=2 order=11
