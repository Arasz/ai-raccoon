---
name: review-tests
description: >-
  Use when tests that already exist have to be judged rather than written — "are these tests any
  good", "review the tests in this PR", "why did the suite stay green while that shipped", a
  coverage number nobody trusts, a gate nobody has watched fail, a lane that flakes, or a test
  file a reviewer flagged. Takes a directory, a file list, a diff, or "the tests for X"; refuses
  to run with no target. Returns findings, not edits.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [testing, test-quality, review, mutation, flakiness]
    related_skills: [design-tests, code-review-checklist, review-changes, task]
---

# review-tests

Green is the symptom, not the verdict. This skill judges tests that already exist against one
shared ruleset and hands back an improvement plan — it never writes a fix itself.

This is one ruleset used twice. It sits beside `design-tests` because every rule has a review
view and three have no design view — not because review owns it.

This skill and `design-tests` are one group, `SKILL_GROUPS["testing"]`, in `engine/badger_lib.py`
— naming either installs both, because the walk below cites the shared ruleset directly and has
nothing to read without it.

## Scope, or refuse

| Form | Resolution |
|---|---|
| a directory | every test file under it |
| an explicit file list | exactly the files given |
| a diff / PR / branch | `git diff --name-only <base>...HEAD` filtered to test files, **plus** the production files they cover — reviewing only the changed tests misses the case where production changed and no test did |
| "the tests for X" | resolve X to its symbol, then to the test files naming it; zero files found → say so, never review the nearest file instead |
| nothing named | **refuse.** Offer the three cheapest scopes: the current diff, the highest-risk untested area, or a named directory. A 4,500-test sweep with no scope produces a list nobody acts on. |

`git diff --name-only` on a squashed PR branch can return the whole branch, not the review scope
— check `git diff <base>...HEAD --stat` against the PR's own file list before trusting it.

## The walk

Cheapest-falsifier-first, nine passes, numbered 0–8. Each pass is cheaper to answer than the one
after it, so a pass that finds its worst case can make everything after it secondary — every pass
below states whether it does. Read `references/walk-review.md` **before** Pass 1 when the target
is larger than three files: it carries the full rule id per step; this file carries only the
question, the command, and the stop condition. After Pass 1, read the matching
`references/kind-*.md` **when** any test's kind is not a plain unit test. After Pass 2, read
`references/stack-dotnet.md` or `references/stack-ts-react-browser.md` **when** the target's
files belong to that stack.

Read `references/universal.md`'s field key **when** unsure which of three kinds a cited rule
is: `auto` — run the command, assert the finding, paste the output; `auto-unless-listed` — run the
command, then check the written exception before assigning severity; `argued` — quote the test,
name the falsifying edit, and for a blocker, run it. A `weak`-evidence rule is always `argued` and
never above `major`. Read `references/evidence.md` **when** citing a rule's proof, to quote the
actual failure it was proven against rather than the rule's title.

## Pass 0 — Can this suite claim anything?

Question: does the runner actually observe what it claims to assert, and what does the default
gate exclude? Command: run the project's own test command; record pass/fail counts and duration;
read the gate's include/exclude filter and check whether it is derived (e.g. from a directory
listing) or hand-typed; check whether a written "what the gate cannot see" statement exists and is
current. Stop condition: stops the review when an asserted property is invisible to the runner it
runs in — the headline claim is false and everything after it is secondary.

## Pass 1 — Can any test in it fail?

Question: for each test, read the name, predict the assertion, then read the body — can it fail
for the right reason? Does the arrange reach the branch under test? Was the asserted observable
produced by the act itself? Does every filtered/scanned/architecture check assert a non-zero
subject count first? Does a double standing in for a real backend share one contract suite with
it? Command: sweep for zero-assertion, `NotThrow`-only, always-true or commented-out bodies; grep
scoped checks for a non-zero-count guard; find every double claiming to stand in for a real
dependency and its contract suite. Stop condition: stops the review when a headline check turns
out to be vacuous — that finding outranks every coverage gap found in a later pass.

## Pass 2 — Determinism

Question: wall-clock assertions, sleeps used to synchronise, ambient clock or unseeded
randomness, unpinned culture/timezone, shared mutable state with no reset, fixed ports/paths/
db-names/volumes, ambient env or live credentials — and is "flaky" a measured, quarantined,
expiring state, or a retry budget? Command: `scripts/scan_uncontrolled_resources.py <path>`
**when** the project's stack ships it, else grep the same categories by hand; read the teardown
for every shared resource. Stop condition: none — record every hit and continue; a
non-deterministic suite can still hide real coverage gaps worth finding in a later pass.

