---
name: design-tests
description: >-
  Use when tests have to be designed or written for a target — "write tests for X", "add coverage
  here", "what should I test", a new behaviour with no test yet, a bug that needs a reproduction
  test, a coverage gap someone wants closed, or a bare "write some tests" with nothing named. Works
  with a target given or none given. Not for judging tests that already exist (review-tests) and
  not for diagnosing one already-flaky failure (dotnet-flaky-test-diagnosis).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [testing, tdd, test-design, coverage, quality]
    related_skills: [review-tests, task, create-task-spec, code-review-checklist]
---

# design-tests

A test earns its place by failing under a plausible bug. This skill gets a suite designed and
written to that standard, target named or not — and it finishes **green**, not at the first red.

This is one ruleset used twice. It sits beside `review-tests` because every rule has a review view
and three have no design view — not because review owns it.


Read `../review-tests/references/principles.md` **before** Stage 1 and
`../review-tests/references/universal.md` **before** Stage 2 — both are short, and both apply to
every stage below regardless of what the target turns out to be.

## Two ways in

- **A target is named** (a file, a symbol, a behaviour, a bug report) → go straight to Stage 1.
- **No target is named** → Stage 0 works one out; do not guess.
- **The target is an *existing* suite** someone wants judged, not written → that is `review-tests`'
  job, not this one's — hand off there.

## Stage 0 — Work out the target

Numbered so each step is a command to run, not a judgment call; the agent runs it, never reasons
about it.

1. Uncommitted or unpushed production change? `git status --porcelain`; `git diff --stat`. Any
   non-test source file changed → target = the changed behaviours there. Stop — this is the common
   case and the cheapest one to check.
2. A bug, stack trace, incident, or failing report in the invocation? → target = a reproduction
   test for that defect, written before any fix. Stop.
3. A coverage artifact already on disk and newer than the last commit (`coverage*.json`,
   `lcov.info`, `coverage.cobertura.xml`)? → rank its uncovered files by step 5's bands below. Do
   not run a fresh coverage pass just to pick a target — step 4 answers the same question faster.
4. Which production files have no test file at all? `comm -23 <(sorted source stems) <(sorted
   test-file stems)` → the candidate set.
5. Rank the candidate set, stopping at the first non-empty band: (a) auth/identity, (b) money,
   compensation, pricing, quota, (c) an explicit state machine or status enum, (d) time, schedule,
   retry, expiry, (e) anything writing to a datastore or replayable, (f) an external I/O boundary,
   (g) everything else — pure functions last, they are the cheapest to add later.
6. Top band is a single unit → target = it. A tie (2+ candidates in the top band) → present the
   tie with its evidence and ask; never pick silently.
