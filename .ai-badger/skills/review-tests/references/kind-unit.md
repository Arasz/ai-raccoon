# kind-unit

Applies when the subject is one behaviour of one type, exercised with no real dependency at all.
The runner is whatever the stack's cheapest lane is (xUnit small-trait, Bun's native runner,
`vitest` unit project) — it observes only what runs in-process, so anything this kind cannot see
(layout, real SQL, a real clock) belongs to a different kind file instead of a wider mock. These
specialise `universal.md`'s SCO/CST/ORC/STR/DBL groups; read those bodies there, not restated here.

**`T2-UNIT-01` — A unit test touches no network, filesystem, database, real clock, ambient env, or thread it did not create.**
- *design:* if the body needs one of these, either seam it out or the test belongs in `kind-integration`.
- *review:* grep the small lane for I/O APIs; anything found either moves lane or gets a seam.
- *check:* auto — filesystem/network/container/clock APIs inside a test tagged small.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-01`
- *cites:* Google *Test Sizes*; F.I.R.S.T.
- *meta:* pass=7 order=28

**`T2-UNIT-02` — "Must mock everything" is a coupling finding about the production type, not a testing rule to work around.**
- *design:* if the arrange needs many collaborators stubbed, listen to the test before writing more doubles.
- *review:* count the doubles a single unit test needs; a high count each time is production coupling, not thoroughness.
- *check:* argued — read the constructor/dependency list against what the test actually exercises.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-DBL-03`
- *cites:* superpowers `test-driven-development` ("When Stuck").
- *meta:* pass=5 order=21

**`T2-UNIT-03` — Vary intersecting properties, not one axis at a time — bugs live at the combinations.**
- *design:* list the independent input properties the unit branches on; write at least one case that varies more than one together.
- *review:* count tests that vary more than one property at once. Zero is a finding.
- *check:* argued — enumerate branch conditions, check for a combined case.
- *rationale:* code that branches on present/absent × quoted/unquoted × first/last is untested at the grid's intersections by one-axis-at-a-time tests.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-STR-01`
- *cites:* ai-badger `personas/test-engineer.md` ("Property intersections").
- *meta:* pass=7 order=28

**`T2-UNIT-04` — Phase coverage by dependency layer: leaves unmocked, mid-layer with leaves faked, top layer last.**
- *design:* write leaf-type tests first with nothing faked; only fake a leaf once it has its own coverage.
- *review:* does the suite's build order test top-down, forcing mocks that later become the thing under test?
- *check:* argued — trace which layer's tests were written first against the dependency graph.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-CST-02`
- *cites:* ai-badger `personas/test-engineer.md`.
- *meta:* pass=7 order=29

**`T2-UNIT-05` — Many assertions is fine; many indistinguishable assertions is not.**
- *design:* give each assertion a message or a shape that names which one failed; never delete a real check to quiet the count.
- *review:* Assertion Roulette is a diagnosability defect — the fix is distinguishability, never fewer checks.
- *check:* argued — fail one assertion at a time, confirm the failure output names it.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-ORC-01`
- *cites:* ruling C6; dotnet-claude-kit `verify`.
- *meta:* pass=1 order=5

**`T2-UNIT-06` — Lane speed is a lane-level report, never a per-test assertion.**
- *design:* let the runner's slowest-tests report carry timing; do not encode a duration inside a test body.
- *review:* any per-test time budget in an assertion is `T1-ISO-01` wearing a unit-lane costume.
- *check:* auto — timing APIs inside an assertion call in the unit lane.
- **severity:** minor · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-01`
- *cites:* Beck *Test Desiderata*; Google *Test Sizes*.
- *meta:* pass=2 order=11

**`T2-UNIT-07` — Tighten a constraint deliberately (pool of 1, capacity of 1) to surface leaks and races in a test rather than in production.**
- *design:* where the default fixture is generous, add one case at the tightest legal value to force the leak into the open.
- *review:* this is the deliberate inverse of the no-degenerate-fixture rule — a small value chosen on purpose, not by accident.
- *check:* does at least one test set the constrained resource to its tightest legal value (pool size 1, capacity 1, retry budget 0), not only a generous default? Evidence: read the fixture/builder defaults across the suite — a suite with only generous values is the violation.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-STR-02`
- *cites:* Fowler *Eradicating Non-Determinism*.
- *meta:* pass=1 order=8
