---
name: complete-project-scope-code-review
description: >-
  Use when the whole project — not a diff — is the review target and the result must survive being
  acted on: "review the entire codebase", "full quality review", "MoE review", "what is wrong with
  this project", "audit everything before the next release", or a review whose findings will
  become a plan someone implements. Runs ground-truth baseline, parallel expert lanes, integration,
  an adversarial pass that tries to falsify the findings, severity calibration against production
  reality, a reviewed plan, waved implementation in isolated worktrees, and a join review on every
  merge. Not for judging one diff or PR (that is review-changes plus code-review-checklist), one
  design document's gates (design-gate-audit), or one question (evidence-first-research).
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos]
scope: optIn
metadata:
  hermes:
    tags: [review, parallel, evidence, adversarial, planning]
    related_skills: [evidence-first-research, owner-gate-review, multi-lane-report-assembly, design-gate-audit, task]
---

# Complete project-scope code review

A review campaign, not a review. The output is a graded record, a reviewed plan, and merged work —
each stage gated by something that has been watched fail.

**This skill composes; it does not replace.** Each phase names the skill that owns its mechanics.
Load those rather than reimplementing them:

| Phase needs | Skill that owns it |
|---|---|
| Grade vocabulary, record shape, the `## Still open` discipline | `evidence-first-research` |
| Merging lane blocks into one record | `multi-lane-report-assembly` |
| Adversarially re-deriving a written record from its sources | `research-record-audit` |
| Tautology / fake-honesty hunting inside a lane | `code-review-evidence` |
| Mechanical per-diff gates inside a lane | `code-review-checklist` |
| Blast-radius ranking of a merge | `review-changes` |
| Verifying the diff base before judging it | `review-gate-diff-verification` |
| Auditing a plan's acceptance gates before anyone builds | `design-gate-audit` |
| Getting a human ruling per decision | `owner-gate-review` |
| Per-agent isolation | `worktree-agent-isolation` |
| Running one work package end to end | `task` (and `create-task-spec` when it needs a spec) |


## Phase 0 — Ground truth before anyone is dispatched

Measure, do not assume. Everything the lanes are told must be something you ran.

1. **Run the project's build, test and lint commands** from its config and record the exact
   numbers — counts, duration, warnings. A prior note saying "two flakes" is a claim about a
   different machine at a different commit; re-run it.
2. **Size the codebase**: production lines by layer, test lines, ratio. Write the number down
   before forming an opinion about it — a suspicion recorded as a hypothesis can be disconfirmed,
   and one held silently cannot.
3. **Verify every claim you plan to brief the lanes with**, at `path:line`. Plan documents, prior
   reviews and issue bodies go stale between writing and reading; paths move, counts drift,
   "N unused copies" is routinely 1 used copy.
4. **Name the base commit.** Every finding is against it. Lanes citing a moving ref cite nothing.

Brief the lanes with a labelled block — *"VERIFIED GROUND TRUTH (trust these over anything the
brief says)"* — so a lane knows which half of its input has been checked.

## Phase 1 — Parallel expert lanes

**Derive the lane roster from the repository, never from this list.** Read the project's own
stacks, personas and top-level directories, and open one lane per distinct expertise the code
actually demands. A fixed roster reviews the project you expected. A service with a separate
front end and its own infrastructure definitions needs different lanes from a single-runtime
library, and both need lanes nobody would have guessed without looking.