## Pass 3 — Does the name match the body?

Question: read only the name, predict the assertions, then read the body — does every half of the
claim get proven? Name the one-line production edit that would redden the test; if the only edit
you can name is an intentional decision, it is a change detector, not a test. Any reflection into
privates, or a test-only seam on a production type? Command: name-then-predict per test, then diff
the prediction against the real assertions. Stop condition: none — grade a bad name against the
name, not against the suite's value, and keep walking.

## Pass 4 — Oracles

Question: where does the expected value come from — does deriving it touch the code under test?
Is the predicate pinned as tightly as the contract allows, with an exact count wherever the count
is the contract? Is every negative or absence assertion paired with a positive that proves the
code ran? Are exception assertions exact on type, with a negative test for every narrowed catch
filter? Command: trace each expected value to its source — hand-derived, spec, statute,
independent implementation, or (the forbidden one) the code under test itself. Stop condition:
none — an oracle finding invalidates that one test's verdict, not the walk.

## Pass 5 — Doubles

Question: does any double ignore a parameter a test varies, lose entries under fan-out, or model a
shape production cannot produce? Is anything mocked that this project doesn't own, mocked above
the boundary it should sit at, or mocked inside the domain layer? Do mock verifications assert an
outbound contract, or an inbound/incidental call that should be state instead? Command: read every
double's behaviour against the real dependency it stands in for; grep for mocks under the domain
tree. Stop condition: none — a double finding is a claim-about-production defect scoped to the
tests using it; record it and continue.

## Pass 6 — States, failures and wiring

Question: does a failure-path test assert the state of the thing that failed, or only a flag
raised beside it? Is every reachable state — surface and aggregate, including states reached only
by failure or by nobody answering — covered? Does every rule, filter or allowlist have a test
driving the real production input-building path? Is at least one secondary observable asserted?
Would dropping a registration or route constant fail anything? Command: enumerate the state/status
enum and grep for a test naming each value; enumerate rule/filter construction sites in production
and check whether every test builds inputs by hand instead. Stop condition: none — but a rule
nothing in production populates, or a state written by no production code, is a finding in its own
right, not a reason to stop reading.

## Pass 7 — Cost and hygiene

Question: does every test declare its size and honour the constraints that size implies? Is it one
act, arrange/act/assert visibly separated, with no control flow in the body? Does every skipped
test carry a reason, a tracking id and a re-enable condition? For each expensive test, what defect
does it catch that no cheaper test already catches? Command: read size declarations against actual
I/O; grep test bodies for control flow; list skipped tests and check each for a reason and an id.
Stop condition: none — hygiene findings are minor by default and never justify inflating a
review's severity.

## Pass 8 — the verdict

Question: for each new or changed test in the diff, was the red proven, with the mutation and its
observed failure in the record? Does any claim here rest on a changelog, an ADR, an agent's
report, an unapplied mutation, or an unmeasured number? Does each gate's failure actually block
something, with its blind spot written down? Command: `scripts/red_proof.py` **when** the target
names a red-proof edit, else a hand-applied mutation with both runs pasted. Stop condition: Pass 8
is the walk's own end — read `references/plan-format.md` **before** writing the verdict: every
finding carries a location or it is deleted; the summary's counts equal the enumerated rows; anything not run is
labelled `unverified (static reasoning)` in the row and again in the summary; lead with what the
suite does well. "0 blocker, 0 major, 1 minor" is a complete review — inflating severity to
justify the review is itself a finding.


## Stack-specific reading

Read `references/stack-dotnet.md` or `references/stack-ts-react-browser.md` **when** the target's
files belong to that stack, for the rule bodies a fragment below only points at.

### Runner notes

### Tooling pointers

## dotnet — run it, do not re-derive it

Read `references/stack-dotnet.md` **when** the target's files are C#/xUnit, for the .NET-specific
rule bodies parented off `universal.md`.

- Read the `dotnet-test:test-anti-patterns` skill **when** the `dotnet-test` plugin is installed,
  and run its Step 5 verdict instead of re-deriving one — Pass 8's calibration line is adapted
  from it verbatim.
