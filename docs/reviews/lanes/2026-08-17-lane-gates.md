# Lane: Acceptance Gates and Test Honesty

Plan reviewed: `docs/plans/2026-08-17-issue-close-357-367.md` (1002 lines), base `a2a48b3e`.
Method: read the plan in full, then read the real test/production files the plan cites or
depends on (`HeldOutRetrievalGateTests.cs`, `ParityGateTests.cs`, `ReciprocalRankFusionTests.cs`,
`SourceAffinityRankerTests.cs`/`.cs`, `AdrIndexTests.cs`, `MemorySchemaDdlStatementCountTests.cs`,
`MemorySchemaVersionTests.cs`, `ChunkingCorpusGuaranteeTests.cs`, `MemorySql.cs`,
`MemorySchema.cs`, `ChunkBackfill.cs`, `SyncService.cs`, `MemorySearchResult.cs`,
`ChunkingDefaults.cs`) to check every quoted line number, pinned number, and claimed test name
against what is actually on disk. No build, no test run, no edits.

---

## Findings

### 1. [BLOCKER] WP6 C4 — the headline claim of the fusion package has no stated RED perturbation

Section: WP6 §Gates and §RED (lines 678–700).

C4 (`FusionConfidenceTests.RankOneOnOneLeg_AndLowOnTheOther_IsNotBuriedByFusion`) is the one gate
that proves the feature does what WP6 exists to do — rescue #367's shape. Every other gate in the
C-series has a named falsifying perturbation in the "RED" paragraph (C1: apply unconditionally: C5:
substitute `Count+1`; C6/C7/C8: remove each guard; C9: drop the correlation id). C2, C3, C4 and
C10 are not mentioned at all. C2/C3 are mechanical enough that the missing perturbation is a minor
gap (obvious: write the metric anyway; flip the default). **C4 is not** — it is exactly the kind of
assertion the project has been burned by before (ADR-0056's `ShouldBeInRange(0,1)`): a test that
constructs a favourable fixture and checks the favourable output, with no stated proof that an
unfaithful implementation of `max(rrf, best_single_leg_normalized)` would fail it.

Concretely, a broken implementation that computes the floor but never applies it (returns the
baseline RRF order unchanged) would make C4 pass if the fixture's baseline RRF order already
happens to rank the target reasonably — the test's own docstring purpose ("the shape is rescued")
only holds if the fixture is built so that RRF alone leaves the target buried and the floor alone
lifts it. That fixture design isn't specified, and the plan never states the perturbation that
should be applied and watched fail.

**Fix:** name C4's RED explicitly — e.g. "compute `rrf` only, without the `max(...)` floor, on a
fixture where one leg ranks the target #1 and RRF alone leaves it beyond rank 5; C4 must fail
before the floor is applied and pass after." Require the PR to record that run, the same way C1's
red run is required to be recorded.

### 2. [BLOCKER] WP3 — no gate distinguishes "delete scoped to source_file" from "delete scoped to ctx only"

Section: WP3 §Gates and §RED (lines 383–406).

The plan names two opposed pairs for the reconciliation delete and is right that they're needed:
B1 vs B2/B3 (metadata survives — catches wholesale replace-all) and B1 vs B5/B6 (predicate-drop to
`WHERE project_id = @projectId` — catches a project-wide wipe). But the actual `DELETE` this
package writes is scoped by **both** `ctx` and `source_file` (fact #5's own partition key). There
is a plausible, narrower bug between the two perturbations named: drop `AND source_file =
@sourceFile` but keep the `ctx` predicate. That deletes every row belonging to **every other
source file in the same context** the moment any one file in that context is re-ingested.

None of B1–B10 catches this:
- B1 is scoped to the file under test — it doesn't look at a sibling file's rows.
- B5/B6 test gone-file and sourceless rows, not a second live, valid, untouched source file.
- B7 (`Reingest_DoesNotCrossBucketBoundaries`) tests cross-context isolation (project A vs project
  B / workspace vs shared), not cross-source-file isolation within one context — its own name says
  "bucket," which this codebase uses for scope/context, not source file.
- B9 tests position re-stamping, not deletion scope.

This is exactly the "property intersection" a single-axis test suite misses: (ctx match) ∧
(source_file match) needs a case where ctx matches but source_file doesn't, with two live files
in the same context, only one re-ingested.

**Fix:** add `FileIngestorReconciliationTests.Reingest_DoesNotTouchAnotherSourceFileInTheSameContext`
— seed two source files under the same `(ctx)`, re-ingest one, assert the other's row set and
metadata are byte-for-byte unchanged. RED: drop `AND source_file = @sourceFile` from the delete
predicate while keeping `ctx`; this test goes red while B1, B5, B6, B7 all stay green.

### 3. [BLOCKER] WP2's repair job may never fire on any real (pre-existing) bank — the trigger condition itself is untested

Section: WP2 §The repair job for existing banks and §Gates (lines 291–339).

`source_state` is a brand-new `CREATE TABLE IF NOT EXISTS` (fact #11) with **no backfill step**
for banks that already have entries with `source_file` set — nothing in WP2's description (it has
no Files table, unlike every other WP) populates a `source_state` row for a pre-existing source.
The maintenance job's trigger condition is stated as: *"`HasWorkAsync` true when any source has
`position_known = 0` and a live file."* Read literally, that requires an **existing** `source_state`
row with `position_known = 0`. On first deployment against a real 25,993-entry bank, `source_state`
starts completely empty — there is no row to find, `position_known = 0` or otherwise. If
`HasWorkAsync` is implemented as `SELECT COUNT(*) FROM source_state WHERE position_known = 0 ...`,
it returns 0 on the very first check and the repair job that #371 exists to ship never runs on any
bank that predates it — the single most important case.

This is not a hypothetical: it's the difference between an `INNER`/direct predicate on
`source_state` and a `LEFT JOIN entries ... source_state` that treats "no row" the same as
"`position_known = 0`." The plan does not say which, and **none of P1–P9 exercises this scenario.**
P1 and P2 both go through WP3's ingest path, which stamps `source_state` as a side effect of
running — they never see a bank where entries exist but `source_state` is pristine/empty, which is
what every upgrading production bank looks like on first open after WP2 ships.

**Fix:** add `StaleSourceSweepTests`/`ChunkIndexRepairTests` (WP2, not WP3 — the analogous case
applies to WP2's own repair job) case: `HasWorkAsync_OnABankWithEntriesButNoSourceStateRows_IsTrue`.
Seed entries with `source_file` set and an **empty** `source_state` table (the real post-upgrade
shape), assert `HasWorkAsync` returns true and the job repairs them. RED: implement the query as a
direct `WHERE source_state.position_known = 0` with no outer join — the new test goes red while
every other P-series test (which all go through paths that stamp `source_state` first) stays green.

### 4. [MAJOR] WP2 P7 depends on a production surface change the plan never names

Section: WP2 §Gates, P7 (line 327); cross-checked against
`src/AiRaccoon.Core/Memory/MemorySearchResult.cs` and `src/AiRaccoon.Infrastructure/Sqlite/SourceAffinityRanker.cs`.

P7 (`SourceAffinityRankerTests.UnknownPosition_TakesNoAdjacencyBoost`) requires
`SourceAffinityRanker.Rank` to know, per candidate, whether its source's position is known. Today
`MemorySearchResult` (`Hash, Ranking, Path, Snippet, SourceFile, ChunkIndex, TotalChunks`) carries
no such field, and `SourceAffinityRanker.cs:77,113` only ever reads `ChunkIndex`. For P7 to be
writable at all, either `MemorySearchResult` gains a `PositionKnown` (or similar) property, or the
ranker takes a side-channel set of known/unknown sources — and the SQL/store code that builds the
candidate list needs a join against `source_state` to populate it. **None of this is in WP2's file
list, because WP2 has no Files table** (every other WP does). This isn't fatal to TDD — a failing
test can still be written that simply won't compile until the field exists, which is a legitimate
red — but the plan should say so explicitly, the way it does for WP6a ("today the first cannot even
be written, because the information does not exist"). As written, P7 reads as a same-shape unit
test alongside P1–P6 when it actually requires a cross-cutting data-flow decision (which layer
carries `position_known` from `source_state` to the ranker) that nothing else in the plan makes.

**Fix:** name the surface change WP2 must add (e.g. `MemorySearchResult.PositionKnown`, wired
through `SqliteMemoryStore`'s query that builds candidates) in a Files table for WP2, and state
P7's RED the way WP6a states its equivalent: "cannot be written today; `MemorySearchResult` has no
position signal."

### 5. [MAJOR] WP2 leaves `ChunkBackfill.cs`'s changed behaviour ungated

Section: WP2 §How position is carried, the `ChunkBackfill.cs:96` row (lines 251–255); cross-checked
against `src/AiRaccoon.Infrastructure/Ingestion/ChunkBackfill.cs:1-100`.

WP2's own disposition table says `ChunkBackfill.cs:96` changes behaviour: *"pieces of a split row
inherit the parent's span; the source is then marked position-unknown and the repair job fixes it
from the file."* Verified: `ChunkBackfill.RunAsync` today deletes the over-budget row and inserts
its pieces, then calls `RecomputeChunkColumnsBankWide` once at the end if any row was replaced —
that whole-bank recompute is exactly what WP2 deletes. After WP2, `ChunkBackfill` needs new logic
(assign the parent's span to the pieces, call `UpsertSourceState` to mark the touched source
`position_known = 0`) — a real, non-trivial behaviour change to a file WP2's own parallelism map
(§3) lists as touched.

No gate in P1–P9 tests it. There is no `ChunkBackfillTests.SplitRow_MarksSourcePositionUnknown` or
equivalent. A backfill that forgets to call `UpsertSourceState` (silently leaving the source at
whatever it was, possibly `position_known = 1` from a prior ingest) would let
`SourceAffinityRanker` boost adjacency on chunk_index values that `ChunkBackfill` just
scrambled — precisely the R10 failure mode ("`position_known` is added and then ignored") the plan
warns about elsewhere, but for the one caller that doesn't have a P-series test watching it.

**Fix:** add `ChunkBackfillTests.SplitRow_MarksItsSourcePositionUnknown` (RED: omit the
`UpsertSourceState` call — test goes red, P1–P9 stay green since none of them exercise
`ChunkBackfill`) and a companion asserting pieces inherit the parent's `chunk_index`/`total_chunks`
rather than getting a fresh (wrong) recompute.

### 6. [MAJOR] WP7 D5's stated RED perturbation is ambiguous about which conjunct it drops, and as literally described may not produce the claimed catastrophe

Section: WP7 §RED (lines 755–756).

`HasWorkAsync` is defined (WP3 §Files) as "any source behind the current `ChunkerVersion` **with a
live file**" — two conjuncts. D5's RED says: *"drop the stale-source predicate so the sweep
re-ingests everything; **D6 stays green** while D5 goes red, a sweep that loses no text and
destroys the 1,304 non-re-derivable rows."* Destroying the 1,304 unrepairable rows (1,143
gone-file + 161 sourceless) specifically requires dropping the **live-file** conjunct, not the
**chunker-version-stale** conjunct — "the stale-source predicate" most naturally reads as the
version check. If only that conjunct is dropped, the sweep re-processes every live-file source
regardless of version, which is wasteful but not destructive (the file still exists, so
reconciliation's `old ∩ new` still matches and nothing not genuinely changed gets deleted). The
perturbation that actually produces the claimed catastrophe is dropping the live-file conjunct —
which is B5/B10's territory in WP3, already gated there, but not restated for WP7's D5 with the
elected chunking arm active. Either the plan means the whole `HasWorkAsync` condition (both
conjuncts) and should say so, or D5's test needs to isolate the version-only conjunct and a
separate test needs the live-file-only conjunct — an AND of two guards needs to be shown to fail on
each arm individually, not asserted to fail on "the predicate" as one unit.

**Fix:** split into two explicit perturbations and name which one is under test: "D5a — drop the
chunker-version conjunct (re-ingests already-current sources; wasteful, not destructive — D6 alone
would not catch this since no text is lost, so D5 needs an explicit 'sources not behind
ChunkerVersion are left alone' assertion)" and "D5b — drop the live-file conjunct (destroys the
1,304 rows; this is the one that needs D6's opposed pairing)."

### 7. [MINOR] Plan's own quoted `MemorySchemaDdlStatementCountTests` numbers are stale relative to the worktree it's built on

Section: WP2, fact #11 area (line 288); ground truth given in the task brief also repeats it
("pins the 39/42 statement counts"). Cross-checked against
`tests/AiRaccoon.Tests/Integration/MemorySchemaDdlStatementCountTests.cs`.

The file currently on disk at `a2a48b3e` pins `CountDdl(statements).ShouldBe(40, ...)` (the
digest-stale path) and `statements.Count.ShouldBe(4, ...)` (the digest-matches path) — not 39/42.
The 39/42 figures are ADR-0075's original measurement, superseded by ADR-0076 adding
`model_migration` (the test's own comment says so: *"39 measured at ADR-0075 ... +1 for ADR-0076's
model_migration table"*). This isn't a gate-design problem — the test itself is fine and its
RED (adding a table moves the count) is real and will fire correctly — but the plan states the
wrong baseline for whoever updates the pin, and it's exactly the kind of drift
`check-sources-not-yourself` exists to catch. Low severity because updating "the pin" to whatever
the test currently reports (40 → 41, and no assertion currently pins a "42" total on the stale
path at all) is a mechanical, self-correcting step regardless of what number the plan quotes — but
worth fixing before it's cited again in a PR description as "39/42."

**Fix:** WP2's write-up should say "pins 4 / 40 today (ADR-0075's 39 plus ADR-0076's
`model_migration` table); adding `source_state` moves the 40 to 41" rather than repeating the
stale 39/42.

### 8. Verified sound — no finding

The following claims were checked against the actual files and hold up:

- **`HeldOutRetrievalGateTests.cs`** — the reversal-discrimination test
  (`ReversedRanking_FailsTheHeldOutMeanFloor`) is real, currently passing, and its docstring
  explicitly documents that the retired `ShouldBeInRange(0,1)` assertion survives the same
  reversal — this is the exact mechanism the plan's WP4/WP7 procedure leans on, and it's already
  proven to discriminate (per-query floors do not; the mean does — A8=0.131205, A9=0.553146,
  A10=0.169580 all match the plan exactly).
- **`ParityGateTests.cs`** — `NdcgParityDelta = 0.02` is a real one-sided non-regression check
  against a vendored golden reference; C10's reuse of it for the flag-on variant is a legitimate
  reuse of an existing, working pattern, not a new tautology.
- **`ReciprocalRankFusionTests.cs`** — `61.0/62` and `Fuse_EmptyModalityList_DoesNotEmptyTheResult`
  are real, exact-tolerance pins; C1's claim that these are the leak detector is accurate — a
  heuristic applied unconditionally would visibly move these numbers.
- **`ReciprocalRankFusion.Fuse`** — confirmed it iterates only lists with entries and returns `[]`
  when all scores are empty (`scores.Count == 0`); WP5's Q1 claim ("absent leg contributes 0, not a
  sentinel worst rank") is accurate to the code, not an assumption.
- **`AdrIndexTests.cs`** — `AdrNumbers_HaveNoUnrecordedGaps` and `RecordedSkips_NameNoAdrThatExists`
  are real and would behave exactly as WP6/WP7 describe (a missing ADR row fails the first; a
  contradicted "never used" entry fails the second).
- **`MemorySchemaVersionTests.SeedV1BankAsync`** (lines 868–917 in that file) is a real, working
  precedent for hand-building a wrong-shaped bank and driving `EnsureAsync`/a check over it — a
  legitimate template for A1–A3's RED perturbation.
- **`ChunkingCorpusGuaranteeTests.cs`** currently has no chunk-order assertion — P2's proposed
  addition (`ChunkIndexMatchesDocumentOrder`) is genuinely new, not a restatement of an existing
  passing check.
- **WP6's C6/C7/C8** are three genuinely independent guard conditions (config weight literally 0,
  `queryVector is null` from no engine, and `QueryFtsBatchAsync`'s caught `SqliteException` —
  confirmed at the cited location, still returns `[]` on catch), so three separate tests rather than
  one combined test is correct, not padding — a combined test would leave two of three guards
  unverified, exactly as the plan says.
- **A1–A3 vs A4 (WP1)** — the plan states this opposed pair itself and it is real: a check that
  always reports "wrong" passes A1–A3 and fails A4, and vice versa.
- **B1 vs B2/B3 and B1 vs B5/B6 (WP3)** — both are real, working opposed pairs distinguishing
  set-reconciliation from wholesale replace-all and from a badly-scoped delete, respectively (see
  Finding 2 for the intersection they still miss).

---

## Still open

- I did not verify WP4/WP5's numeric claims (5,799/4,396/2,370 table-line counts, the 44/19/3 query
  corpus split, the 908/1,343 incremental-ingest figure, the 59/59 out-of-order figure) against the
  real bank or corpus — those are measurement claims outside a static-review lane's reach; they are
  falsifiable in principle (Finding-free) but "capable of firing" for F1–F7 depends on the real bank
  actually containing what's claimed, which only a run against it can confirm.
- I did not check whether `IMaintenanceJob`/`MaintenanceJobs.cs`'s actual interface shape supports
  the "not stamped on failure" retry semantics WP2/WP3 both lean on (ADR-0070) — that's closer to
  the regression-surface/architecture lane than test honesty, but it directly bears on whether P1's
  "ships first" ordering claim and the HasWorkAsync gap in Finding 3 are even structurally possible
  to test the way described.
- Findings 1, 4, 5 and 6 are all instances of the same root cause: WP2 is the only work package
  without a Files table, and it is the one carrying the most cross-cutting production surface
  changes (a new field or side-channel for position, a new `ChunkingDefaults.ChunkerVersion`
  constant — confirmed absent from `src/` today — and behaviour changes in two other files'
  callers). Whoever picks this plan up should treat "WP2 needs a Files table" as a prerequisite
  fix, not a nice-to-have, since at least two gates (P7, and the missing `ChunkBackfill` gate) are
  currently unwritable from the plan's text alone.
