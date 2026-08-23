# L1 — Universal (40)

The always-loaded review/design spine. Stack- and kind-independent — every project that installs
`review-tests` or `design-tests` loads this file on every invocation. Distilled from this
ruleset's own research; see `governance.md` (the rule-adding bar) and `evidence.md` (the failures behind each `evidence: strong` rule).

**Id scheme.** `T<layer>-<GROUP>-<nn>`. Groups: `SCO` scope & intent · `ORC` oracles & assertions ·
`ISO` isolation & determinism · `DBL` doubles · `STR` structure · `CST` cost, placement &
visibility · `PRF` proof. Numbers are allocated once and never reused or renumbered; retirement
leaves a tombstone (`governance.md` §"Retiring a rule").

**Field key.** `design:` what to do when writing · `review:` what to look for · `check:` the
falsifier · `flag:` `auto` (a command decides, report it) / `auto-unless-listed` (a command
decides, then check the documented exception) / `argued` (quote the code and name the falsifying
edit) · `absorbs:` source rule ids folded in · `cites:` framework invariant or external authority
this rule defers to rather than restates · the machine-read line (`pass`/`order` place this rule
in `walk-review.md`; `phase` places it in `walk-design.md`, `review-only` meaning a triage or
governance rule with no design action — see `governance.md`).

---

