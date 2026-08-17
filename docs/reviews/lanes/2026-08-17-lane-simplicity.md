# Lane: simplicity — the case against this plan

Date: 2026-08-17
Target: `docs/plans/2026-08-17-issue-close-357-367.md` @ `a2a48b3e`
Lane: `.ai-badger/invariants/ask-if-simpler.md` — "an abstraction added before a real caller needs it is a cost with no buyer."

**Verdict: the plan is roughly twice the size of the smallest plan that closes all three issues.**
7 work packages → 4. 5 PRs → 3. Two research packages → zero. One new table → none.
The plan is unusually honest — it predicts its own largest package will produce nothing (R11), it
names the trap it is walking toward (R8), and it builds the decision point that should cancel WP4.
It then schedules WP4 anyway. This lane's argument is that the plan should act on what it already
knows.

---

## The reduced plan, explicitly

| # | Package | Issue | PR | Delta vs plan |
|---|---|---|---|---|
| R1 | `ai-raccoon doctor` — one derived shape comparison | #357 | PR1 | WP1 minus `IBankCheck`, minus the DI collection, minus gate A7 |
| R2 | `chunk_index` is document order + in-place repair + ingest reconciliation | #371 (+ ADR-0069 duplicates) | PR2 | **WP2 + WP3 merged**, minus `source_state`, minus `chunker_version`, minus `StaleSourceSweep` |
| R3 | `max(rrf, best_single_leg)` floor behind a default-off flag + one metric + ADR-0077 | half of #367 | PR3 | WP6 minus `LegAvailability`, minus 4 of 5 metrics; **WP5 collapses into it** |
| R4 | Re-run #367's query on the repaired bank. Record the rank. Stop. | — | — | the WP2 decision point, promoted from a checkpoint to the task's terminus |

**Deleted outright: WP4, WP7, ADR-0078, PR5, and WP5 as a separate research package.**
Deferred to two follow-up issues sized by R4's number:

- **FU-A (defect-shaped, no adjudication needed):** prose and tables never share a chunk; no table
  body row is emitted without its header. Both are *correctness properties* with unambiguous
  property gates — the same shape as #371 — not tuning arms. ADR-0048 already measured the second
  one (33 of 34 chunks carry orphaned body rows) and already named it "unbuilt, not broken". It
  needs an issue, not an 8-cell grid.
- **FU-B (real research, blocked on its input):** whole-table vs per-row vs per-cell vs linearized.
  Opens only after held-out capacity exists. Allowed to sit blocked, because the honest state of
  that question today is "blocked".

---

## Findings

### BLOCKER-1 — `position_known` cannot reach `SourceAffinityRanker` without a join the plan never mentions

**Section:** §WP2 "`source_state`", gate P7, §5 item 4, R10.

`SourceAffinityRanker` (`src/AiRaccoon.Infrastructure/Sqlite/SourceAffinityRanker.cs`) is a pure
static over `IReadOnlyList<MemorySearchResult>`. It has no connection, no store, no I/O. The record
it consumes is `src/AiRaccoon.Core/Memory/MemorySearchResult.cs` — seven positional members, no
DB access anywhere in the type.

So P7 ("the adjacency boost is skipped when position is not known") forces one of two things the
plan costs at zero:

1. Add a member to the Core record `MemorySearchResult`, **and add a `LEFT JOIN source_state` to
   three hot-path SELECTs** — `MemorySql.SearchByFilter:109`, `VectorSearchByFilter:135`,
   `StructureVectorSearchByFilter:148`. The join key is `(ctx, source_file)`, and `ctx` is
   `MemorySql.ContextKeyExpression` — a length-prefixed `CASE` over `workspace_id`/`project_id`/
   `context_label` (`MemorySql.cs:576+`). That is a **computed, unindexable join predicate on the
   search hot path**, added to the FTS leg, the vector KNN leg and the structure KNN leg.
2. Or pass a pre-fetched set of position-unknown sources into `Rank(...)`, which needs its own
   query per search and changes the ranker's signature anyway.