- Read the `dotnet-test:detect-static-dependencies` skill **when** the plugin is installed and
  Pass 2 needs an untestable-static scan (`DateTime.Now`, `File.*`, `Environment.*`,
  `new Random()`) — run it before hand-grepping the same categories.
- Read the `dotnet-test:crap-score` skill **when** the plugin is installed and a Pass 7 "which
  expensive test is worth its cost" finding needs a complexity-vs-coverage number instead of a
  guess.
- Read the `dotnet-test:test-gap-analysis` skill's step 4b **when** the plugin is installed,
  before labelling anything `unverified (static reasoning)` — it is where that discipline comes
  from, and it may already have run the check you are about to skip.

**Blind spot none of the above patches:** these plugins mutate production expressions, so an
inert-rule or vacuous-gate finding (`references/archetypes.md`, A17/A18) still needs Pass 1's and
Pass 6's manual walk — no installed tool seeds that class of defect.

### Archetypes

Read `references/archetypes.md` **when** a candidate finding matches a named defect shape, to
cite its id beside the rule rather than describing the shape from scratch.

## Reference map

Every file under `references/` is loaded only when a pass or a finding points at it; none is read up front.

- `references/principles.md` — when a finding needs the principle (`T0-*`) behind it named.
- `references/universal.md` — when a finding cites a `T1-*` rule and you need its `check:` verbatim.
- `references/kind-unit.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-integration.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-contract.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-architecture.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-property.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-snapshot.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-performance.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-time.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-bdd.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-end-to-end.md` — only when pass 0 names the suite under review as that kind.
- `references/kind-accessibility.md` — only when pass 0 names the suite under review as that kind.
- `references/stack-dotnet.md` — when the project stack is dotnet.
- `references/stack-ts-react-browser.md` — when the project stack is react or ts in a browser.
- `references/stack-azure-functions.md` — when that stack is present; it is a stub and says so.
- `references/stack-cosmos.md` — when that stack is present; it is a stub and says so.
- `references/stack-node.md` — when that stack is present; it is a stub and says so.
- `references/stack-python.md` — when that stack is present; it is a stub and says so.
- `references/stack-terraform.md` — when that stack is present; it is a stub and says so.
- `references/archetypes.md` — when a finding matches a bug archetype and you need its proof mutation.
- `references/evidence.md` — when a reviewer asks why a rule exists; it holds the proven failures rules cite.
- `references/walk-review.md` — before pass 0, as the generated pass order.
- `references/walk-design.md` — only when the plan proposes a new test and you need the creation order.
- `references/plan-format.md` — before writing the improvement plan.
- `references/conflicts.md` — when two rules appear to contradict each other.
- `references/governance.md` — when a rule must be added, retired, or a stack extended.

## The improvement plan

One table, columns fixed. Read `references/plan-format.md` **before** writing the first row — it
states the seven columns, the four governing rules, and the `### WPn` work-package block that
`/task` consumes directly.

## Rationalizations

| Excuse | Reality |
|---|---|
| "The suite is green, nothing here is urgent" | Green is the symptom this review exists to explain. |
| "I can see that mutation survives by reading it" | Apply it, run it, revert it — or label the finding `unverified (static reasoning)` and say so in the summary too. |
| "The suite won't run in this worktree, so I'll infer the rest" | Every finding carries the unverified label, including in the summary, and the review says which command failed. |
| "I need more findings to justify the review" | "0 blockers, 0 major, 1 minor" is a complete review. Inflated severity is a finding against the reviewer. |
| "No scope given, I'll take the whole tests folder" | Refuse, and offer three scopes instead. |
| "The fix is obvious, I'll just apply it" | This skill returns findings. A fix applied inside a review has no gate and no red proof behind it. |

## Red flags — stop

About to report a mutation you did not apply. About to write "this test would fail" instead of
pasting the run. About to grade a suite you have not run and label nothing `unverified (static
reasoning)`. About to inflate a minor to a major because the review feels thin.

## What this skill is not

Not `code-review-checklist` (the whole diff, not just its tests). Not `design-tests` (writing new
tests, not judging existing ones). Not a fixer — every row in the plan is a finding for someone
else's work package, never an edit made in place.

## Gotchas

- A suite that "cannot be run here" is the normal case in a worktree without the full emulator
  stack — it changes the label on every finding to `unverified (static reasoning)`, never the
  decision to review.
- `git diff --name-only` on a squashed branch can silently widen the scope past the actual PR; see
  `## Scope, or refuse` above.