## Group SCO — Scope & intent (7)
**`T1-SCO-01` — Name the one-line production edit that reddens this test; if the only edit you can name is an intentional decision, it is a change detector, not a test.**
- *design:* before writing the body, name the edit. Cannot → you are writing decoration.
- *review:* per assertion, name the edit. `expect(MAX_RETRIES).toBe(5)` fails this: the only edit is a decision.
- *check:* argued, with a grep prior — assertions whose expected side references the same constant, resource, or literal production reads. Worst-case shape: asserting a prompt string *contains* an instruction like `"NEVER invent"`, which guards against deleting the instruction, not against disobeying it.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-01`, `U-SCO-05`, `U-ASR-08`, L2 `U4`
- *cites:* invariant `prove-the-check-fails`; TotT *Change-Detector Tests Considered Harmful*; `evidence.md` (cv-truthfulness-gate-design)
- *meta:* pass=3 order=14 phase=7
**`T1-SCO-02` — The test's name is a claim; the body proves every half of it.**
- *design:* `unit_condition_expectedOutcome`. If the name has two halves, assert both or split.
- *review:* read only the name, predict the assertions, then read the body. Divergence → grade the *name* false, not the test weak.
- *check:* auto-unless-listed — grep names for `_and_|And[A-Z]|Without|, and `, count asserted halves per hit; then a sampled name-vs-body read.
- **severity:** blocker when the name asserts the opposite of the truth; major when a claimed half is unasserted; minor when merely vague · **evidence:** strong · **flag:** argued
- *absorbs:* `U-STR-02`, `U-SCO-03`, L2 `U3`
- *cites:* `T0-03`; `evidence.md` (incomplete-state-transitions) (`Failure_parks_awaiting_user_...` asserted the flag beside the thing; nothing was parked)
- *meta:* pass=3 order=13 phase=6
**`T1-SCO-03` — Test observable behaviour through the surface a caller uses; no reflection into privates, and no test-only seam on a production type.**
- *design:* if you need a private, the seam is missing — extract the collaborator instead.
- *review:* both directions of the same coupling defect: the test reaching in, and production reaching out to help it.
- *check:* auto — `GetMethod|GetField|BindingFlags|setAccessible|as any|__dict__` in tests; `internal` setters, `ForTesting`, `#if DEBUG`, reset methods with no production caller in source.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-SCO-02`, `U-STR-10`, L2 `U7`, L2 `U22`
- *cites:* Beck *Test Desiderata* (structure-insensitive); TotT *Prefer Testing Public APIs*
- *meta:* pass=3 order=15 phase=1
**`T1-SCO-04` — A failure-path test asserts the state of the thing that failed, not only the flag raised beside it.**
- *design:* after asserting the error, assert where the aggregate now is and what it can do next.
- *review:* every `*_fails_*` / `*_error_*` / `*_rejects_*` test — state, or only a returned flag / log line / exception type?
- *check:* auto-unless-listed — enumerate failure-path tests by name; read their assertions.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-06`
- *cites:* `evidence.md` (incomplete-state-transitions) — 4 instances in one sweep, all green; `PracticeSessionStatus.Failed` was written by no production code and its only occurrence was a test asserting the enum *contains* it
- *meta:* pass=6 order=23 phase=2
**`T1-SCO-05` — Every reachable state has a test: empty, loading, partial, error, success, and the states reached only by failure or by a human not answering.**
- *design:* enumerate the states of the surface *and* of the aggregate before writing the first test. Both are states, not accidents of the data.
- *review:* enumerate the status/state enum and the surface's states; for each, find the test that puts the system there and asserts what is allowed from there.
- *check:* auto-unless-listed — per state value, grep for a test naming it. A state value written by no production code is itself a finding.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-10`, `F-NET-08`, `F-QRY-03`'s per-state requirement, `F-NET-09`
- *cites:* invariant `design-every-reachable-state`; `evidence.md` (parked-is-not-in-flight) (`AwaitingUser` keeps `status: "analyzing"` forever, so every "don't act while busy" guard excluded exactly the records needing recovery)
- *meta:* pass=6 order=24 phase=2
**`T1-SCO-06` — Every rule, filter, allowlist or predicate has at least one test driving the real production input-building path.**
- *design:* one test constructs the input the way production does — not by hand.
- *review:* grep production for the rule type's construction sites; if every test builds inputs by hand, the wiring is untested. Mutation-check by *unwiring the input*, not by breaking the rule.
- *check:* auto-unless-listed — grep production for the rule/predicate type's construction sites; for the rule under review, does at least one test call that exact construction path rather than `new RuleType(...)`/a hand-built instance? A rule type never constructed outside test code is the exception this flag allows.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-07`
- *cites:* `evidence.md` (check-the-inputs-not-just-the-rule) — `AtsDomainRule` admitted **0 of 22** real messages; `BlockedSenderRule`'s input collection was populated by nothing, anywhere
- *meta:* pass=6 order=25 phase=6
**`T1-SCO-07` — Assert at least one secondary observable: a neighbouring field, an emitted event, a counter, related state.**
- *design:* a function can return the right thing and still be wrong. Widen the behaviour radius by one.
- *review:* count assertions touching something other than the return value; near-zero across a suite means shallow regardless of coverage.
- *check:* per test, does at least one assertion touch something other than the primary return value — a neighbouring field, an emitted event, a counter? Evidence: count non-return-value assertions across the suite; a suite near zero is the violation regardless of its coverage percentage.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-09`
- *cites:* ai-badger `personas/test-engineer.md`
- *meta:* pass=6 order=26 phase=3
---

## Group ORC — Oracles & assertions (7)
**`T1-ORC-01` — Every test asserts something that can distinguish right from wrong.**
- *design:* if the only outcome is "nothing threw", you have not written a test yet.
- *review:* four shapes, one finding class — (a) zero assertions; (b) `Should.NotThrow` / `assertDoesNotThrow` / `not.toThrow` as the *sole* assertion; (c) tautological or always-true (`AreEqual(x, x)`, `IsTrue(true)`); (d) a commented-out assertion, which still reports as coverage.
- *check:* auto — `Should.NotThrow|Assert.DoesNotThrow|assertDoesNotThrow|not\.toThrow`, `Assert.IsTrue(true)|expect(true)`, `// *Assert|# *assert`, and a zero-assertion-body count.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ASR-01`, `U-ASR-02`, `U-ASR-04`, `U-STR-11`
- *cites:* `evidence.md` (test-suite-shape) — **48 sites** where `Should.NotThrow` is the sole assertion; `LLM_request_schema_is_valid_json_schema` is satisfied by replacing the whole schema with `"{}"`; `code-review-checklist` Phase 3.1
- *meta:* pass=1 order=5 phase=6
**`T1-ORC-02` — The expected value is derived independently of the code under test.**
- *design:* literals, hand-checked fixtures, a statute, a published table, a reference implementation. Table-driven `want` literals are the preferred shape.
- *review:* trace the expected side of every assertion; if it calls into the SUT, its builder or its mapper, the assertion is true regardless of behaviour.
- *check:* argued (a mirror assertion is a data-flow property, not a grep).
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-ASR-03`
- *cites:* `T0-04`; superpowers's `writing-good-tests` skill reference; an in-repo statute-derived-before-consulting-the-engine oracle example
- *meta:* pass=4 order=16 phase=3
**`T1-ORC-03` — Assert the tightest predicate the contract allows: no slack a plausible regression fits through, and an exact count wherever the count is the contract.**
- *design:* pin the bound at the production value, not one significant figure away. Where a check must admit legitimate variation, **partition by class rather than widen a tolerance**.
- *review:* for each `>`/`<`/`>=` compute the slack against the production value; for each call assertion ask whether two calls would also pass.
- *check:* auto-unless-listed — `ShouldBeGreaterThan|toBeGreaterThan|<|>` in assertions, plus `toHaveBeenCalled()`/`Received()` with no count.
- **severity:** major; **blocker** where the loose bound guards a safety or privacy property · **evidence:** strong · **flag:** argued
- *absorbs:* `U-ASR-05`, `U-ASR-10` (count clause), `U-PRF-09`, `F-INT-09`, L2 `T7`
- *cites:* `evidence.md` (loose-assertion-bound-admits-regression)
- *meta:* pass=4 order=17 phase=3
**`T1-ORC-04` — A negative or absence assertion is paired with a positive that proves the code ran.**
- *design:* "correctly shared nothing" and "did nothing at all" must produce different results.
- *review:* `ShouldBeEmpty` / `toHaveLength(0)` / `ShouldBeNull` as a sole assertion — a no-op implementation passes.
- *check:* auto — grep those forms, require a companion invocation counter or state check.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ASR-06`
- *cites:* ai-badger `dotnet-hosted-service-testing/SKILL.md`; L2 `T10` (assert time-gated behaviour does *not* happen before its trigger, and does after)
- *meta:* pass=4 order=18 phase=3
**`T1-ORC-05` — Exception assertions name the exact type and the discriminating detail; every narrowed production catch filter gets a negative test.**
- *design:* for each `catch (X) when (...)`, write the test that throws a *different* instance of X through the same path and asserts it propagates.
- *review:* bare base-type throws assertions; assert-in-catch (`catch (Exception ex) { Assert.Fail(...) }`) which converts "wrong exception type" into a generic failure.
- *check:* auto — `Throws<Exception>|raises(Exception)|toThrow(Error)` with no matcher; `Assert.Fail(` inside a catch.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ASR-07`
- *cites:* `evidence.md` (broad-catch-swallows-real-bugs); `code-review-checklist` Phase 3.1 (no assert-in-catch)
- *meta:* pass=4 order=19 phase=3
**`T1-ORC-06` — Prove the arrange actually reaches the branch under test.**
- *design:* assert the precondition that makes the competing branches inapplicable, or delete the branch and watch the test redden.
- *review:* the highest-yield question in the walk, and the one that most often turns a green test into a finding.
- *check:* argued, verified by deletion (delete the branch, re-run the named test).
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-ASR-11`
- *cites:* `evidence.md` (arrange-does-not-reach-branch-under-test)
- *meta:* pass=1 order=6 phase=7
**`T1-ORC-07` — The asserted observable must have been produced by the act.**
- *design:* wait on, and assert, a value only the awaited work can produce — a sibling field, a changed label, a captured request body.
- *review:* four shapes, one defect — (a) the assertion's collection is written by the test's own callback or mock; (b) the `waitFor`/`findBy` predicate is already satisfied by `defaultValues` / initial state, so it passes on the first tick; (c) the observable is read *before* the act; (d) the assertion re-reads what the arrange set.
- *check:* auto-unless-listed — trace the writer of every asserted collection; diff every wait predicate against the component's initialiser; read AAA ordering literally.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-ASR-13`, `U-ASR-14`, `U-ASR-12`, `F-INT-04`
- *cites:* `evidence.md` (vacuous-test-trap) — every "3 tabs visible ✓" reading was captured *before* scrolling; `Mid_batch_failure_preserves_already_persisted_signals` asserted against a list its own mock callback appended to, so making persistence transactional leaves it green
- *meta:* pass=1 order=7 phase=6
---