Neither appears in §WP2's file list, §3's parallelism map, or §6's risks. A plan that adds an
unindexed expression join to every search leg and does not say so is understating its own cost.

**Smaller alternative — a sentinel in the column whose meaning is at stake.** `MemorySearchResult.ChunkIndex`
is a non-nullable `int` already carried by all three SELECTs. Have the repair job write
`chunk_index = -1` for rows whose position it cannot establish, and guard the ranker:

- `SiblingCount` (`:77`) and `Consolidate` (`:113`) each gain `if (a.ChunkIndex < 0 || b.ChunkIndex < 0) continue;`
- The insert path never writes a negative, so the sentinel is unambiguous by construction.
- `SyncService.cs:399` stops calling `RecomputeChunkColumnsBankWide` and stamps `-1` on merged rows
  instead — P8 satisfied with one UPDATE, no upsert into a second table.

Cost: two guard lines, one once-ever UPDATE over 1,143 rows. Zero schema change, zero join, zero
change to `MemorySearchResult`, zero change to the three hot-path SELECTs. P7 and P8 survive
unchanged as gates.

**Bonus correction the sentinel exposes:** the 161 sourceless rows need no marker at all.
`SourceAffinityRanker` already short-circuits on `candidate.SourceFile is null` at lines 24, 70 and
99. The plan's §WP2 folds them into the `position_known = 0` population; they were never in the
adjacency path. Only the 1,143 orphaned rows need the sentinel, and they are exactly what it covers.

**What is lost by cutting the join:** the ability to ask "is this whole source's ordering trusted?"
as a set operation. Acceptable: nothing in the plan asks that question except P7, and P7 is a
per-row question.

---

### BLOCKER-2 — `source_state` has zero justified members once you interrogate each one

**Section:** §WP2 "`source_state` — one new table, serving both #371 and option B"; §WP3
`StaleSourceSweep`; §5 items 4 and 9.

The table has two data columns. Take them separately.

**`position_known`** — replaced by BLOCKER-1's sentinel, strictly cheaper.

**`chunker_version`** — **has no consumer in this task.** Its only reader is `StaleSourceSweepJob`,
whose only purpose is to re-ingest sources after a chunker change, and the only planned chunker
change is WP7 — which the plan itself expects not to ship:

- §WP7 opening: "Conditional on WP4 and on the WP2 decision point. If WP4 cannot adjudicate, this
  ships nothing."
- §WP5: "Expected outcome, stated in advance… G2 is likely."
- R11: "WP4 returns 'no arm is adjudicable' … Likely enough to plan for."

So the plan ships a marker column, a sweep class, a maintenance job, a `ChunkerVersion` constant and
gate B10 to migrate a change it predicts will not happen. That is the textbook shape: infrastructure
built for a second caller that does not exist, justified by a first caller that is conditional.

**And when WP7 *does* eventually ship, `chunker_version` still isn't needed.** Staleness is
derivable, not markable. The repair job in §WP2 already reads each file, chunks it with the current
chunker, and builds `hash → position`; a stored row whose hash appears in no fresh chunk is stale.
The plan says this itself: *"This is option B's detection half, delivered free."* A sweep can use
exactly that derivation. `chunker_version` buys only the ability to **skip** a file without reading
it — across 1,343 source files, in a once-ever background job that must read most of them anyway.
That is a table to save seconds, once.

This also engages `.ai-badger/invariants/derive-or-delete-the-list.md` from the other direction:
`chunker_version` is a hand-stamped mirror of "what the chunker would produce", which drifts the
moment anyone changes a boundary without bumping the constant, and nothing compares the two sides.
The hash comparison *is* the comparison, and it cannot drift.

**Smaller alternative:** no table. `position_known` → `chunk_index = -1`. `chunker_version` and
`StaleSourceSweep`/`StaleSourceSweepJob` → deferred to whichever PR first changes chunk boundaries
(FU-A), where they will have a real caller and can be sized against a real migration.

**What is lost:** a cheap answer to "which sources are stale?" without touching the filesystem, and
per-source resumability recorded in the bank. Acceptable: §5 item 9 already argues resumability
beyond the row is unnecessary because reconciliation is idempotent and ADR-0070 does not stamp a
failed job — the same argument disposes of the row itself.

