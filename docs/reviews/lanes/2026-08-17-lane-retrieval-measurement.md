# Lane — retrieval correctness and measurement rigor

Target: `docs/plans/2026-08-17-issue-close-357-367.md` (1002 lines), base `a2a48b3e`.
Scope: WP4, WP5, the WP2 decision point, and every falsifier in the plan. Read-only.

**Verdict up front.** WP3 and WP1's measurement posture are sound. WP4 as designed **cannot
adjudicate anything**, and not for the reason the plan defends against. The plan spends its
methodological effort on the risk of *re-pinning a floor that moved*; the measured reality is that
**no offline surface in this repo moves at all under a table-chunking change**, because the
held-out tier's three expected documents and the entire 68-query benchmark corpus contain **zero
markdown tables**. That is ADR-0072's trap in its exact original form — "the gate corpus cannot even
see the defect class" — reproduced one ADR later, and the plan states the opposite as a fact
(§WP7:762). WP5's headline rule is not the ADR-0006 enforcement the plan claims, is not well
defined, and its stated safety property is false.

---

## Blockers

### BL-1 — The offline surface is measurably blind to the defect class. §WP4 "The ADR-0056 / ADR-0072 constraint" (517-538), §WP7 (760-779)

**The plan is wrong, and it is wrong in the direction that matters.**

Measured by me, read-only, on the shipped fixture
(`tests/AiRaccoon.Tests/Resources/jsaa-memory.db`, 2,518 entries):

| Surface | Table-bearing content |
|---|---|
| Held-out tier expected sources (A8 `docs/adr/0013-…md`, A9 `…0086-…md`, A10 `…0014-…md`) | **0 chunks with a pipe character, let alone a table** (12 / 7 / 12 chunks each) |
| All 19 gradeable expected sources | **3 of 19** have any table-separator chunk (A5 1/6, A6 3/39, A7 1/19) |
| Whole fixture | 176 / 2,518 = 7.0% carry a header separator |
| `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs` (the 68-query / 174-doc ParityGate corpus) | **`grep -c '|'` returns 0** — not one pipe character in the file; bodies are 2-4 sentence verbatim excerpts |

Consequences the plan does not draw:

1. `HeldOutRetrievalGateTests` **cannot see any WP4 arm**. Not one of the three held-out expected
   documents contains a table, so no arm changes their chunk boundaries. Whatever those numbers do
   is second-order competition noise from the other 176 table chunks.
2. `ParityGateTests` (§WP4 rule 2, 528-529) is named as "the non-regression side". It is not even
   that for this change class: a corpus with zero pipe characters cannot regress under a
   pipe-table-aware chunker. It proves nothing, and the plan cites its `NdcgParityDelta = 0.02`
   (`tests/AiRaccoon.Tests/Integration/ParityGateTests.cs:21`) as if it were coverage.

**Fix.** State this measurement in §WP4 as the leading constraint, before the grid. Then either
(a) accept that WP4's only honest deliverables are F4/F5/F6/F7 (cost, FTS rank, cell preservation,
existing guarantees) and drop every retrieval-score claim from the design, or (b) name the held-out
capacity that would be needed — table-bearing expected documents graded by someone other than the
change's author — as a precondition and stop WP4 until it exists. Option (b) is ADR-0058's own
"what would unblock it", and this plan is the third consecutive record to arrive at it.

### BL-2 — §WP7:762 states a mechanism that does not exist. The held-out floors do not move.

> "**A chunking change moves boundaries, which moves the corpus hash map, which moves every one of
> those numbers.**"

**This is false.** `HeldOutRetrievalGateTests.cs:60` copies a **committed binary fixture**,
`Resources/jsaa-memory.db`. Nothing in the test path re-chunks it. The only thing that re-chunks it
is `tests/AiRaccoon.Tests/Integration/JsaaCorpusRegenerationTool.cs`, which:

- skips unless `AIRACCOON_REGENERATE_JSAA_CORPUS=1` (`:35-42`);
- reads a **second checkout on one specific machine**, default
  `/Users/arasz/RiderProjects/job-search-ai-assistant` (`:28`), at pinned commit
  `9397bbef504b5b30a31003c84e8c5c316641adb6` (`:30`);
- overwrites the committed fixture, and **is skipped in CI by construction** (`:17-19`).

So the WP7 §"measurement trap" procedure (steps 1-5, 766-776), the R8 exposure analysis (976-980),
and the §Parallelism note "WP2 re-pins, WP7 may re-pin" (840) all defend against a threat that does
not occur unless someone deliberately regenerates. The real threat is the opposite one and is worse:
**the gate goes green having measured nothing**, which is indistinguishable from having passed.

Compounding: WP4's grid needs a per-arm retrieval number. Producing one means regenerating
jsaa-memory.db **eight times** (cells A, B, C, D, E, F, D′, G), each run requiring the second
checkout, the pinned commit, the bundled model, and a full re-embed of ~2,518 chunks. The plan
never mentions the fixture, the tool, the pin, or that cost — `grep -niE "jsaa|regenerat|fixture"`
over the plan returns only unrelated hits. If that checkout is absent or the pin has drifted, WP4
has **no** held-out measurement and F3 cannot be evaluated at all.

**Fix.** Replace §WP7:762 with the measured mechanism. Budget the regeneration explicitly, name the
external dependency as a risk, and state what WP4 does if it is unavailable.

### BL-3 — WP4's response variable is undefined and is not comparable across arms. §WP4 F1/F2/F5 (496-500), §WP2 decision table (347-351)

F1, F2, F5 and the WP2 decision point are all keyed on "the rank of **the target**". The target is
`entries.id = 18336` — a specific 1,012-char chunk. Under arms C, D, E and F **that chunk does not
exist**: it is replaced by a whole-table chunk, a row chunk, a set of cell chunks, or a generated
sentence. "The rank of the target" therefore measures a different object in every arm, and A→B,
B→{C,D,E,F} and D vs D′ are differences between incommensurable quantities.

Worse, arm E and arm F multiply the number of retrievable units that contain the answer, which
mechanically improves any rank-of-any-matching-unit metric without improving retrieval at all.

**Fix.** Define the target as a **content predicate fixed before measurement** — e.g. "the
highest-ranked returned result whose text contains the substring `the stored text hit the 200-char
field cap`" — plus a separate file-level rank for `docs/retrieval.md`, and report both. Write the
predicate into the record before the first arm is run.

### BL-4 — The two axes are not independent, and the grid cannot detect the interaction or separate axis 3. §WP4 "The design is two axes" (429-469)

The plan asserts (429): "Prose/table separation and intra-table granularity are **independent**."
That is an assumption, not a finding, and the grid is drawn so it can never be tested: **axis 1 = off
has exactly one cell (A)**. There is no (1a, 2c) cell, so no interaction term is estimable. The
interaction is moreover the likely case — with axis 1 off, a per-row chunk is immediately re-packed
with the surrounding prose by the greedy packer, so 2c under 1a is close to a no-op. If that is so,
"axis 2's effect" is not a main effect at all.

