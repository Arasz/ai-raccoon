# Search-quality follow-ups: three experts on ADR-0094's four open points

Date: 2026-09-03. Task: `air-enable-search-quality-all-kinds` (PR #596).
Source: the Consequences section of `docs/adr/0094-search-quality-records-every-kind.md`.

## Method, honestly stated

The brief asked for MoE: three experts, each given the four points, each proposing a fix with
argumentation, then a ruling per point. This project cannot spawn subagents (no `.pi/agents`
exists, so the `delegate` tool has nothing to read), so the panel ran in-session: three
viewpoints argued as hard as I could argue them, then ruled on. Treat this as a worked
starting position, not a reviewed verdict. If any point below turns into real work, run it
through `owner-gate-review` before scoping.

The panel:

- **A, the signal analyst.** Wants the richest telemetry that can be defended. Reads every gap
  as lost evidence. Accepts migration cost when ambiguity corrupts consumers.
- **B, the privacy engineer.** Wants the least data leaving the machine. Reads every stored
  byte in a syncing table as liability. Prefers local-only telemetry and record-on-use.
- **C, the compat keeper.** Wants zero-migration fixes. Reads every DDL change as risk and
  every convention as cheaper than a column. Prefers extensible JSON and documented
  conventions over schema work.

Rule of the exercise: general concepts only. No code, no migration scripts, no assignments.

---

## Point 1: code rows carry no file list, so file-level retrieval attribution is lost at write time

Problem (from the ADR): a pure code search records its hit count with an empty file list, by
design. Nothing in the row says which files were served. Follow-through recovers the files an
agent actually opened, which is partial.

- **A proposes hashed paths.** Store the top code paths as opaque content hashes, not
  plaintext. The argument: this restores file-level retrieval analytics (which files get
  served, which get graded) with no readable leak. A hash is still an identifier, but it only
  resolves on a machine holding the same corpus, which is exactly the machine allowed to see
  it. Cost is one new convention, no DDL if the existing JSON column carries the hashes.
- **B proposes keeping the rows empty and trusting follow-through.** The argument: retrieval
  lists have unproven value next to use signals. Nobody has shown a decision that
  file-at-retrieval-time would change and file-at-use-time would not. Every identifier stored
  in a syncing table is a promise to defend later. Follow-through already answers the only
  question tied to an outcome ("did the agent open it"), so the gap is theoretical until
  someone names the decision it blocks.
- **C proposes rank-aware follow-through inside the existing JSON.** The
  `follow_through_files` column is a JSON blob, which extends without a migration. Record the
  opened file together with the rank it was served at. The argument: use-attribution is the
  only attribution tied to outcomes, it costs no DDL, it leaks nothing at retrieval time, and
  rank-at-use subsumes most of what a retrieval-time list would tell (a file opened from rank
  9 reads differently from rank 1).

**Final: C now, A on probation.** Take rank-aware follow-through first. It captures the
attribution that matters (what was used, served at which rank) at zero migration cost and
zero new leak, and it composes with B's position rather than fighting it (rows stay
path-free at write time). Keep A's hashed paths as the named fallback, but demand the
missing evidence first: a concrete decision that retrieval-time file analytics would change.
**Why this one:** it is the only option that adds information without adding liability or
migration risk. B alone is accepted as far as it goes but leaves retrieval blindness
unanswered (grades without denominators mislead); A alone pays identifier cost before anyone
has shown the return.

---

## Point 2: `result_count` means different things by kind, and no `kind` column says which

Problem (from the ADR): the count holds memory hits except on pure code searches, where it
holds code hits, and the row carries no kind. Consumers must not compare counts blindly.
Correction to the ADR, found while writing this: the "hint" sentence overclaims. Both a code
row with hits and a memory row whose hits lack source files persist files as NULL (the
service nulls empty lists), so the rows are shape-identical today. There is no hint. The
follow-up note should assume zero distinguishability, not weak distinguishability.

- **A proposes the `kind` column through the normal migration ladder.** The argument:
  ambiguity in a consumed table corrupts every reader silently, and silent corruption
  compounds (a dashboard, then a tuning decision, then a promotion weight). Migrations are a
  solved problem here with a version ladder and backfill conventions. A column is the only
  honest schema, and everything else is a hint pretending to be one.
- **B proposes one unified definition instead of a discriminator: count all served rows
  across both sections on every kind.** The argument: comparable numbers with no migration
  and no new synced bytes. If every row counts the same thing, no consumer can compare
  blindly, because there is nothing left to confuse.
- **C proposes making the marker real without DDL: persist the empty file list literally**
  (an empty JSON array, distinct from NULL) for code rows, then document and test the
  NULL-vs-empty convention as the discriminator. The argument: near-zero cost, test-pinned,
  removes the ambiguity for all future rows. The cost is openly admitted: an implicit schema
  is uglier than a column, and it must be fenced with a test that breaks loudly if anyone
  "cleans it up."