Lanes that recur: architecture/layering, domain algorithm (retrieval, scoring, whatever the
product's hard part is), the primary language's code quality, data access, test-suite QA, the
consumer-facing surface (CLI, API, UI), the product's **design** — flows, states, wording — and
operations/infrastructure.

The surface and the design are two lanes, not one. "Is this contract correct" and "is this good
to use" are different questions, and the second loses to the first whenever they share a lane.
Give the design lane a way to *run* the product, and read `references/lane-brief.md` before
dispatching it — a design lane briefed like a code lane returns opinions.

Each lane is **read-only**, gets **its own worktree and its own workspace id**, and is told:

- The verified ground truth, the base commit, and its own lens.
- **The lane contract:** one `### F<n> — <claim, present tense> [GRADE]` block per finding, grade
  from the closed set `MEASURED` / `READ` / `INFERRED` / `UNVERIFIED` at the end of the claim
  line, an `**Evidence:**` line carrying `path:line` for everything `READ` or `MEASURED`, a
  severity, a `## Still open` list, and its grade mix. This is `evidence-first-research`'s
  vocabulary; do not invent a second one.
- **Permission to disagree with the brief, in writing.** Say it: *"if a briefed finding is wrong,
  proving it wrong is a first-class result and worth more than confirming it."* The highest-value
  lane output on the session this skill comes from was a lane that proved a briefed feature had
  never worked at all. A brief that only invites confirmation gets confirmation.
- **Decision-ready owner questions**, one line each, so they can be routed without reformatting.

Run lanes concurrently up to your dispatch cap; where the cap bites, wave them and give the later
wave the integrated result of the earlier one to review rather than the same raw input.

## Phase 2 — Integration

Follow `multi-lane-report-assembly` for the mechanics — read it before assembling, because
renumbering, truncation at embedded `## ` headers and stale cross-references are where assembled
records break. On top of it:

- **Convergence raises confidence; lane count does not settle a fact.** Two lanes reaching the
  same finding independently is evidence. Two lanes disagreeing is not a vote — go read the code
  and settle it. The minority lane is right often enough that counting is not a method.
- **Re-verify at `path:line` every finding that drives expensive work** before it enters the
  record. Cheap findings can ride on their lane's grade; a finding that will cost a week cannot.
- Record what is **healthy**, explicitly, so a later simplification pass does not sweep it up.
- Record every **disconfirmed hypothesis** as plainly as the defects. "We suspected bloat; we
  measured it; the suspicion was wrong" is a finding.

## Phase 3 — Adversarial verification

Dispatch an independent reviewer **instructed to falsify the record**, with the sources but not
your reasoning. It re-derives every load-bearing claim, re-runs every `MEASURED` one, and checks
quotes verbatim at their cited lines — `research-record-audit` owns that procedure; read it when
briefing this pass.

Then publish what it changed, in the record, as a table: claim → refuted / corrected / softened /
reproduced. A reader must be able to see which way the errors ran. On the source session this
pass refuted or corrected six claims while every core conclusion survived — the failures were in
supporting numbers, which is exactly what gets quoted later.

**Attack before anyone implements.** A refuted number that reached a plan costs a work package.

## Phase 4 — Calibrate severity against production reality

A defect that is real in code and has never once fired in a deployment is still real — and it is
not a hotfix. Before ranking anything, query the live system read-only: does the table the defect
writes to have any rows? Has the flag it depends on ever been set? Is the feature reachable at all
in the shipped configuration?

Where the live system is a managed service rather than a file you can open, "read-only" is an
access decision with a cost — say which instrument you used (a direct query, telemetry, logs, a
staging replica) and grade the answer accordingly. An `UNVERIFIED` calibration is a legitimate
result; a calibration inferred from the code is not one at all.

State the result as *loaded, not fired* when that is what it is. This changes urgency and
sequencing; it must not change the finding. Two blockers on the source session had never fired in
production — which made the campaign a planned release rather than a hotfix, and surfaced a
sequencing constraint nobody had seen: *improving the broken filter's recall before the honest
write outcome landed would have converted a dormant defect into an active one.*

## Phase 5 — Plan, then review the plan

Package findings by **surface**, so everything touching one file lands in one change. Every
package carries acceptance criteria **and a gate that has been watched go red**.

Then put the plan through two independent reviews before implementation — an architect pass for
sequencing and blast radius, and an adversarial pass that attacks the plan's own claims and its
gates (`design-gate-audit` owns gate honesty; read it when auditing acceptance criteria). Fold
both into a revision, and **list what the revision changed** — a plan whose corrections are
invisible teaches nobody, and reviewers cannot tell a considered rejection from an overlooked one.

Sequencing rules worth writing down every time:

- **Name the serialisation points.** A file that five packages edit is not parallelisable, however
  independent the packages read.
- **The measurement chain runs backwards from what you want to prove.** If package C's ranking
  change must be measured on a corpus, and package A changes what the corpus contains, the order
  is A → corpus → C. Getting this backwards is easy and invisible until the numbers are useless.
- **Two packages that make each other worse in between ship as one change.** Deleting the last
  copy of some data in one package while another still fabricates success is strictly worse than
  the state you started in.

**Draw the module the plan changes, before and after.** Two diagrams in the repo's own diagram
convention: the current shape built from verified code facts, the proposed shape built from what
the review decided. A reviewer sees a restructuring in a picture that they will not see in a
finding table.

Route every question that needs a human through `owner-gate-review` — one decision, one ruling,
and generate its form programmatically from a decisions array rather than hand-editing the
template. Where no owner is available, decide, record the decision and the reason, and mark it
reversible.

## Phase 6 — Waved implementation

Each package runs through `task`, in its own worktree, TDD-first, with its named gate. Waves are
ordered by the sequencing rules; packages inside a wave run concurrently only where they share no
serialisation point.

Two hazards specific to running many lanes at once:

- **A lane holding unpushed work is a lane that has stalled invisibly.** Its branch looks absent
  from the integration side while its worktree holds everything. Require a push after every
  commit, and check for unpushed commits before concluding a lane produced nothing.
- **Every gate value that is a pin — a line count, a member count, a metric floor — carries a
  raise history on the constant.** A ratchet re-pinned silently is a ratchet that has been turned
  off. See the failure modes below.

## Phase 7 — Merge, and review every join

Defects that exist only where two individually-correct branches meet are the ones no lane can
find. On the source session at least five did. Verify the base first
(`review-gate-diff-verification`, read it before judging a merged diff), then on the **merged**
tree, not the branches:

- Re-run the build and the full suite. A per-branch green says nothing about the join.
- **Read what a mechanical conflict resolution dropped.** Taking one side wholesale silently
  removes anything the other side added that had no counterpart — a test, an assertion, a
  deliberately-changed constant. Diff each resolved file against *both* parents and account for
  every line that vanished.
- **Check that test infrastructure still does its job.** A helper that builds a pre-migration
  fixture stops being able to build it the moment another branch adds a constraint it does not
  know to drop; the test then fails in arrange, not assert.
- **Check the dispatch, not just the compile.** A method added on one side and a fake extended on
  the other can compile and still never be called. Your stack's extension names the concrete
  shape this takes.
- Write the integration reasoning into the merge commit. It is the only place that survives.

## Phase 8 — Record the decisions

Every conclusion that changes how the system is built becomes a decision record in whatever form
the project already keeps, and each one **names what it supersedes**. A campaign this size
routinely contradicts earlier decisions; a superseding record that does not say so leaves two
live answers.

Three rules learned by getting them wrong:

- **Separate what was measured from what was not, inside one record.** A decision that deletes
  two things — one measured to fail, one that never ran and therefore could not be evaluated —
  must not let the measurement carry both. Removing unreachable code is a maintenance argument,
  not an efficacy one, and a record that blurs them will be quoted as if it measured both.
- **Record refuted directions, not just the chosen one.** A reader who cannot see that an
  alternative was tested and lost will propose it again. Include your own rejected argument.
- **A decision resting on a circular benchmark can be overturned mid-implementation.** When that
  happens, supersede rather than amend, and say which measurement changed the verdict. Note where
  the deleted code is recoverable from.

## The failure modes this exists to catch

Each of these happened, most more than once. They are the reason for the phases above; read
`references/failure-modes.md` when you hit one, or before designing a benchmark or a gate.

1. **Circular benchmark.** A filter validated on a corpus built from the shape it matches scores
   perfectly by construction — and a later review reuses that rigged corpus to argue the opposite.
   **Control, required not advised: held-out evaluation by family.** Partition the corpus by the
   thing that generated it (tool family, source repo, operator, document type), train or tune on
   some families and evaluate on the held-out ones. A number that does not survive
   leave-one-family-out does not ship. An in-sample 0.946 AUC is a description of the corpus.
2. **Vacuous gate.** A metrics test reporting nDCG/MRR/recall of 0 for every query while asserting
   only "in range [0, 1]". A test comparing a column that is 0-of-2518 populated against a stale
   map: *always false equals always false*. Every gate is broken on purpose once and watched go
   red, before it is trusted.
3. **The specification encodes the defect.** A test or `.feature` file asserts the bug as required
   behaviour, so the fix turns it red and the reflex is to "restore" it. **Adjudicate, in the
   commit message**: is the assertion the contract, or a transcription of what the code did? Four
   separate cases on one session; every one was the second.
4. **Join defect.** See Phase 7.
5. **In-sample numbers.** Any metric produced on the data it was tuned on. Label it, and re-derive
   held-out before it justifies work.
6. **The finding that is a refutation.** The mechanism is usually silent: a parameter dropped
   because nothing matched it, a value written to a column that does not exist. A feature can pass
   review, an ADR, a benchmark and a full green suite while doing nothing. Treat a RED test that
   refuses to go red as evidence, not as a broken test.
7. **Ratchets re-pinned without a raise history**, and **lanes stalling with unpushed work**.
8. **Derive, don't pin.** Every expectation that mirrors something else — a tool list, a hash map,
   a set of statements, a fixture's contents — is derived from the source of truth at test time,
   or it is a second source of truth that drifts silently. A hand-maintained copy is a defect with
   a delay fuse.

## claude: which model runs which lane

The base skill's lanes split cleanly into two cost tiers, and the split is the largest lever in a
campaign this size.

- **High-reasoning lane (Opus).** Architecture, the domain-algorithm lane, the adversarial
  verification pass, the plan and both plan reviews, and arbitration when two lanes disagree about
  a fact. Anything whose value is in the reasoning rather than the reading. Dispatch with
  `model: "opus"` and prefix the call's `description` with `"Opus: "` so the lane is visible in
  the agent panel.
- **Survey lane (Sonnet).** Language-quality, data-access, QA and consumer-surface lanes; the
  implementation of an already-decided work package; test backfills against pre-derived
  expectations. Pass `model: "sonnet"` explicitly rather than relying on the session default.
- **Mechanical (Haiku).** Doc touch-ups, inventory greps, liveness checks.

Do not assume the orchestrating session is already the reasoning lane — the default model for new
sessions changes. Get it by dispatching with an explicit `model` override.

The seven-lane session this skill comes from ran three Opus lanes and four Sonnet lanes, plus an
Opus adversarial pass. The adversarial pass is the one to never economise on: it refuted or
corrected six claims that would otherwise have driven implementation.

## claude: isolation, and the hazards that travel with it

Every lane gets **its own worktree** and **its own workspace id** in any shared notes or memory
store. Use the Agent tool's own isolation rather than creating worktrees by hand — a manual step
before each dispatch is the one that gets skipped when the work feels urgent. Two things travel
with a new worktree and are easy to forget:

- **Arm any per-directory permission or auto-approval mode for the new path**, or the lane stalls
  waiting for an answer nobody is there to give. In a long autonomous campaign this looks exactly
  like a lane that found nothing.
- **The gate is re-run on the merged result.** Each lane's green measured a different tree.

**Push after every commit.** This is the concrete form of the base skill's stalled-lane hazard: a
lane's work exists only in its worktree until it is pushed, so the integrating side sees an absent
branch and concludes the lane produced nothing. It is also how work is lost when a draft PR is
squash-merged from outside the session.

**Two levels of dispatch, no deeper.** A lane may dispatch once; nothing below that. A tree that
widens without bound starves the work already running, and lane failures at depth three are
invisible.

## claude: reading a lane's real output

Subagent transcripts are written beside the session's, not inside it, at
`<transcript-dir>/<session-id>/subagents/agent-<id>.jsonl` with a paired `agent-<id>.meta.json`
naming `agentType`, `description`, `spawnDepth` and `model`. Read them when a lane's summary looks
truncated or when you need its actual model rather than the agent panel's — the panel's per-task
`model` field comes from an async live-status feed and can lag the transcript's `resolvedModel`.
The transcript is ground truth.

Judge a finished campaign by its **model mix** over the main transcript *and* its subagents
together, not by cache efficiency, which does not discriminate. A campaign whose dispatches are
mostly `general-purpose` is not routing to the project's personas whatever the config says.

**Keep always-loaded context byte-stable during the campaign.** Every lane's request prefix
includes `CLAUDE.md` and the project's state file; rewriting them mid-campaign turns every
subsequent lane's cache read into a fresh write. Update them between phases, not during one.

## claude: the review documents are the artefact

A campaign this long outlives its session. Write the integrated review, the plan and its revisions
into the project's docs tree as you go, and put the integration reasoning into merge commit
messages rather than into chat. The chat is not recoverable; the commits are. When the campaign
resumes in a new session — or in a `/compact`ed one — those files are what re-establishes context
in one read.

## dotnet: ground truth and lane tooling

**Phase 0 baseline.** Run `commands.build` and `commands.test` from config and record the exact
counts — a .NET suite reports `passed / failed / skipped` and a duration, and all four numbers
matter. **Record the skipped count separately and enumerate what is skipped**: a skip reports as
green, and a suite whose only real-data correlation check is skipped is measuring nothing there.
Note which trait/category filters CI uses and confirm they *partition* the suite — sum the filtered
counts and compare to the total. A test carrying no `Category` trait is invisible to a
`--filter Category=…` run while still counting in the total.

Also capture, once, at the base commit: warning count (`0 warnings` is a fact worth pinning),
project reference graph, and whether the domain project has any package references it should not.

**Lane accelerators.** Where these are installed, a lane gets more done per token:

- A Roslyn-backed navigator (find-references, dependency graph, DI registrations, type hierarchy,
  dead code, public API surface) — the architecture and code-quality lanes should use it before
  grepping.
- `dotnet-claude-kit:code-review` and `:health-check` — single-agent, diff- or project-scoped .NET
  reviews. They are good *lane implementations*; they are not a substitute for this workflow's
  ground-truth, adversarial and join phases.
- `dotnet-test:test-quality-auditor` and the `assertion-quality` / `test-smell-detection` skills —
  the QA lane's starting sweep. Treat their output as `INFERRED` until the lane re-checks the hits
  at `path:line`; a regex over `[Fact]` methods reports false positives (generic
  `Should.Throw<T>()` reads as assertion-free), and a lane that forwards the raw count without
  tracing every hit has filed a number, not a finding.
- MSBuild binlog tooling when the build itself is the suspect.

## dotnet: traps that produced real join defects

**Default interface members do not participate in derived-class dispatch.** Adding a member to an
interface as a DIM is the standard way to avoid touching every fake — and it is exactly what makes
this trap fire on merge. If `IStore.GetAsync` is a DIM and `FakeStore` implements `IStore`, a
plain (non-`override`) `GetAsync` on a class *derived from* `FakeStore` never participates in
interface dispatch: calls through the interface land on the DIM and silently return the default.
The suite compiles, the fake looks correct, and the test fails with "not found".

Fix: declare the base fake's member `virtual` with a comment naming the trap, and `override` it in
the derived fake. Check for this by grepping merged fakes for members that shadow rather than
override.

**A test fake that ignores its input.** A `FakeEmbedder` returning a fixed vector regardless of
argument makes a pair of tests pass with their inputs swapped. The test asserts arithmetic, not
behaviour. Before trusting any fake-backed gate, swap the inputs between the positive and negative
case and confirm exactly one fails.

**Silent parameter drop in micro-ORM parameter objects.** Dapper (and equivalents) ignore a
property with no matching `@placeholder` in the SQL. A whole feature can ship — with an ADR, a
benchmark and green tests — writing to a column that does not exist in the statement. Add a test
that reflects over every SQL constant and asserts each parameter-object property has a matching
placeholder; break it with a bogus parameter and watch it go red.

**Skips that report as passes.** An integration test that `return;`s when its backend is
unavailable is reported *passed*, not skipped. A broad `catch (Exception)` around the probe makes
"not provisioned" and "broken" indistinguishable. Gate the skip on the missing precondition and
use the framework's skip API.

**Allocation metrics read across `await`.** `GC.GetAllocatedBytesForCurrentThread()` is per-thread;
reading it either side of an `await` measures two different threads and can report a negative
allocation. A benchmark printing an impossible number is telling you its whole method is wrong.

**Nullable-optional injected dependencies.** `ILogger<T>? logger = null` on a DI-registered type
defeats the analyser rather than helping it; it usually appears as a workaround so existing
parameterless test constructions keep compiling. Route the tests through a shared test-data factory
instead. Optional *data* parameters (`string? workspaceId = null` meaning "not supplied") are a
different thing and are fine.

**Migration ladders are append-only.** Never renumber or delete a ladder step, even when the thing
it created is being removed — the step still runs on existing databases. Leave it as a historical
no-op and add a new step.

## dotnet: the deletion package

Whole-project reviews on .NET codebases routinely find that the highest-leverage change is
deletion. Two rules learned the hard way:

- **Green tests over unreachable code are why it looked maintained.** A subsystem with two
  dedicated test files, no DI registration and no caller is not covered — it is decorated. Delete
  the tests with the code, in the same change.
- **A deletion grep is an acceptance criterion, and it must be widened until it is honest.** One
  session's first grep would have passed with five of the intended deletions still present. Write
  the grep to name every type, interface and registration in the inventory, and run it as the gate.

## github: the campaign branch and its PR

A project-scope review produces one long-lived **campaign branch** off the default branch, with
each work package merged into it from its own lane branch. That shape — rather than one PR per
package straight to the default branch — is what makes the join review of Phase 7 possible: the
joins happen somewhere you control before anything reaches the trunk.

- Open the campaign PR as a **draft** as soon as the review document exists, so a human can watch
  it grow. Push after every commit.
- The version marker is bumped **once** for the campaign, not per wave, while nothing has been
  tagged. Maintain its release-notes entry as each wave merges — a wave that lands without
  amending the entry ships unrecorded under a number that claims to describe it.
- Never push to the default branch.

## github: integrating a base that moves under you

The default branch will move during a campaign this long. That is the single most productive
source of join defects, and it needs treating as a merge to review rather than a chore.

**Before judging any lane finding against a moved base:** re-fetch, and diff the reviewed head
against the merged commit. A squash-merge landing during the review can contain changes the lanes
never saw — the author may have pushed reworks between your dispatch and the merge — so a finding
citing `path:line` may cite a shape that no longer exists. Re-verify each surviving finding against
the merged file before accepting it.

**Resolving conflicts where the trunk refactored what your branch changed.** The recurring shape is
"main restructured the helper while this branch changed the behaviour underneath it". Take the
trunk's structure and splice your behaviour into it — but a wholesale take costs two things that
the build will not catch:

1. **Tests with no counterpart on the other side are dropped silently.** Enumerate them and restore
   them explicitly.
2. **Deliberately-changed constants are reverted.** An assertion your branch changed on purpose —
   an exit code, a threshold, a name — goes back to the trunk's value and nothing fails. Re-apply
   it with the reason on the line.

Diff each resolved file against **both** parents afterwards and account for every line that
disappeared. Then run the affected suites, not just the build.

## github: review rounds and merge

If the project uses an automated reviewer, run the campaign PR through it once per wave rather than
once at the end — a 40-file wave gets a useful review, a 400-file campaign does not. Triage a whole
review batch together before implementing, since findings interact, and verify each finding still
applies to the branch head before acting on it: review snapshots lag the commit they are tagged
against.

Reply on every thread you addressed, deferred to a filed issue, or determined stale, and resolve
it. A resolved-in-code thread left open is indistinguishable from an ignored one.

Squash-merge the campaign once a review round returns with no new findings since the last pushed
commit and the merged-tree gates are green.

## github: workflow findings the review should file

A project-scope review is the right moment to check the CI definition itself, because nothing else
ever does:

- Every third-party action pinned to a full commit SHA, never a tag.
- The workflow's test filters **partition** the suite — sum them and compare to the total run
  count. A test matching no filter is a test CI does not run, and it will look covered.
- Least-privilege `permissions` declared at the workflow or job level.

## Gotchas

- **The base moves under you.** If the trunk merges during the review, the squashed result can
  differ from the head every lane read. Re-fetch, diff the reviewed head against the merged
  commit, re-run the gates on the merged state, and re-verify each finding against the merged file
  before accepting it.
- **Never weaken a gate to make a merge green.** Six retrieval failures on the source session were
  genuine ranking movement on a denser corpus; they were left failing and routed to the package
  that owned them. A gate lowered to pass is a gate deleted.
- **Characterization tests keep CI honest, not quiet.** When a finding cannot be fixed in this
  wave, pin the *current* behaviour with a test that names it as characterized-not-endorsed. It
  keeps the suite green without hiding the finding.
- **An attribution can be wrong while the finding is right.** A test blamed on one defect that
  still fails after that defect is fixed was mis-attributed — re-diagnose it rather than reopening
  the fix.
- **A refuted supporting number does not refute the conclusion.** Withdraw the number, keep the
  claim if its other legs hold, and say which leg was removed.
- **Taste is not a defect until you have looked for the ruling.** A design finding must name where
  it checked for a prior decision, because the ruling that made a choice deliberate lives in a
  plan or design document and not in the code. The design lane's highest-severity finding on the
  source campaign was refuted exactly there: an approved default, filed as a defect by a lane that
  had not opened the document approving it. The lesson is not that design lanes are unreliable —
  the adversarial pass corrected or refuted well under half of that lane's findings and the rest
  stood — it is that this particular check is load-bearing for a design lane and optional for a
  code one.

## Verification checklist

- [ ] Build/test/lint baseline measured at a named base commit, not quoted from a note
- [ ] Lane roster derived from this repository's own stacks and directories
- [ ] Every lane got the ground-truth block, the grade contract, and explicit permission to refute
- [ ] Adversarial pass ran, and its refutations are published in the record
- [ ] Every design finding says whether it saw the running product, and where it looked for a
      prior ruling before calling a choice wrong
- [ ] Severity calibrated against the live system; "loaded, not fired" stated where true
- [ ] Every work package has a gate that has been watched go red
- [ ] Every held-out claim is held-out; no in-sample number justifies a package
- [ ] Every merge re-ran the gates on the merged tree and accounted for lines a resolution dropped
- [ ] Every re-pinned ratchet carries its raise history
- [ ] `## Still open` is non-empty, or its emptiness is defended

## References

- `references/failure-modes.md` — worked cases for all eight, with the evidence and the control
  each one needs; read it before designing a benchmark, a corpus or a gate, or when a finding
  turns out to be a refutation.
- `references/lane-brief.md` — the dispatch brief template and the lane output contract; read it
  before dispatching the first lane.
- `references/owner-gate-form-lifecycle.md` — how a decision form gets generated, opened and
  ingested without losing a late question; read it when Phase 5 routes questions to an owner.