**Downstream deletions this enables:** §WP3's `StaleSourceSweep.cs`, `StaleSourceSweepJob`,
`UpsertSourceState` in `MemorySql`, gate B10, gates D5/D6 (WP7's, already conditional), R12's
`Ddl` statement-count re-pin, and the `MemorySchema.cs` region collision between WP1 and WP2 in §3.

---

### MAJOR-3 — the "a new table needs no ladder step" claim: verified true, but it is being used to price the table at zero

**Section:** §0 fact 11; §WP2 "No `user_version` bump"; §5 item 4.

Verified at `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:38-49`. The comment reads:

> "The schema shape this build creates. **Bumped by one per shipped schema change**, with a matching
> ladder step … Not every schema change needs a ladder step: **a trigger body replacement** that is
> safely re-runnable on every open belongs in the unconditional `Ddl` instead (ADR-0023 amendment) —
> the ladder is for changes that need guarded, one-time work."

Two things the plan's paraphrase moves:

1. The comment's *rule* is "bumped by one per shipped schema change"; the carve-out is exemplified
   by **a trigger body replacement**, not stated as "anything re-runnable". The plan quotes the
   carve-out as the rule. Mechanically the generalisation holds for `CREATE TABLE IF NOT EXISTS`
   (`EnsureAsync:405-436` applies `Ddl` whenever `application_id != SchemaDigest`, and a fresh
   table needs no guarded backfill), so the *conclusion* is correct. But `CurrentVersion` is
   documented as tracking schema shape, and a bank at `user_version = 10` will now mean two
   different shapes depending on binary age. That is precisely the confusion `doctor` exists to
   resolve — and the plan's own §WP1 mitigates it by printing `user_version` and the digest as
   context lines. The plan is creating the ambiguity in PR2 and shipping the reader for it in PR1.

2. **Does it change the calculus? No — it makes it worse, not better.** "No ladder step" is used in
   §5 item 4 as the reason the table is cheap. But the ladder step was never the expensive part.
   The expensive parts are the ones BLOCKER-1 and BLOCKER-2 name: three hot-path joins, a Core
   record change, a `Ddl` statement-count re-pin (admitted, R12), a `MemorySchema.cs` merge
   collision with WP1 (admitted, §3), a sweep class, a maintenance job and a gate — none of which
   the ladder-step argument touches. The claim answers a question nobody was going to charge for.

**The irony worth naming:** WP2 adds a table via `CREATE TABLE IF NOT EXISTS` **in the same task as
WP1, whose entire subject is that `CREATE TABLE IF NOT EXISTS` silently keeps a wrong-shaped table**
(#357). For a genuinely new table this is harmless today. It sets the precedent that shape-affecting
changes may skip the ladder, and the next `ALTER`-shaped change to `source_state` inherits it — at
which point `doctor` becomes the only thing standing between the bank and the exact bug it was
built to report. Cutting the table removes the precedent along with it.

---

### MAJOR-4 — WP1's `IBankCheck` frame is a plural with one member, and gate A7 has to manufacture the second

**Section:** §WP1 "A thin frame"; gate A7.

> "`IBankCheck` with exactly one implementation, collected as `IEnumerable<IBankCheck>` and iterated.
> That buys the derived-list property (A7) for one interface."

And A7's test is `Doctor_RunsEveryRegisteredCheck` — "registers a fake second check via DI".

A gate that must invent a fake collaborator to have anything to assert about is not proving a
property of the system; it is proving a property of the frame that was added to make it provable.
`.ai-badger/invariants/derive-or-delete-the-list.md` targets **hand-maintained lists that mirror
something else**. With one implementation there is no second side, so there is nothing to drift and
nothing to derive. The invariant is being cited to justify the thing it was written to prevent.

**Minimum that closes #357 honestly:**

| Keep | Cut |
|---|---|
| `SchemaShape` — the record graph | `IBankCheck` |
| `SchemaShapeReader` — pragma-based, never writes | the `IEnumerable<IBankCheck>` DI registration |
| `MemorySchema.ApplyDdlAsync` / `ExpectedShapeAsync` (derived expected side, the technique proven at `MemorySchemaDigestTests.cs:108-117`) | gate A7 |
| `BankShapeCheck` as a **static comparer** — `Compare(expected, actual) → findings` | — |
| `DoctorCommands`, `ExitCode.BankSchemaMismatch = 19` | — |
| Read-only open per `AppRegistrations.cs:93-126`, never `OpenBankAsync` | — |
| Gates A1-A6, A8 | — |

Five new files → four; one interface → zero; eight gates → seven. Everything §WP1 got right —
derived expected side, structural introspection never SQL text, never `EnsureAsync`, never repairs,
"unknown" not "wrong" for an unopenable bank, `user_version`/digest as context lines not findings,
no `--json` — survives untouched. **A5 remains the most important gate in the package** and is
unaffected.

**Scope of `SchemaShape` — one genuine trim.** #357 is about a wrong-shaped *table*. The plan builds
columns, indexes, foreign keys, defaults, declared types, triggers and virtual tables, then admits
mid-section that triggers have no pragma (existence and owning table only) and CHECK bodies are
excluded and the exclusion is printed. Keep trigger *existence* — it is one `sqlite_master` query
and nearly free. Cut nothing else; the pragma reads are cheap and the graph is one record. This is
the one place the plan's ambition is proportionate.

**What is lost by cutting `IBankCheck`:** the shape into which a future second check (row-count
sanity, orphan detection, FTS/vec consistency) would slot. Acceptable: adding an interface to a
static comparer when the second check arrives is a 20-minute mechanical refactor with a compiler
enforcing it, and the second check is not on any roadmap in this plan.

---

### MAJOR-5 — WP4 and WP7 should not exist in this task. Fix the defects, re-measure, stop.

**Section:** §WP4 entire, §WP7 entire, §1 rows WP4/WP7, §4 PR5, §6 R8/R11.

**Testing the architect's own pushback (§5, "One thing I would push back on if asked"): it is
correct, and the plan does not follow it.** The evidence:

1. **The plan predicts WP4's null result in three independent places.** §WP5 ("G2 is likely"),
   §WP7 opening ("if WP4 cannot adjudicate, this ships nothing"), R11 ("likely enough to plan for").
   ADR-0058 and ADR-0072 both measured and shipped nothing on smaller questions with the same
   evidence base. Planning two packages, one PR, one ADR, an 8-cell grid and a held-out re-pinning
   procedure around an outcome the author expects to be null is planning the branch nobody expects
   to take.

2. **The measurement cannot pay for itself, by the plan's own constraint section.**
   `.ai-badger/invariants/measure-when-it-pays.md`: run the experiment when its cost is repaid by
   the decision it settles. WP4 proposes eight arms adjudicated against a 3-query held-out set whose
   spread ADR-0058 measured at ±0.03, and F3 rules out any arm inside that band. Eight arms, each
   requiring a full re-chunk and re-embed of a 24,689-row bank copy, to produce a record whose most
   likely content is "no arm is adjudicable" — which §WP4 item 5 explicitly permits. That
   conclusion is available today, for free, by reading ADR-0058 and ADR-0072. The measurement is
   not repaid.

3. **Half of WP4's grid is not a tuning question at all, and the plan mis-sorts it.** Split the
   arms by whether they need adjudication:

   | Arm | Kind | Needs the corpus? |
   |---|---|---|
   | 1b — prose and tables never share a chunk | **property** | No. Gate: `ProseFollowedByATable_AreNeverPackedIntoOneChunk` (the plan already wrote it as D1) |
   | 2c+3b — no body row emitted without its header | **property** | No. ADR-0048 already measured 33/34 orphaned and named it "unbuilt, not broken" |
   | 2b — whole table as one chunk | tuning | Yes |
   | 2d — one chunk per cell | tuning | Yes |
   | 2e — linearized row | tuning | Yes, plus a text-generation risk (R9) |

   The two properties are defects with unambiguous fixes and deterministic gates — the same shape
   as #371, which the plan correctly ships without adjudication. They do not need WP4 to elect
   them; they need an issue. The three tuning arms are exactly the ones the corpus cannot
   adjudicate. **The grid's structure hides this by treating a correctness property and a tuning
   parameter as comparable cells.**

4. **The ground truth undercuts the sizing.** The defensible figure is **9.1%** of chunks carrying a
   markdown table header separator. §6 R3 sizes the blast radius at "22% blast radius" and §5's
   prior leans on the same neighbourhood; the 22.3% pipe-counting measure also catches shell pipes
   inside fenced code. The plan quotes all three numbers in F4 — correctly — and then argues scope
   from the largest. Sized honestly, WP7's addressable population is under a tenth of the bank,
   and 2.4× smaller than R3 implies.

5. **The #367 target chunk is a prose-dilution case, not a table-granularity case.** Direct
   inspection (which the plan reports well, §2) shows ~7/8 of the embedded text is prose about
   something else. Axis 1 — the plan's own cell B, "the cheapest possible change" — addresses that
   directly. The plan's own F2 says: if B reaches rank ≤ 5, "every arm finer than B" is ruled out
   as "unpaid-for complexity". **F2 is the likely outcome and it makes seven of the eight cells
   unnecessary.**

**Recommendation.** Cut WP4, WP7, ADR-0078 and PR5 from this task. Replace with:

1. Ship R2 (chunk_index order + reconciliation). Re-run #367's query. Record the rank. **Make the
   ≤5 branch a hard cancel, not a "re-scope"** — see MINOR-9.
2. If still buried, open FU-A as a plain defect fix with two property gates and `ParityGateTests`
   non-regression only (`NdcgParityDelta = 0.02`). **No held-out re-pinning**, because a property
   fix does not need a headline number — which sidesteps §WP7's entire five-step re-pinning
   procedure and R8's "not fully mitigable, and the plan's largest exposure".
3. If FU-A leaves it buried, *that* is the evidence that opens FU-B — and FU-B's first task is
   building held-out capacity, not measuring arms.

**The evidence that would decide it, named concretely:**
- **Cancels the chunking work entirely:** the target reaches rank ≤ 5 on the repaired bank (R4), or
  reaches it after the fusion floor (R3).
- **Justifies FU-A:** the target stays buried on the repaired bank *and* a manual inspection shows
  the failing chunk still mixes prose with a table (already true today for `entries.id = 18336`) —
  a property violation is its own justification and needs no ranking evidence.
- **Justifies FU-B:** the target is still buried after FU-A ships, with the chunk now containing
  only the table and its header. That is the only state in which "how finely should a table be
  chunked?" is a live question rather than a speculative one.
- **Overturns this recommendation:** a held-out query set with table-shaped queries, built before
  and independently of the change. Nobody has one, and §WP4 item 4 correctly forbids building one
  now.

**What is lost by cutting WP4/WP7:** a measured comparison of whole-table vs per-row vs per-cell vs
linearized chunking, and the ADR that records the losers with numbers. Acceptable because the plan
itself predicts the comparison will be unadjudicable, so the artifact lost is a record that says
"cannot adjudicate" — writable today at zero cost, from ADR-0058 and ADR-0072, as a note on the
FU-B issue.

---

### MAJOR-6 — WP6's telemetry has grown five metrics where the plan's own verdict recipe uses one

**Section:** §WP6 6c, gate C9.

The owner asked for the old-vs-new diff recorded as a metric, accepting the cost. The plan ships
five metric names, a `LegAvailability` record threaded to `SqliteMemoryStore.cs:259`, a
`FusionConfidenceOutcome` record carrying applied/skipped-with-reason, and a named sample size in
ADR-0077.

**The plan states its own verdict recipe in §6c:**

> "Partition real searches by `top1_changed`. Compare follow-through rate and mean
> `usefulness_grade` across the partition."

That recipe consumes exactly one of the five metrics. `topk_jaccard` and `max_rank_delta` are
descriptive statistics with no stated decision attached — nobody has said what value of
`max_rank_delta` would change anything. `applied` and `skipped` are derivable: a row's presence is
"applied", its absence is "not applied", and the skip reason is a tag.

**Smallest thing that produces evidence someone can act on:**

```
name  = "search.fusion.confidence"
value = 1 when the top-1 result changed, 0 when it did not
tags  = "skipped:<reason>" when the guard fired (no row value, or value recorded as -1)
correlation_id = the id MemoryTools.cs:161-164 already mints
```

One `IMeasurementRecorder.Record` call, one metric name, joinable to `search_quality` on
`correlation_id`, and it answers the exact question §6c poses. Gate C9 survives verbatim — the
correlation id is the whole value and the plan is right about that. Gates C2, C6, C7 survive.

**`LegAvailability` (§6a) should be its own issue, not part of this.** The plan says it "ships
regardless" and is "independently valuable" — both true, and both reasons it does not belong in a
#367 PR. Its only consumer inside WP6 is gate C8's guard. If WP5 returns G1 and WP6b does not ship,
`LegAvailability` ships as an observability record with no reader. Worse: bundled into PR4, it is
the first thing cut when PR4 is descoped, and a genuinely swallowed `SqliteException` at
`QueryFtsBatchAsync:826-849` stays invisible for another release. File it as its own defect
("the FTS leg degrades silently") and ship it on its own merits.

Without `LegAvailability`, C8's guard degrades to what is already observable at the fusion point:
`queryVector is null` (C6) and `VectorWeight/FtsWeight == 0` (C7). Those are the two causes that
actually fire in practice; a silently-degraded FTS leg is rare and, on the plan's own reading, the
heuristic's failure mode there is over-firing on a bank that is already returning bad results.

**What is lost:** the ability to say "the rule was skipped because FTS degraded" in the first
release of the flag. Acceptable if and only if the degradation issue is filed the same day.

---

### MAJOR-7 — WP5 is not a research package; it is one hypothesis the plan has already chosen

**Section:** §WP5, §5 "Can the confidence heuristic help #367…".

§WP5 lists three candidate rules, then says "**Lead with the third**". §5 goes further: "Yes in one
specific form, no in the general form", and explains why `max(rrf, best_single_leg)` is the only
form whose asymmetry makes it safe. The first two candidates are dismissed in the same breath they
are proposed ("would demote every query where the semantic leg carries the meaning — most of them
on this corpus").

A research record whose conclusion is written in the plan that commissions it is not research; it
is a design decision with a document attached. The `evidence-first-research` skill and the
`research-record-audit` gate cost real time and are aimed at open questions.

**Smaller:** delete WP5 as a package. Fold Q1 (absent ≠ low-ranked), Q2 (degradation enumeration)
and Q3 (the harm count on the 44-query corpus) into PR3's own gates — C5 already *is* Q1, C6/C7
already *are* Q2, and Q3 is a number you produce by running `ParityGateTests` with the flag forced
on, which is already gate C10. Everything WP5 was going to establish is established by gates PR3
must write anyway. The falsifiers G1/G2/G3 become the PR's ship/no-ship conditions and go into
ADR-0077 directly.

**What is lost:** a standalone record naming the two rejected rules with numbers, so nobody
re-proposes them. Acceptable: ADR-0077 is already required to record exactly that, and §WP6's ADR
brief already says it must ("must state plainly why shipping-behind-a-flag is honest"). Add one
paragraph to the ADR instead of a whole record.

---

### MAJOR-8 — the 5-PR split is 2 PRs of ceremony. Three is right.

**Section:** §4 "PR shape", §3 parallelism map.

Taking each seam:

| Seam | Verdict |
|---|---|
| PR1 (doctor) \| PR2 | **Keep.** Different issue, different subject, no shared risk. The `MemorySchema.cs` collision is trivial and §3 handles it. |
| PR2 (chunk_index) \| PR3 (reconciliation) | **Merge.** Both edit the same method in `FileIngestor.cs` (`InsertChunksAsync` → stamp position → `ReconcileChunksAsync`), both edit `MemorySql.cs`, and §3's shared-file table already calls them a "hard dependency — WP2 first". Splitting means the reviewer reads the same function twice and the intermediate state — stamp-at-insert without replacement — ships to users as a release that still accumulates duplicates. §4's stated reason for the split ("changes the ingest path for every bank and file type") applies verbatim to PR2. **The confound argument (R1) does not require the split:** the decision-point experiment measures the *repaired existing bank*, and WP3 changes only *future* ingests, so WP3 cannot move that measurement. |
| PR3 \| PR4 (fusion) | **Keep.** Genuinely orthogonal; §4 is right that "does the flag leak?" is only answerable in an undiluted diff, and R6/C1 are the sharpest gates in the plan. |
| PR4 \| PR5 (chunking) | **Moot** — PR5 is cut (MAJOR-5). |

Result: **PR1 doctor · PR2 chunk_index order + reconciliation · PR3 fusion floor.** Three PRs, three
reviewable questions: "does it read without writing?", "is chunk order right and does re-ingest
replace?", "does the flag leak?".

**Under-splitting? No.** Nothing in the reduced plan bundles two independent risks. The one place I
looked hard was PR2 combining a schema-shaped change with a behaviour change — but with
`source_state` cut (BLOCKER-2), PR2 has **no schema change at all**: no new table, no new column, no
`Ddl` edit, no statement-count re-pin, no `MemorySchema.cs` collision with PR1. It becomes a pure
behaviour change to three call sites plus a maintenance job. That is one coherent PR.

---

### MINOR-9 — the WP2 decision point has no branch that cancels anything

**Section:** §WP2 "The decision point this package creates".

| Rank | Plan's ruling |
|---|---|
| ≤ 5 | "**Re-scope** WP4/WP7 before building" |
| 6-14 | "WP4 proceeds" |
| ~18 | "WP4 proceeds as specified" |

Every branch proceeds. The strongest branch — H-c dominated, the target already fixed by a bug fix —
gets "re-scope", a word that has never cancelled a work package. An experiment whose every outcome
leads to the same next step is a checkpoint in name only, and it costs the schedule a serialisation
point (§3, "nothing else can honestly start until WP2 lands").

**Smaller:** rewrite the ≤5 row as "**#367's chunking half is closed. WP4/WP7 do not open. Record
the rank and the command in the PR2 description and close the issue.**" Under the reduced plan this
becomes the task's terminus (R4) rather than a mid-task gate, which removes the serialisation
pressure entirely.

---

### MINOR-10 — two numbers used inconsistently for the same quantity

**Section:** §WP4 F4; §6 R3; §5 "On the chunking arms".

F4 lists all three figures correctly: 5,799 (22.3%) contain a table line, 4,396 (16.9%) two or more,
2,370 (9.1%) a header separator. R3 then sizes the risk at "22% blast radius" and the §5 prior
reasons in the same neighbourhood. The 22.3% pipe-count also matches shell pipes inside fenced code
blocks, which are not tables. **9.1% is the defensible figure for "chunks containing an actual
markdown table"**, and every scope, cost and blast-radius argument in the plan should use it. Using
22.3% inflates the chunking package's addressable population by 2.4×.

---

## Speculative generality — built for a caller that does not exist

| # | Thing | Second caller | Section |
|---|---|---|---|
| 1 | `IBankCheck` + `IEnumerable<IBankCheck>` DI | none — gate A7 must register a **fake** one | §WP1 "A thin frame" |
| 2 | `chunker_version` column | `StaleSourceSweepJob` only, which serves WP7 only, which the plan predicts won't ship | §WP2, §WP3 |
| 3 | `StaleSourceSweep.cs` + `StaleSourceSweepJob` | a chunker change that does not exist | §WP3 files |
| 4 | `source_state` as a **table** | a container for (1 speculative member + 1 member that belongs in `chunk_index`) | §WP2 |
| 5 | `LegAvailability` record with a reason | gate C8, inside the half of WP6 that may not ship | §WP6 6a |
| 6 | `FusionConfidenceOutcome` (applied / skipped-with-reason) | one metric tag — a record shape for a string | §WP6 6b |
| 7 | `topk_jaccard`, `max_rank_delta`, `applied`, `skipped` metrics | none; §6c's own verdict recipe reads only `top1_changed` | §WP6 6c |
| 8 | WP4 cells C, E, F, D′ | a decision the plan says the corpus cannot make | §WP4 grid |
| 9 | ADR-0078 + PR5 | conditional on 8, which is conditional on nothing shipping | §WP7, §4 |

**Correctly declined by the plan, and worth keeping declined:** `--json` on `doctor`; a repair path
on `doctor`; a `document_ordinal`/`line_span` column; a `SchemaShape` DSL or severity ladder; a new
table for fusion metrics; a separate shadow-mode flag; the full 20-cell cross-product. §5's
"would not build" list is the best section in the plan — this lane's argument is that it stopped
seven items too early.

---

## Net effect of the reduced plan

| | Plan | Reduced |
|---|---|---|
| Work packages | 7 | 4 |
| PRs | 5 | 3 |
| New tables | 1 | 0 |
| Schema changes | 1 (+ `Ddl` re-pin, + a `MemorySchema.cs` merge collision) | 0 |
| New hot-path joins | 3 (unstated) | 0 |
| Core record changes | 1 (`MemorySearchResult`) | 0 |
| New interfaces | 2 (`IBankCheck`, `LegAvailability`) | 0 |
| Research records | 2 | 0 |
| ADRs | 2 | 1 (0077) |
| Metric names | 5 | 1 |
| Full-bank re-embeds | 8 (WP4 grid) + 1 (WP7 sweep) | 0 |
| Issues closed | #357, #371, #367 | #357, #371, #367 |

Every issue still closes. #367 closes on: #371's ordering fix (H-c), the reconciliation that stops
duplicate near-copies competing, the fusion floor that enforces ADR-0006:49-50 (H-d), and a recorded
measurement of what is left — with H-a and H-b handed to a follow-up carrying real evidence instead
of an unadjudicable grid.

---

## Still open

1. **Does the `chunk_index = -1` sentinel survive contact with anything I did not read?** I checked
   every `ChunkIndex` reader in `src/` (`SourceAffinityRanker.cs:55,77,113`,
   `SqliteMemoryStore.cs:856,923`, `SqliteMemoryStore.Rows.cs:46,74`, the three `MemorySql` SELECTs).
   I did not check `SyncService`, the MCP tool response shaping, or the CLI output paths for a
   consumer that would render `-1` to a user. If one exists, the fix is a display guard, not a table.
2. **The 1,143-row `-1` UPDATE and `total_chunks`.** I have not decided whether those rows should
   keep their current `total_chunks` or be zeroed. Keeping it is a lie of the same class as
   `chunk_index`; zeroing it may affect display. Someone should rule.
3. **Whether PR2-merged is too large in practice.** I argued the seam is artificial, but I have not
   counted the diff. If `FileIngestor` + `MemorySql` + `SqliteMemoryStore` + `SourceAffinityRanker`
   + `SyncService` + `ChunkBackfill` + the repair job exceeds what one review can hold, the split
   is defensible on size alone — just not on the reasons §4 gives.
4. **Whether the owner's "option B with A" ruling is satisfied by the reduced plan.** §WP2 reads the
   ruling as requiring a version marker. I argue the marker is derivable from hashes and needed only
   when a chunker change ships. **This is a direct reinterpretation of an owner ruling and needs the
   owner, not this lane.**
5. **What FTS-leg degradation actually costs today.** I asserted it is rare without measuring it.
   If `QueryFtsBatchAsync:826-849` fires often, `LegAvailability` moves from "own issue" to
   "urgent", and MAJOR-6's cut is wrong.
6. **Whether ADR-0048's "unbuilt, not broken" framing means header carry-over is a defect or a
   feature.** I treat it as a property violation needing no adjudication. A reader of ADR-0048 who
   thinks it was a deliberate deferral with a cost/benefit attached would put it back in FU-B.