**Final: A, the owner's pick over the panel's C.** Schedule the `kind` column migration
through the normal ladder, with backfill rules stated up front. **Why:** the panel's C-final
optimized for cheapness, but a marker convention permanent enough to rely on is a second
implicit schema maintained alongside the real one: every future reader pays the decoding tax,
and the column stays necessary anyway. If the honest repair is accepted regardless, the marker
buys a stopgap at the price of lasting ugliness. Do the column once, properly. B is rejected
as before (a unified count destroys exactly the per-leg meaning grades need: a grade-5 on 2
memory hits is not a grade-5 on 9 code hits, and no consumer recovers the difference). C falls
away with it: no marker, no convention, no pinning test for something the schema will state
outright. The ADR's hint correction stands regardless (rows are indistinguishable today),
which is now simply the state the migration fixes.

---

## Point 3: code query text syncs, as memory query text already does

Problem (from the ADR): the principled fix is stripping `search_quality` and `metrics` from
the sync snapshot (telemetry has no merge consumer; the merge reads entries and tombstones
only). Deferred as bigger than the telemetry fix. The record accepts the sync in exchange
for the signal.

- **A proposes accepting the sync permanently and investing instead in smarter snapshots
  later.** The argument: memory rows already sync arbitrary text, so code text adds volume in
  an accepted class, not a new class. Text joins (failure clustering, query-shape analysis)
  are the highest-value uses of this table, and every redaction proposal gives them up to
  protect against a leak whose harm nobody has specified beyond unease.
- **B proposes stripping both telemetry tables from the snapshot now.** The argument: the
  merge path demonstrably ignores these tables (entries plus tombstones are the whole merge),
  so stripping loses no function. Per-machine telemetry is already the reality (a pull never
  merges remote telemetry into local reads), which means the sync copies bytes for no
  consumer. One well-precedented snapshot change (the code-corpus DROP pattern shows how)
  removes the entire leak class, including the memory-text leak the ADR accepted too
  casually.
- **C proposes redacting only code-adjacent rows at write time** (blank or hash the query
  for code/both, keep memory text full). No migration (the query column stays NOT NULL), no
  sync change. The argument: bounded blast radius, keeps the memory signal rich, closes the
  new exposure this task introduced while leaving the old one for the principled fix.

**Final: B as the scheduled follow-up, C as stopgap only, A's permanence rejected.**
Schedule the snapshot strip as its own task: it converts an accepted leak into a removed
leak at small, precedented cost, and it upgrades privacy for the existing memory rows too,
not just the new code rows. Take C only if the strip stalls on review. Reject A's
permanence: "same class as accepted" was a fair argument for shipping the signal this week,
not a reason to stop. **Why B over C:** C bifurcates the table's semantics (text joins work
for memory rows, silently return nothing for code rows) while leaving the larger memory-text
exposure in place. B removes the cause instead of grading the symptoms.

---

## Point 4: the deferred bundle (`session_id`, the `kind` column, the sync strip, consumer review)

Problem (from the ADR): four items explicitly not addressed. `session_id` is NULL on all
1,534 rows because no caller passes one. The `kind` column needs a migration. The snapshot
strip is point 3. Grading and promotion scoring read this table but are not ruled here.

- **A proposes one telemetry-hardening task: `kind` column plus `session_id` backfill
  discipline together.** The argument: both are schema/contract truthfulness for the same
  table, both serve the same consumers, and splitting them pays the review and rollout cost
  twice for one coherent change.
- **B proposes privacy first: strip the snapshot, defer everything else.** The argument:
  order work by irreversibility. A leaked byte cannot be unshipped; an unattributed row can
  be re-graded later. Richness work on a leaking table builds on ground that should move.
- **C proposes the cheapest item alone: pass `session_id` from the callers.** No migration
  (the column exists and accepts NULLs), small diffs at the call sites, restores attribution
  for all future rows including the surviving memory ones. Leave the rest deferred.