**Second, and concretely wrong:** the plan's own rule (453, restated 907-909) is *"Any axis-2 arm
finer than 2b is being compared unfairly without [axis 3] … An arm compared without it has been
handicapped, not tested."* The grid then lists **B (1b, 2a, —)** and **C (1b, 2b, —)** with axis 3
off, and D/E/F with axis 3 on. So the contrast the plan calls "axis 2's effect given axis 1"
(B→{C,D,E,F}) differs on **two** factors at once — exactly the confound §R2 (944-947) says the grid
exists to prevent. And 2b is not exempt: a whole-table chunk carries the table's header row but no
`#` line, so `HeadingPathParser.Parse` (`src/AiRaccoon.Core/Chunking/HeadingPathParser.cs:9-33` —
verified: it scans only the chunk's own text and returns `""` with no `#` line) gives it an empty
section and it forfeits the same 4× bm25 weight (`MemorySql.cs:109,116`). Cell C is handicapped by
the plan's own definition.

**Fix.** Either hold axis 3 constant across every axis-2 contrast (add B′/C′ at 3b, or run
D′/E′/F′ at 3a and compare within one level), or drop the claim of decomposition and report the
grid as what it is — eight point measurements. Add at least one (1a, 2c) cell if the independence
claim is to be made at all.

### BL-5 — `max(rrf, best_single_leg)` is not what the plan says it is. §WP5 (565-572), §5 (918-927)

Three separate defects in one claim.

**(a) It is not ADR-0006:49-50.** Verified verbatim: `docs/adr/0006-rrf-parameter-optimization.md:49-50`
reads *"Fusion regression gate (hybrid exact-chunk rank ≤ best single modality) holds on all eleven
queries"*, and the Consequences restate it as *"'No fusion regression' is enforced on **the
exact-chunk rank**"* with the three graded examples. That is an **empirical gate over one designated
expected chunk per tuning query**, measured and reported. It is not a universally quantified
invariant over all results. The plan promotes it to one and then says the promotion "needs no new
justification" (924-925). It needs exactly that justification, because as a universal it is
**unsatisfiable**: when the two legs' rank-1 differ, both cannot occupy hybrid rank 1.

**(b) The safety property is false.** "It can only *raise* a result … never lower one, so it cannot
demote a vector-leg winner (which gets the same floor)" (570-572, 921-923). Rank is a total order:
raising X above Y *is* lowering Y. And "gets the same floor" means **tie**, and ties in
`ReciprocalRankFusion.Fuse` are broken by `ThenBy(result => result.Path, StringComparer.Ordinal)`
(`src/AiRaccoon.Infrastructure/Sqlite/ReciprocalRankFusion.cs:53`) — **alphabetical path order**. A
rank-1 floor applied to both legs makes the top hybrid result a function of the file name. That is a
concrete harm mode the plan's asymmetry argument says cannot exist.

**(c) The rule is not well defined, and its magnitude is discarded downstream.** `best_single_leg`
has no common scale: the FTS leg's `Ranking` is raw `bm25(entries_fts, 1.0, 8.0, 4.0)`
(`MemorySql.cs:109`), negative-better, ordered ascending by `ModalityCandidates.ByBm25`; the vector
leg's is a fused cosine, positive-better, ordered descending by `ByCosine`. The plan never says how
they are normalized to be `max`-able against a fused score that has already been divided by its own
max (`ReciprocalRankFusion.cs:46,51`).

And the magnitude does not survive: `SqliteMemoryStore.cs:260` fuses, then `:269` calls
`SearchResultMerger.Merge`, which **re-fuses the already-fused list** at weight 1.0
(`SearchResultMerger.cs:26`) — the ADR-0058 defect, pinned by
`Merge_RebuildsScoresFromRankPosition_DiscardingTheFusedScores`. Every fused score is replaced by
`(k+1)/(k+rank)` from its *position*. So a score floor has effect **only** through the order it
produces, and `query.MinRelativeScore` is then compared against the positional curve, not against
the floored score (ADR-0058's `Merge_FloorComparesAgainstThePositionalCurve_NotMatchQuality`).

**Fix.** Drop "needs no new justification". State the rule as a **rank** rule, not a score rule,
since only rank survives. Define the tie-break explicitly rather than inheriting the alphabetical
one. Add a WP5 question: what does the rule do when both legs have a rank-1 and they differ — which
is the common case, not the edge case.

### BL-6 — WP2's `position_known` gate silently disables ADR-0005 on every existing bank and permanently on the test fixture. §WP2 (306-315), §PR shape (859)

The plan requires (309-311): *"`SourceAffinityRanker` must skip the adjacency boost and the
consolidation merge for candidates whose source is not `position_known`."*

`SourceAffinityRanker.Rank` (`src/AiRaccoon.Infrastructure/Sqlite/SourceAffinityRanker.cs:11-21`) is
a pure function over `IReadOnlyList<MemorySearchResult>`; it has no bank access. Whatever mechanism
threads `position_known` in, the effect on existing data is the same: **`source_state` starts empty
on every bank that already exists**, so every candidate is position-unknown until the repair job
runs, and the boost is off for all of them.

For the retrieval gates this is not temporary. `jsaa-memory.db` is a committed fixture whose source
files live in a second checkout at a pinned commit; the repair job's precondition is *"a live file"*
(314-315). Those files are not present. **The fixture can never become `position_known`, so
ADR-0005's λ boost and consolidation are permanently off for `HeldOutRetrievalGateTests`,
`RrfParameterSweepTests`, `SourceAffinitySweepTests` and every other test built on it.**

ADR-0006 credits *source-affinity ranking* with fixing the C2 case — the plan itself says so at
122-123 and gives H-c "top billing" on that basis. Turning it off wholesale is not, as §PR shape:859
says, *"a plain bug fix with no corpus-adjudication problem"*. It is a first-order ranking change
whose gate corpus is the same one this plan has just established cannot adjudicate ranking changes.

It also contaminates the decision-point experiment (BL-7): after WP2, the target's rank moves for
**two** reasons — corrected ordering (affinity on, right neighbours) and the ranker gate (affinity
off wherever unrepaired). The experiment attributes the whole delta to H-c.

**Fix.** Add a gate that the fixture-backed retrieval suites are run both before and after WP2 and
the delta reported, and decide explicitly what `position_known` means for a source with no live
file — "unknown" and "boost off" is a defensible answer, but it must be measured, not assumed
harmless. Consider defaulting `position_known = 1` for rows written by a chunker version that
already stamps position, so a freshly-ingested bank is not silently degraded.

---

## Majors

### MJ-1 — F3 cannot discriminate; it rules out every arm. §WP4 F3 (498)

> "An arm does not beat the best cheaper arm by more than the held-out measurement's own spread
> (ADR-0058 measured a ±0.03 band on n=3)"

Given BL-1, no arm changes any held-out expected document's chunking, so no arm can beat any other
by more than noise on that measurement. F3 therefore fires for **every** arm — including the
cheapest one, which then has nothing to beat. A falsifier that rules out the entire arm set is as
uninformative as one that rules out none; it just produces "unadjudicable" by a route that looks
like a measurement. Also, F3 uses the held-out tier to *reject arms*, which contradicts the plan's
own rule 2 (528-529: the corpus "cannot elect a winner") — rejecting all but one is electing.

**Fix.** Delete F3 or restate it against a surface that can vary under the change.

### MJ-2 — F4's threshold is on the wrong denominator and cannot fire for the arms it is aimed at. §WP4 F4 (499)

F4 rules out an arm whose "entry-count multiplier on the real-bank copy exceeds **2×**". That is
bank-wide, but the effect is confined to the table subset. Using the plan's own numbers: 2,370
header-separator chunks on 25,993. A row-level arm (D, F) expanding a 4-6 row table into 4-6 chunks
gives roughly 1.3-1.5× bank-wide — **F4 cannot fire for D or F.** It fires only for arm E (per cell),
and only when tables average ≥3 columns. The threshold 2× is not derived from anything (no embed
budget, no bank-size budget, no latency budget is cited).

Separately, F4's premise text is overstated as measured: *"5,799 chunks (22.3%) contain a table
line"*. The 22.3% figure counts any pipe-bearing line, which also catches shell pipes inside code
blocks. The defensible table figure is the 9.1% header-separator count, which the same sentence
also gives. §R3 (949-951) then propagates the loose number as "22% blast radius" — a ~2.4×
overstatement presented as measurement.

**Fix.** Express F4 per table-bearing source (or as an absolute embed-time / bank-size budget), and
derive the threshold from something. Use 9.1% wherever the claim is about tables.

### MJ-3 — F1, F2 and G1 use the single #367 query as a decision metric, which the plan forbids. §WP4 rule 1 (527), F1/F2 (496-497), §WP5 G1 (597)

Rule 1: *"The #367 query is a single named counterexample, reported as one, **never as a metric**."*
F1 and F2 then kill the entire chunking package, and G1 kills the entire fusion package, on that one
query's rank crossing 5. Rule 3 (530-532) additionally says the real bank — the only place the
target exists — is *"not a gate"*. F1/F2/F5/G1 are gates on the real bank.

This is not pedantry: it is the same n=1 selection ADR-0058 refused at n=3, applied to the one
observation that motivated the work.

**Fix.** Either drop rule 1/rule 3 as written and defend n=1 explicitly as a *stop* criterion only
(a single counterexample can honestly justify stopping work, never electing an arm), or drop
F1/F2/G1. The asymmetric version is defensible; the plan should say so rather than holding both.

### MJ-4 — The decision-point experiment is written so work proceeds. §WP2 "The decision point this package creates" (341-352)

- Two of the three bands say "WP4 proceeds". The third says *"**Re-scope** WP4/WP7 before building —
  a chunking change **may** not be needed"*. Nothing says stop.
- **Ranks 15, 16 and 17 are not in the table.** The bands are ≤5, 6-14, "~18, unchanged". The
  default reading of an unruled outcome is the third band, i.e. proceed.
- The plan's own evidence says H-c is the most likely dominant cause (119-131: ADR-0006 credits
  affinity with fixing the identical rank-18 symptom; 59/59 files out of order; `retrieval.md`
  itself has 10 inversions in 60 chunks). So the **most likely outcome triggers the weakest
  ruling**.
- n = 1, one run, no tolerance, no tie/rounding rule, no statement of what else changed (see BL-6:
  after WP2 the delta has two causes).
- "a bank with repaired ordering" is unspecified. The real bank cannot be opened read-write (rule 3
  at 530-532; ADR-0075 "only the server writes"), so the experiment must run on a copy with the
  maintenance job forced. The plan never says this, and §WP4 line 424 repeats "**Baseline: a bank
  with WP2 merged and the repair job run**" with the same ambiguity.

**Fix.** Make the ≤5 band a hard stop on WP7 ("WP7 is not built; #367 closes on WP2+WP3+WP6 unless
new evidence is produced"), close the 15-17 gap, name the exact bank and the exact command, and
report the rank with affinity forced on and forced off so BL-6's confound is separated.

### MJ-5 — The fusion flag is never gated against the held-out floors with the flag on, and no falsifier catches the held-out mean going down. §WP6 gates C1/C10 (682, 691), §WP5 G1-G3 (597-601)

C1 runs `HeldOutRetrievalGateTests` **flag off**. C10 runs `ParityGateTests` flag on. Nothing runs
the held-out floors flag on. Meanwhile G1 fires when the rule does *not* help #367, G2 fires when it
changes order but the held-out mean *barely moves*, G3 fires on `ParityGateTests` nDCG@10. **There is
no falsifier and no gate for "the held-out mean drops by more than 0.03 with the flag on."** The one
outcome that is unambiguously bad — the rule works on #367 and damages the queries nobody tuned on —
falls through every check.

**Fix.** Add G0: the winning rule must hold `HeldOutMean_HoldsItsFloor` and every per-query floor
with the flag forced on; failing that it does not ship even off. Add the flag-on variant of
`HeldOutRetrievalGateTests` to the WP6 gate table beside C10.

### MJ-6 — G2 has no threshold and is pre-declared as the expected outcome, so it cannot function as a falsifier. §WP5 (598-604)

*"changes order on a **meaningful share**"* — undefined, so G2 cannot be evaluated deterministically.
And 603-604 states in advance that G2 is the likely outcome, while G2 *"does not kill the package;
it is the recorded reason the flag defaults off."* A criterion whose predicted firing changes
nothing about what ships is not a falsifier; it is a pre-written excuse. Pre-registering the
expectation is good practice — pre-registering it as consequence-free is not.

**Fix.** Give "meaningful share" a number before measuring (e.g. ">25% of the 19 gradeable queries
change top-1"), and state what G2 firing *changes* beyond the default the plan already chose.

### MJ-7 — "No arm is adjudicable" is not an escape hatch; on this evidence it is the determined output, and nothing forces it to be taken. §WP4 rule 5 (535-538), §R11 (991-994)

Answering the question directly: the statement is **honest in intent and structurally unreachable as
written**. Taken together, rule 1 (no metric from #367), rule 3 (real bank is not a gate), rule 4
(no manufactured table corpus) and BL-1 (every existing graded surface is table-blind) leave WP4
with **no surface on which a retrieval score for an arm can legitimately be produced**. So
"unadjudicable" is not one possible outcome among several — it is the only outcome consistent with
the plan's own rules, and it is knowable *now*, before any measurement.

The pressure this creates is the danger. When the grid produces no separating number, the cheapest
way out is to quietly relax rule 3 (score on the real-bank copy after all) or rule 4 (add a handful
of table queries "just to see"). That is precisely ADR-0072's named trap, and it will present itself
as diligence.

**What would force the honest output to be taken.** One gate, stated now: *WP4 may not report any
arm's retrieval score unless the queries producing it were graded against expected documents that
(a) contain markdown tables, (b) existed in the corpus before this plan was written, and (c) were
graded by someone other than the author of the chunking change.* No such query exists today — I
checked all 19 gradeable ones. So the gate is a real constraint, not a formality, and it converts
rule 5 from permission into obligation. Add it to the `research-record-audit` criteria at 542-546,
which currently requires "F1-F7 answered per arm" and would pass a record full of unadjudicable
numbers.

### MJ-8 — The floor's tie-break is alphabetical, and the plan never mentions it.

Covered under BL-5(b). Called out separately because it is the concrete failure scenario: with the
rule enabled, two queries whose legs disagree at rank 1 return whichever result's `Path` sorts first
under `StringComparer.Ordinal` (`ReciprocalRankFusion.cs:53`). `SourceAffinityRanker`'s own
tie-break chain (`:50-55`) is `score → docScore → Path → ChunkIndex`, so the same bias persists
through affinity ranking. Any WP5 candidate rule that produces ties needs an explicit, defended
tie-break, and WP6 needs a gate on it.

---

## Minors

- **mn-1 — "invisible to everything, metrics included" is wrong.** §WP6a:615-618 and §WP5 Q2:586-588
  say the swallowed FTS `SqliteException` is *"currently unobservable at the fusion point"* (true)
  and *"invisible to everything, metrics included"* (false). `SqliteMemoryStore.cs:843-847` calls
  `Log.KeywordModalityFailed(logger, ex)` before returning `[]`. WP6a's independent value is real
  but smaller than claimed; say "unobservable at the fusion point and in metrics; logged only".
- **mn-2 — cited line numbers drift by one to three.** The fusion point is
  `SqliteMemoryStore.cs:260`, not `:259` (the plan cites `:259` five times: 557, 618, 630, and the
  Files table). `QueryFtsBatchAsync`'s catch is `:843-847`, not `:826-849`. Harmless now, actively
  misleading after WP2 edits the same file.
- **mn-3 — P7's mechanism is unspecified and it is not free.** `SourceAffinityRanker.Rank` is pure
  over `MemorySearchResult`, which carries no `position_known`. Threading it in means either a new
  field on `MemorySearchResult` (touching both leg projections — `MemorySql.SearchByFilter:109-117`
  and the vector query) or a second parameter carrying a per-source set. §WP2's file list does not
  name `MemorySearchResult` or the vector SQL. Decide it in the plan; it is the difference between
  a two-line change and a projection change across both legs.
- **mn-4 — arm G's premise is already answerable.** §WP4 cell G measures "the duplicate-row effect"
  of the 48-token overlay duplicating the clipped row into chunks 7 and 9. Note that
  `SourceAffinityRanker.Consolidate` (`:113`) only merges siblings at `|Δchunk_index| == 1`, so
  chunks 7 and 9 (Δ = 2) can never be consolidated — the two copies genuinely compete and always
  will. That is a read finding, not a measurement, and belongs in the record as one.
- **mn-5 — F5 is measurable but under-specified.** "Moves the target off FTS rank 1" requires an
  FTS-only search (`VectorWeight = 0`), which is the reproduction path the plan itself names at
  583. Say so, and pair it with BL-3's content predicate or F5 measures a different chunk per arm.

---

## What is sound (one line each)

- **WP3's gate design.** B1↔B2/B3 and B1↔B5/B6 are genuinely opposed pairs, and the stated red
  perturbations (a wholesale-replacement implementation passing B1 while destroying every rating)
  are the right shape. Best-designed package in the plan.
- **WP4's Q1/Q2 premise (absent ≠ low-ranked; enumerate degradation).** Verified correct:
  `ReciprocalRankFusion.Fuse:26-39` iterates only lists containing the hash, so an absent hash
  contributes 0; and both legs' raw scores are on `MemorySearchResult.Ranking` in the per-leg lists
  (`ModalityCandidates.ByBm25`/`ByCosine`), so no extra query is needed. C5's opposed-pair framing
  (substitute `list.Count + 1` for a missing rank; C5 red while C4 green) is exactly right.
- **The axis-3 justification.** `HeadingPathParser.Parse` verified to scan only the chunk's own text
  and return `""` with no `#` line, forfeiting the 4× `section` bm25 weight
  (`MemorySql.cs:109,116`). The reasoning is correct even though the grid then violates it (BL-4).
- **The WP7 reversal-probe hard stop (step 4).** Correct instinct. Note the coupling the plan
  misses: `ScoreAsync` grades relevance at **file** level over the expected source's hash set, so an
  arm that multiplies a file's chunk count raises both the forward and the reversed score and
  degrades the probe's discrimination. F4 (cost) and the probe's validity are the same knob, not
  independent ones.

---

## Still open

1. **Whether the second checkout exists and the pin still resolves.**
   `JsaaCorpusRegenerationTool.cs:28,30` needs `/Users/arasz/RiderProjects/job-search-ai-assistant`
   at `9397bbef504b5b30a31003c84e8c5c316641adb6`. I did not look outside this repo. *Settles it:*
   `git -C /Users/arasz/RiderProjects/job-search-ai-assistant cat-file -e 9397bbef^{commit}`. If it
   fails, WP4 has no held-out measurement at all and BL-1/BL-2 escalate from "blind gate" to "no
   gate".
2. **What each WP4 arm actually costs to measure.** Regenerating the fixture eight times means eight
   full re-embeds of ~2,518 chunks plus the extraction step. I did not run it (read-only, and the
   invariant says a measurement must repay its cost). *Settles it:* one timed regeneration run.
3. **The exact magnitude of BL-6.** I could not run `HeldOutRetrievalGateTests` with source affinity
   forced off. *Settles it:* run the existing suite with `SourceLambda = 0` and compare against the
   pinned floors — that is a two-line probe and it tells you today whether WP2's ranker gate breaks
   the retrieval suite before anyone writes WP2.
4. **Whether `ManagedHarness` builds its bank by ingest at test time.** If it does, `source_state`
   would be populated there and BL-6 is confined to the jsaa fixture; if it loads a prebuilt bank,
   BL-6 hits ParityGate too. *Settles it:* read `tests/AiRaccoon.Tests/Integration/` harness setup.
5. **Whether any of the 44 queries' *unexpected* (distractor) documents carry tables.** I measured
   expected sources only. Distractor tables could still let an arm change competition even where the
   expected document has none — a weaker but non-zero sensitivity. *Settles it:* score one arm's
   regenerated fixture and diff per-query nDCG; if every delta is 0, BL-1 is total.
6. **The real bank's table-chunk figure at the header-separator definition per source file.** I have
   the bank-wide 9.1% from the baseline but not its distribution across sources, which is what F4's
   multiplier actually depends on. *Settles it:* a read-only group-by over a copy of the bank.