## Group ISO — Isolation & determinism (9)
**`T1-ISO-01` — Never assert on wall-clock time: no millisecond budget, no p95, no ratio of one duration to another. A real budget lives in a dedicated lane, excluded from the default run.**
- *design:* assert what the time was a proxy for — a fake clock advanced per phase, a count of rows touched, `total_changes() == 0`, a result type reporting "no work done". Timings may be **printed**, never asserted.
- *review:* every hit is a finding, regardless of test kind.
- *check:* auto — `Stopwatch|Elapsed|TotalMilliseconds|performance.now|Date.now()|p95|percentile` inside an assertion call.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-01`, `U-CST-05`, `F-TIME-01`, `F-NET-10`'s rationale
- *cites:* `evidence.md` (no-wall-clock-test-assertions) — owner ruling verbatim: *"time based tests — this is huge anti pattern"*, *"i do not care — they should not fail on any env"*. Two gates green in CI went red on a workstation at load average 35. Perf budgets: see `T2-PERF-*`.
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-02` — Never sleep to synchronise. Poll a condition with a timeout, gate on a latch the test controls, or sync on a signal the system itself emits.**
- *design:* the best sync point is the SUT's own emitted event or log line — it doubles as the "the work is registered" guarantee. For "while in flight" assertions, use a latch the test resolves, never a delay.
- *review:* a fixed delay races in both directions: too short on a loaded box, and long enough that the test passes *because* the thing under test never ran.
- *check:* auto-unless-listed — `Thread.Sleep|Task.Delay|time.sleep|setTimeout|Start-Sleep|waitForTimeout|delay(|networkidle`. The one documented carve-out is a third-party rate limit, and even then the sleep is paired with a condition check.
- **severity:** blocker (unit/integration/e2e); major (documented external rate limit) · **evidence:** strong · **flag:** auto-unless-listed
- *absorbs:* `U-ISO-02`, `K-INT-04`, `F-E2E-02`, `F-NET-10`, L2 `U14`, L2 `U15` (a test's own timeout throws a distinguishable type, never the assertion's)
- *cites:* dotnet-test `test-smell-detection` — severity "does **not** drop because the test is an integration test" (ruling C4)
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-03` — Non-determinism enters through an injected, pinned source: an injected clock for time, a seeded generator for randomness — and the instant and the seed are printed on failure.**
- *design:* the code under test never reads the ambient clock or an unseeded generator. Time-dependent behaviour is driven by a fake clock in the runner that owns one.
- *review:* an unseeded generator makes a failure unreproducible; an ambient clock makes it unrepeatable.
- *check:* auto — production: `DateTime.Now|DateTime.UtcNow|Date.now()|datetime.now()|time.Now()`; tests: `new Random()|Math.random|random.random|rand.Int` with no seed.
- **severity:** major; **blocker** where the ambient read is inside the assertion's path · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-03`, `U-ISO-04`, `K-PRP-05`, `F-TIME-02`, L2 `U12`, L2 `T1`, L2 `T2`
- *cites:* dotnet-test `detect-static-dependencies` — **run it, do not re-derive the static list**; boundary cases (midnight, DST, leap year) are `T2-TIME-*`
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-04` — Culture, timezone and locale are pinned wherever the test parses, formats, sorts or compares text, dates or money — and at least one case runs under a non-default one.**
- *design:* pin invariant in production; run one test under a comma-decimal culture and one under a non-UTC timezone. Pinning invariant *everywhere* proves nothing about whether production depends on the host.
- *review:* a suite green on `en-US`/UTC and red on `pl-PL`/`America/Los_Angeles` is a production bug found late.
- *check:* auto — `DateTime.Parse|decimal.Parse|ToString(|toLocaleDateString|Intl.DateTimeFormat` with no explicit provider; then check whether the suite contains *any* non-default-culture or non-UTC case at all.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-05`, `F-TIME-04`, L2 `T4`
- *cites:* archetypes `A03`, `A04`
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-05` — Tests pass in any order and alone. No shared mutable state without an unconditional reset.**
- *design:* a test that cannot be parallel-safe is explicitly serialized and the reason is recorded at the serialization point. Rebuild starting state from scratch in preference to relying on teardown — a teardown failure is easy to swallow silently. Anything a test *arms* (fake timers, global stubs, storage, env) is restored in a shared, unconditional teardown, not in the happy path.
- *review:* an armed fake timer that leaks does not fail cleanly — it stalls every timer-driven update in that worker until each later test times out, which reads exactly like machine contention.
- *check:* auto — run shuffled and run each failure alone; grep mutable `static` fields, module globals, singletons, step-definition class state; confirm `useRealTimers`/`restoreAllMocks`/`localStorage.clear` are in the *global* `afterEach`.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-06`, `U-ISO-08`, `F-ENV-05`, `F-TIME-03`, `F-E2E-04`, L2 `U13`, L2 `R4`, L2 `T5`
- *cites:* Beck *Isolated*; F.I.R.S.T. *Independent*; Fowler *Eradicating Non-Determinism*; `.../frontend/app/test/setup.ts` fake-timer leak guard (proven by minimal-pair repro)
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-06` — Every shared external resource is namespaced per run or per worker and destroyed on teardown: port, path, database, container, volume, queue, task hub, orchestration instance.**
- *design:* never a fixed literal. Ask: what happens if two runs overlap?
- *review:* leaked state turns a clean run into a false failure and, worse, a dirty run into a false pass.
- *check:* auto — hardcoded ports, fixed container/database/volume names, `WithDataVolume()` with no ephemeral flag; then `docker volume ls` / temp-dir listing after a run, and confirm every created resource has a `finally` / `IAsyncLifetime` / `t.Cleanup` counterpart.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-07`, `U-ISO-10`, `K-INT-05`
- *cites:* `evidence.md` (one-apphost-at-a-time) (two AppHosts on one host-global volume: the second emulator's `pg_ctl start` self-terminated the first's PostgreSQL while the gateway kept answering, so every read 500'd and pointed at an innocent PR); `evidence.md` (azurite-volume-corpse) (a wedged `Pending` instance in a persistent volume 409'd forever, reproducibly, "even alone on fresh infra" — 64 orphaned volumes by 2026-07-31)
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-07` — No test reads ambient environment: no hardcoded absolute paths, no inherited env vars, no host network, no real credentials, no live third-party.**
- *design:* hermetic. Child processes get an explicit environment, not an inherited one. Backend and third-party calls are stubbed at the transport boundary.
- *review:* an ambient `PATH` entry — a real CLI that happens to be installed on this box — silently changes what the test exercises.
- *check:* auto — `C:\\|/tmp/|/Users/|getenv|process.env|Environment.GetEnvironmentVariable` in tests.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-ISO-13`, `F-E2E-05`'s general form
- *cites:* testsmells.org "Mystery Guest"; Google *Test Sizes* (hermetic)
- *meta:* pass=2 order=11 phase=4
**`T1-ISO-08` — "Flaky" is a measured, quarantined, expiring state — never a retry budget.**
- *design:* define it precisely (*a test that both passes and fails against the same source*), measure the suite-wide rate with an action threshold, quarantine rather than delete or silently ignore, and bound the stay (days to weeks, not indefinitely).
- *review:* a spec that only passes on retry is a defect filed with an id, not an accepted cost. Retries buy a trace, not a disposition.
- *check:* auto-unless-listed — retry/repeat configuration; a quarantine list with entry dates; a flakiness metric anywhere.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* L2 `U16`, `U17`, `R7`, `F-E2E-06`'s retry clause
- *cites:* Fowler *Eradicating Non-Determinism*; Google Testing Blog (~16% of >4M suites flaky)
- *meta:* pass=2 order=12 phase=review-only
**`T1-ISO-09` — Before calling a red run a regression, record the failing *fraction* and reproduce on the unmodified baseline.**
- *design:* n/a — this is a triage rule, and it is in L1 because it decides whether any other finding is real.
- *review:* contention degrades gradually (8/179, 11/179); a broken build fails almost everything (178/179). Both produce the identical "element not found" signature. Raising the per-test timeout once settles it — an assertion mismatch survives it, a timeout does not. Then read the host/dev-server output, not the test output. In a multi-session repo `origin/main` itself can be red; use a throwaway worktree at the base commit.
- *check:* the diagnosis record names the failing fraction, the raised-timeout re-run, and the baseline commit.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* `U-ISO-11`, `U-ISO-12`, `F-ENV-07`
- *cites:* `evidence.md` (load-vs-broken-build) — misdiagnosed twice in opposite directions in one day, at a cost of hours
- *meta:* pass=0 order=2 phase=review-only
---

## Group DBL — Test doubles (4)
**`T1-DBL-01` — A double standing in for a real backend is bound by one contract suite executed against both, and a meta-test fails when a port has no such binding.**
- *design:* for each port with more than one implementation, write the contract once and run it against the fake and the real thing.
- *review:* this is `T0-05` made checkable, and the rule that catches the most expensive class of false green.
- *check:* auto — for each port interface, grep for a shared abstract contract suite; assert the meta-test exists.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-DBL-04`, `K-CTR-03`, `K-CTR-04`, `F-NET-05`, L2 `U18`
- *cites:* `evidence.md` (fake-contradicts-production-backend)
- *meta:* pass=1 order=10 phase=5
**`T1-DBL-02` — A double's behaviour is a claim about production: it honours every parameter the real thing honours, is concurrency-safe when the SUT fans out, and is never degenerate.**
- *design:* a fake with real behaviour (tenancy, optimistic concurrency, dedupe indexing) catches handler bugs a stub cannot. Promote duplicated stubs into one shared chassis.
- *review:* an ignored parameter makes every test that varies it vacuous; an unsynchronized recording list loses entries under fan-out (symptom: a count assertion that passes ~2/3 alone and fails under full-suite load — never fix it by serializing the production scheduler); a fake whose index is never updated makes a second pass re-share, so `count == 2` documents behaviour production cannot produce.
- *check:* auto-unless-listed — diff each fake method's parameter list against the parameters it actually reads; check collections used under `Task.Run`/`Parallel.*`/`Promise.all` are lock-guarded or concurrent types.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-DBL-05`, `U-ISO-09`, `U-DBL-07`, `U-ASR-10`'s fake-artifact clause
- *cites:* `evidence.md` (double-ignores-parameter); Class A of `dotnet-flaky-test-diagnosis`
- *meta:* pass=5 order=20 phase=5
**`T1-DBL-03` — Stub at a boundary you own, at the lowest level that removes the cost, and never in the pure-domain layer.**
- *design:* wrap the third-party type and fake the wrapper; stub at the network/transport boundary, not at your own API module. Name what each double removes — time, network, cost, nondeterminism. "It was easier" is not an answer.
- *review:* a mock of a library you do not control encodes your *belief* about it and freezes that belief through every upgrade. A double placed too high swallows a side effect a later assertion silently depends on. A mock under the domain tree is a layering bug, not a testing one.
- *check:* auto — mock constructions naming external namespaces/packages; `vi.mock("@/api/...")` / `stubGlobal("fetch")`; mocking-library imports under the domain test tree.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-DBL-01`, `U-DBL-03`, `U-DBL-08`, `F-NET-01`
- *cites:* TotT *Don't Mock Types You Don't Own*; GOOS; invariant `clean-architecture-layering`; MSW best practices.
- *meta:* pass=5 order=21 phase=5
**`T1-DBL-04` — A mock earns an assertion only when the call is itself the observable contract; never assert call sequences or counts as a proxy for an outcome.**
- *design:* outbound side effects that *are* the contract (an email sent, a message published, a payment charged, an audit record written) are legitimately asserted on the double — there is no other observable. Inbound/incidental calls (`SaveChangesAsync`, a repository read) are asserted as state.
- *review:* "more mock setup lines than test logic" is the smell; `InSequence`/`Received.InOrder` where an observable outcome exists pins an implementation.
- *check:* auto-unless-listed — for each `Verify`/`Received`/`toHaveBeenCalled`, ask whether that call is published contract.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* `U-DBL-02`, `U-DBL-06`, L2 `U11`
- *cites:* ruling C2. For T1-ORC-01's purposes a verification still counts as an assertion — a verify-only test is shallow, not empty.
- *meta:* pass=5 order=22 phase=5
---

## Group STR — Structure (3)
**`T1-STR-01` — One act, visibly separated arrange/act/assert, no control flow in the body except idiomatic parametrization, and the varied input readable at the call site.**
- *design:* DAMP inside the body, DRY for the machinery (fixtures, builders, container setup). Parametrize three or more bodies differing by one literal; keep genuinely distinct boundary cases separate. Name magic values. Initialise in setup only what most tests in scope use.
- *review:* control flow means some assertion paths may never execute. Explicitly **not** smells: table-driven sub-test loops, `[Theory]`/`@parametrize`/`test.each`, a `foreach` whose whole body is the assertion.
- *check:* auto-unless-listed — `if |switch |\? :` in test bodies, classified against the exception list; more than one SUT-mutating call; setup fields fewer than half the tests reference.
- **severity:** minor · **evidence:** strong · **flag:** auto-unless-listed
- *absorbs:* `U-STR-01`, `U-STR-03`, `U-STR-04`, `U-STR-05`, `U-STR-06`, `U-STR-08`'s readability clause, L2 `U2`, `U4`, `U5`, `U6`, `U8`, `U9`
- *cites:* TotT *DAMP not DRY*; testsmells.org (Conditional Test Logic, General Fixture); ruling C3
- *meta:* pass=7 order=28 phase=6
**`T1-STR-02` — No degenerate fixture: the parameter under test is at a value where the mechanism can be observed at all.**
- *design:* scrollback with more than zero scrollback; eviction with a capacity above one; retries with a budget above zero; ordering with more than one element; a builder whose defaults are valid *and* non-degenerate, with at least one test overriding them toward empty/null.
- *review:* a degenerate fixture makes the test fail for the wrong reason *and* stay green when the parameter stops working. A builder with degenerate defaults silently disarms every test that uses it; a builder always fully populated hides the empty-input path entirely.
- *check:* argued — per test, is the parameter under test observable at this value? Then read the builder's defaults.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-STR-07`, `U-STR-08`
- *cites:* `evidence.md` (degenerate-fixture-hides-untested-path)
- *meta:* pass=1 order=8 phase=6
**`T1-STR-03` — A skipped test carries a reason, a tracking id and a re-enable condition; its code is unverified until it runs.**
- *design:* re-enabling is a required step of the change that lifts the block, not a follow-up.
- *review:* a skip is not low-severity clutter — it is untested code wearing a test's clothes. Bindings written while a scenario was ignored have **never executed** and are frequently subtly broken.
- *check:* auto — `Skip =|\[Ignore\]|@disabled|it\.skip|t\.Skip|todo(`; require a reason string and an issue reference.
- **severity:** minor as clutter; **major** at the moment of un-ignoring · **evidence:** strong · **flag:** auto
- *absorbs:* `U-STR-09`, `F-ENV-06`
- *cites:* ai-badger `dotnet-bdd-testing/SKILL.md` — three real traps in one un-ignore session, including a step silently searching the wrong bucket from a wrong positional argument; ruling C11
- *meta:* pass=7 order=28 phase=review-only
---

## Group CST — Cost, placement & visibility (6)
**`T1-CST-01` — Every test declares its size, and the size's constraints are enforced.**
- *design:* Google's measurable taxonomy, not opinions — **small**: no network, no database, no filesystem, no sleep, no thread it did not create; **medium**: localhost only; **large**: anything. "Unit test" is an opinion; "no network access" is a fact.
- *review:* map the suite's traits/categories onto the three sizes, then check the small lane actually contains no I/O.
- *check:* auto — grep the small lane for filesystem/network/container APIs.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `K-UNI-01`'s enforcement clause, L2 `U1` (decomposed; "FIRST" as a mnemonic is cut — see `governance.md`)
- *cites:* Google *Test Sizes*; *SE at Google* ch. 11
- *meta:* pass=7 order=28 phase=4
**`T1-CST-02` — Every test names the defect it catches that no cheaper and no existing test catches.**
- *design:* push each test to the cheapest level that can observe its defect, and add exactly one level above for wiring. Write the integration test first to pin the behaviour end to end, then push the branch coverage down.
- *review:* three shapes — (a) the answer is "the library author's bug" (delete or relabel as a named characterization test); (b) the answer duplicates a cheaper test (two tests that always redden together are one test); (c) an expensive test asserting what the fake already asserted has bought a container for nothing.
- *check:* for the test under review, name the defect it alone catches. Evidence: delete the test — does any other test in the suite redden? If not, or if the only failure it could catch is a third-party library's own bug, it is the violation.
- **severity:** minor · **evidence:** strong · **flag:** argued
- *absorbs:* `U-SCO-04`, `U-CST-02`, `U-CST-06`, `K-INT-03`, `F-E2E-07`, L2 `R8`
- *cites:* Vocke *Practical Test Pyramid*; ruling C1
- *meta:* pass=7 order=29 phase=1
**`T1-CST-03` — The default gate's exclusions are derived, not hand-typed, and the count delta proves it.**
- *design:* enforce the trait/tag's spelling and placement with a meta-guard so a mis-spelled or mis-placed marker fails the build rather than silently excluding a test.
- *review:* a hand-maintained list of what the gate skips drifts the moment someone adds to one side. Run the default gate and the full gate; the delta must equal the enumerated exclusions.
- *check:* auto — the count delta, plus the existence of the meta-guard.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-CST-03`, `K-INT-07`'s scheduling clause, L2 `P7`
- *cites:* invariant `derive-or-delete-the-list`; `evidence.md` (test-suite-shape) — `[RequiresInfra]` excludes ≈170 facts / 30 classes / 12 files, pinned by three reflection meta-guards
- *meta:* pass=0 order=3 phase=8
**`T1-CST-04` — What the gate cannot see is named in writing: coverage per risk area, production types with no test at all, and each gate's blind spot.**
- *design:* ship the blind-spot sentence *with* the gate. Report coverage per layer/area with the weakest area named in the summary, never as a single average.
- *review:* a number without that list is a comfort blanket. Gap analysis that only grades existing tests cannot see the type nobody tested.
- *check:* auto-unless-listed — does a current, dated document name the uncovered risk areas? Enumerate production types/handlers, diff against test-file names, list the residue.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* `U-CST-04`, `U-CST-08`, `U-PRF-05`, `U-PRF-08`, L2 `U10`, `K-INT-06`
- *cites:* `evidence.md` (test-suite-shape) — 76.1% overall concealed Cosmos+Blobs at 21.9% and auth middleware at **0% over 19 lines**, the only code resolving the identity header, with all 106 endpoints anonymous and depending on it. Ranking: `dotnet-test:crap-score` — **run it**. Coverage *thresholds* are refused: see `conflicts.md` C21.
- *meta:* pass=0 order=4 phase=8
**`T1-CST-05` — The runner must be able to observe the property asserted.**
- *design:* pick the cheapest runner that can *see* the behaviour, using the stack's runner decision table. Where the cheap runner is structurally blind, the test moves up, and the choice is recorded.
- *review:* the failure mode is a green result that certifies nothing. Three proven instances of one shape: a DOM shim with no layout engine (`getBoundingClientRect` returns all-zero regardless of applied styles; CSS `content` does not exist); an in-memory database provider that hides real SQL, transactions and constraints; an emulator whose divergences (indexing, RU accounting, consistency, auth modes, error codes) go unrecorded.
- *check:* auto — geometry/computed-style APIs in a shim-run suite; `UseInMemoryDatabase` and equivalents; a narrowed scanner (`withRules([...])`) with no unnarrowed run anywhere.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `F-ENV-01`, `F-ENV-02`, `F-ENV-03`, `F-QRY-04`, `K-INT-01`, `K-INT-06`'s emulator clause, `N-DATA-01` parent
- *cites:* `T0-08`; `evidence.md` (frontend-happy-dom-blindspot) — the suite stayed green through a dialog rendering 46px wide, a submit button 623px below the fold, and a button announcing "left bracket save right bracket"; MS Learn EF testing-without-the-database (InMemory *strongly discouraged*)
- *meta:* pass=0 order=1 phase=4
**`T1-CST-06` — Wiring and registration have a smoke test: dropping a registration must fail something.**
- *design:* one assertion per registered background service / middleware / handler / route constant.
- *review:* if `AddHostedService<T>()` is dropped, the service silently never runs and every other test still passes. Same shape for a route literal a backend rename should have broken.
- *check:* auto — one DI/registration smoke test per registered component; a `--check` mode on every generated file.
- **severity:** major · **evidence:** strong · **flag:** auto
- *absorbs:* `U-CST-07`, `F-NET-06`'s general form
- *cites:* ai-badger `dotnet-hosted-service-testing/SKILL.md`; invariant `derive-or-delete-the-list`
- *meta:* pass=6 order=27 phase=7
---

## Group PRF — Proof (4)
**`T1-PRF-01` — Red-proof every new or changed check against the exact expression it targets, and record the mutation and the observed failure.**
- *design:* break the expression, confirm the edit landed (grep the file — an editor hook can silently revert a stream-edited patch), watch it redden, restore, watch it green.
- *review:* the PR/record states which expression was mutated and what the failure said. Green is the default state of a test that does not reach anything.
- *check:* argued, verified by execution.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-PRF-01`, `K-ARC-01`, `K-PRP-02`
- *cites:* invariant `prove-the-check-fails` (**this rule adds only the evidence-record requirement; it does not restate the invariant**); `T0-01`
- *meta:* pass=8 order=30 phase=7
**`T1-PRF-02` — Every claim about the suite is executed, not reasoned.**
- *design:* n/a — a review-conduct rule.
- *review:* three shapes — (a) a reported mutation survivor that was never applied and re-run (label it `unverified (static reasoning)` or do not report it: a false gap costs more than a missed one, because it sends someone to write a redundant test); (b) a gap declared closed from a commit title, changelog, ADR, PR name or an agent's report (where a doc and the code disagree, the code wins; an ADR written mid-task from your own prompt is not independent verification of that prompt); (c) a quantitative claim quoted as current when it was carried from a dated measurement. When a mutation *survives*, identify which layer the assertion actually reaches before calling the test worthless — and record the answer, because it names exactly what the guard does not cover.
- *check:* the completion claim names the command that was run and its observed output.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *absorbs:* `U-PRF-02`, `U-PRF-03`, `U-PRF-06`, `U-PRF-10`
- *cites:* invariants `check-sources-not-yourself`, `measure-when-it-pays`; `evidence.md` (verify-behavior-not-changelog); `dotnet-test:test-gap-analysis` step 4b — **its apply/run/revert discipline is the mechanism; cite it, do not restate it**
- *meta:* pass=8 order=31 phase=7
**`T1-PRF-03` — A filtered or scoped check asserts a non-zero subject count before it can pass.**
- *design:* every filtered gate's first act is a count assertion.
- *review:* one shape across four tools — a mutation run scoped to a namespace that expands one directory deep and measures **zero mutants** while exiting successfully-looking; an architecture rule whose matcher filters to an empty set with no zero-match diagnostic; a lint scope matching no files; a source-scan audit reporting "0 drift" while inspecting zero files.
- *check:* auto — every filtered gate, scan, or architecture rule is preceded by a non-zero subject assertion.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *absorbs:* `U-PRF-04`, `K-ARC-02`, `F-SNAP-05`'s zero-match clause, `N-MUT-02` parent
- *cites:* `evidence.md` (stryker-blind-spots); `evidence.md` (archunitnet-rules-pass-vacuously); invariant `derive-or-delete-the-list`
- *meta:* pass=1 order=9 phase=7
**`T1-PRF-04` — A gate whose failure blocks nothing is documentation; say so or wire it in.**
- *design:* name, before building it, what a failure of this gate stops.
- *review:* trace whether a failure fails a build, a merge, or nothing. Advisory output counts as documentation, not as a guarantee.
- *check:* does this gate's non-zero exit appear in a CI job, pre-commit hook, or branch-protection required check — anywhere its failure actually blocks a build or merge? Evidence: `grep -rn '<gate command>' .github/workflows/ .pre-commit-config.yaml` — no hit means the gate is documentation, not a guarantee.
- **severity:** major · **evidence:** strong · **flag:** argued
- *absorbs:* `U-PRF-07`
- *cites:* invariant `proof-of-done`; `evidence.md` (cv-truthfulness-gate-design) — a truthfulness checker was left out of a quality report whose own prompt calls it "advisory — it never blocks anything"
- *meta:* pass=8 order=32 phase=7