**Final: a sequence, not a bundle (C, then B as in point 3, then A-minus-session
(`kind` column), then consumer review.** First the caller-side `session_id` (cheapest,
unblocks attribution going forward). Then the snapshot strip (removes the leak class before
more richness lands). Then the `kind` column migration (schema honesty, now on
non-leaking ground). Consumer review of grading and promotion scoring only after those
three, because tuning consumers on ambiguous, unattributed inputs is how the old skew
(140 grade-2s) gets baked into weights. **Why this order:** ascending cost and risk, and
each step is the precondition of the next. A's bundling is rejected on scheduling grounds,
not substance (reviewing a migration alongside caller plumbing invites either half to slip);
B's privacy-first instinct is honored by placing the strip second rather than last.

---

## Suggested task breakdown (my take, for the owner to accept or cut)

1. **Correct the ADR hint sentence** (done on the branch with this note): rows are
   indistinguishable by shape today, not weakly hinted.
2. **Caller-side `session_id`** (pass it from the search call sites; no migration).
3. **Strip telemetry from the sync snapshot**, extending the existing strip to the two telemetry tables following the code-corpus DROP precedent, with restore-open and merge-untouched verified.
4. **`kind` column migration** (the point-2 decision, owner's pick over the marker
   convention; backfill rules stated up front).
5. **Rank-aware follow-through JSON** (file plus served rank, no DDL).
6. **Consumer review last** (grading and promotion reads, only after 2–4 land).

Items 2–6 are each small enough to ship alone. None of them belongs smuggled into PR #596,
which stays a telemetry-restore change with privacy pins, nothing more.

---

## Item 6 closure — consumer review (P5 audit, 2026-09-03, lane `air-followup-p5-audit`)

Read-only audit plus two deliberately-scoped pins; no production behavior change
(`git diff --stat src/` empty at close). Every inventory below re-grepped at build time;
no line number inherited. Planned shapes judged against: required sessionId (P1),
stripped sync (P2), kind column (P3), ranked follow-through files (P4) — none landed in
this worktree (base `9aebeaa8`), so verdicts record current state + planned impact.

1. **GetMetricsAsync kind-blindness — confirmed, pooled semantics pinned.** SQL filters
   `created_at` + `project_id` only (`SqliteSearchQualityService.cs:120-150`); no `kind`
   column exists (`MemorySchema.cs:333-349`); result has no per-kind breakdown
   (`SearchQualityMetrics.cs`). Zero production callers (only def/interface/doc-ref under
   `src/`). Rationale recorded: pooled semantics deliberate; split only on a named new
   consumer (none). Pin: `GetMetrics_PooledSemantics_CountsAllRowsRegardlessOfScope`
   (`SearchQualityServiceTests.cs`) — red-proofed (`AND scope = 'all'` mutation:
   `TotalSearches should be 4 but was 1`).
2. **follow_through/RecordGrade projectId-forwarding gap — confirmed, accepted-out-of-scope
   + pinned (absence pin), stated never silent.** `QualityTools.cs:30-32` resolves canonical
   project for the gate but forwards no project to `RecordFollowThroughAsync` (signature has
   none, `ISearchQualityService.cs:43-45`); `RecordGradeAsync` takes projectId but binds only
   `Id/Grade/Note`, `WHERE correlation_id` only (`SqliteSearchQualityService.cs:102-116`).
   No live cross-project grade demonstrated (ids UNIQUE per `MemorySchema.cs:335`,
   unguessable envelope values; gate still enforces write access) — out of scope per the
   correlationId-only-keying ruling. Pin:
   `RecordGrade_CorrelationIdOnlyKeying_ProjectIdNotAPredicate` — red-proofed
   (`AND project_id = @ProjectId` mutation: `GradedSearches should be 1 but was 0`). Any
   future re-scoping trips it for deliberate revisit.
3. **Zero session_id readers — confirmed, acceptance recorded.** `session_id` occurs only in
   DDL (`MemorySchema.cs:339`) and the INSERT (`SqliteSearchQualityService.cs:52-63`); zero
   `SELECT … session` hits repo-wide; sole writer passes null (`Safe`,
   `SqliteSearchQualityService.cs:27`; dispatcher has no session source). P1 changes no
   consumer. No pin.
4. **Probe line struck — confirmed, no P4-compat work.** `QueryGuardRecallProbe.cs:53`
   selects only `query`; zero `servedRank` in `*.cs`; sole `follow_through_files` reader is
   the writer itself. No fixture busywork done. No pin.
5. **Grade fail-fast — NO behavior change (default stands).** No range guard in
   `QualityTools.cs:42-56` or the service; only the DB CHECK (`MemorySchema.cs:344`). The
   decider bar (tool-error-log CHECK-violation search) is unobservable from the bank — no
   such log infra in-repo — so bar-as-absent → no guard added. Owner note: the bar stays
   open; a future CHECK-violation sighting reopens the guard question as its own package.
6. **review-tests lens on both pins.** Pin 1 failure mode: silent dashboard narrowing;
   mutation above proves it trips. Pin 2 failure mode: silent re-scoping of grade keying;
   mutation above proves it trips. Fixtures non-degenerate (multi-row, mixed scopes,
   cross-project seeds); secondaries asserted (rates, average, coverage, totals).

Follow-up sketched, NOT implemented (needs its own package if ever wanted): post-P3,
extend the pooled pin with multi-kind seeds so a kind-filtered GetMetrics trips on kind
itself rather than via the scope proxy. AC: seed memory/code/both rows, assert pooled
totals; mutation kind-filtered GetMetrics fails; file: the same quality test file.