7. Candidate set empty — everything already has a test file? → this is not a design job. Say so
   ("every production file has a test file; whether those tests can fail is review-tests'
   question") and offer the handoff. Do not pad the suite to manufacture work.
8. Stop and say so when: no test project, runner or script exists anywhere (the target is the
   harness itself — ask); the repo is a monorepo and the diff spans packages with different
   runners (ask which); steps 1–5 all came back empty (report the commands run and their output,
   and ask for a target — a guessed target burns a full design cycle on the wrong unit).

**The target card.** Stage 0 ends by writing this; Stage 1 refuses to start without every field:

```
unit:        <file>::<symbol>
behaviours:  [<one phrase per behaviour>]
kind:        unit
runner:      <the exact scoped command>
isolation:   time=... network=... fs=... env=... random=... shared=...
oracle:      <where each expected value comes from — never "the code under test">
red-proof:   <the one-line production edit that must redden the first test>
```

`red-proof` empty means the behaviour list is not yet falsifiable — go back to Stage 2. `kind` is
`unit` unless the target's own shape says otherwise; read
`../review-tests/references/kind-unit.md` or the matching `kind-*.md` **when** `kind` is anything
other than plain `unit`.

## Stage 1 — The contract

State what the unit promises, in the caller's words, in three lines. Read the implementation for
its *surface* — signature, thrown types, collaborators — never for an expected value: if you have
already read the branch that computes the expected result, derive the expectation from the rule
instead, and say which rule.

## Stage 2 — The behaviour list

List every scenario before making any one concrete — the list comes from failure modes, not from
the code's shape. Beside each behaviour, name the one-line production edit that would break it; a
behaviour with no such edit is not a behaviour, it is coverage. Read
`../review-tests/references/archetypes.md` **before** finalising the list when the unit has a
boundary, a state transition, a clock, a retry, a replay, or two writes.

## Stage 3 — The oracle per behaviour

One of: `hand-derived`, `spec:<doc §>`, `statute/standard`, `independent-impl`,
`golden-from-production`, named before any test is written. The forbidden value is "computed by
the code under test, its mapper, or its builder" — this is a positive recipe, not a prohibition:
name the source rather than merely avoiding the mirror.

## Stage 4 — Runner, isolation and doubles plan

Pick the cheapest runner that can *observe* the behaviour — read the stack extension merged below
first; fall back to `../review-tests/references/stack-dotnet.md` **when** the target's files are
C#, or to `../review-tests/references/stack-ts-react-browser.md` **when** they are TypeScript or
React and the merged extension's short form is not enough. Declare five controls explicitly, each
`none` or a named mechanism: **time, network, filesystem, environment, randomness** — plus
**shared state** (statics, fixed ports, fixed db names, data volumes). `none` is an assertion, not
a default; Stage 6 checks it. Every double gets a one-line note of what it removes (time, network,
cost, nondeterminism) — "it was easier" is not an answer.

## dotnet: xUnit v3 runner
- **Harness:** xUnit v3 (`Xunit.v3`), assertions via **Shouldly**.
- **Scoped run:** `dotnet test --filter "FullyQualifiedName~<Type>"` — this resolves to zero tests
  and still exits 0, so Stage 5's pasted output must show a non-zero test count, not just exit 0.
- **Clock seam:** inject `TimeProvider`; tests substitute
  `Microsoft.Extensions.Time.Testing.FakeTimeProvider` — never `DateTime.Now`/`UtcNow` inside the
  unit under test.
- **The three-rung ladder**, cheapest first: pure unit (no host) → `WebApplicationFactory<T>`
  (in-process HTTP, real routing/DI) → Testcontainers (a real emulator, `[Trait("RequiresInfra",
  "true")]`-tagged). Push up a rung only when the cheaper one cannot observe the behaviour.
- Read `../review-tests/references/stack-dotnet.md` **before** writing the first test when the
  target touches Cosmos, HTTP, hosted services, or culture-sensitive parsing.

## Stage 5 — One test, watched failing, then green

- Write **one** test. Run it. **Paste the runner output**, counts included.
- New behaviour, no production code yet → the run failing *is* the RED. Confirm it fails for the
  right reason (behaviour missing, not a typo, not a build break).
- Production code already exists → RED cannot come from a missing implementation, so **prove the
  check fails**: run `scripts/red_proof.py --file <the file the red-proof edit targets> --line <N>
  --replace "<old>" --with "<new>" --run "<the scoped runner from the target card>"`, using the
  target card's `red-proof` line. Paste both runs it prints — mutated, then reverted. A test whose
  red-proof did not run is reported at Stage 7 as `unverified (static reasoning)`, never silently
  as done.
- Then make it green, then the next behaviour. Never a batch — batching is how a suite acquires
  five tests that all pass against the same wrong implementation.
- **The one branch that stops at RED.** When the invocation is explicitly TDD-inside-a-feature-
  change — no production code exists yet, and `test-engineer` or the stack engineer owns writing
  it next — stop after the failing run and hand off there. This is the exception, named here so it
  is never assumed: every other invocation of this skill finishes green, because a suite that
  stops at RED cannot be judged against a finished build, only against a promise that someone else
  will complete it.

## dotnet: red-proof command shape
- `--run` is the same `dotnet test --filter "FullyQualifiedName~<Type>.<Method>"` string the
  target card's `runner` line records — scope it to the one test, never the assembly, or the
  mutated run measures more than the behaviour under proof.
- A `FakeTimeProvider`-seamed test never blocks on real time; if `red_proof.py`'s mutated run
  hangs or times out, the seam is missing — that is a finding about the test, not the tool.

## Stage 6 — Self-review

Read `../review-tests/references/walk-review.md` **before** running Passes 0–2 of `review-tests`
(or the skill itself) against the files just written, plus `scripts/scan_uncontrolled_resources.py
--json` over them. Reconcile the two: every control declared `none` in Stage 4 must show zero
unmitigated hits in the scan output, and a control declared as a named mechanism must show as
`mitigated`. Fix findings against your own output here — do not carry them into the report.

## Stage 7 — Report

One table, no prose verdict:

| Behaviour | Test | Runner + duration | Red evidence | Oracle |
|---|---|---|---|---|
| DST transition keeps the slot | `NextAvailable_at_dst_transition_…` | `dotnet test --filter …`, 1.8 s | `red_proof.py` exit 0, output pasted | hand-derived |

Followed by: the Stage 6 reconciliation result, the count of behaviours with no red evidence, and
what was not tested and why. Durations are printed, never asserted — that is the one assertion
shape `scripts/scan_uncontrolled_resources.py`'s two wall-clock-assertion rows never let through,
mitigated context or not.

## Rationalizations

| Excuse | Reality |
|---|---|
| "It obviously fails; running it costs a minute" | You have not seen it fail. A typo reddens a test the same way a real defect does, and looks identical in a report. Paste the run. |
| "The production code already exists, so RED is impossible" | Then the RED is the mutation — that is what `red_proof.py` is for. Same minute. |
| "I'll mock the collaborator to keep it a unit test" | A double standing in for the unit under test tests the double. Move the boundary out, or change the kind. |
| "`getByTestId` is more stable" | Stable and wrong. No user can see a test id; a test that only finds one proves the DOM exists. |
| "The expected value comes from the mapper — same thing" | The mapper is inside the blast radius. Derive it from the rule and name the rule. |
| "Happy path first, edge cases as a follow-up" | The follow-up is where the suite's whole value was, and it is the thing that gets cut. |
| "No target was given, so I'll test the file I have open" | The open file is the least likely to be the highest-risk untested one. Stage 0 takes three commands. |
| "Coverage went 61% → 78%" | Coverage is exposure, not verification. Report what the tests kill, not what they touch. |
| "Five tests written, all green, done" | Five tests that have never failed are five unproven claims — run Stage 5's red-proof on each. |

## Red flags — stop

About to write a second test before running the first; about to report a mutation you did not
apply; about to write "this test would fail" instead of running `red_proof.py`; about to pick a
target without running Stage 0's commands; about to put a duration in an assertion; about to name a
test after the method instead of the behaviour; about to stop at RED outside Stage 5's one named
exception.

## Gotchas

- A `--filter`/`--namespace` scoped run that resolves to zero tests exits 0 and looks like success
  — Stage 5's pasted output must show a non-zero test count, not only a zero exit code.
- MSBuild emits Windows path separators that break a naive path comparison inside a scoped filter.
- `red_proof.py` refuses to run against a dirty target file, and leaves a
  `.design-tests/red-proof.journal.json` behind if it is ever killed mid-proof — gitignore
  `.design-tests/` in the target project.
- bun's fake timers do not intercept `Bun.sleep()`.

## What this skill is not

Not `review-tests` — that skill judges tests that already exist; this one writes them. Not a
flaky-test diagnosis skill — `dotnet-flaky-test-diagnosis` triages one already-failing case; this
one designs new coverage. Not a fixer of the production bugs a target card's `red-proof` line
reveals — those are reported, not silently patched, except where Stage 5's mutation is itself the
deliberate, reverted proof step.
