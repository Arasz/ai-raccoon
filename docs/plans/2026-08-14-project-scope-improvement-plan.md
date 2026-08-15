# Improvement plan — project-scope review, 2026-08-14

Source review: [`docs/reviews/2026-08-14-project-scope-review.md`](../reviews/2026-08-14-project-scope-review.md) ·
Base commit `1d1889d517baf840df0b839f547091bd7f46808b` · Campaign branch
`campaign/project-scope-review-0814` · PR #290

Packages are grouped **by surface**, so everything touching one file lands in one change. Every
package carries acceptance criteria **and a gate that must be watched go red before the fix**. A gate
that has only ever passed is not a gate.

> **Status of the evidence.** Packages WP1–WP3 rest on claims that went through an independent
> adversarial falsification pass. Where that pass corrected or refuted a supporting number, the
> package text says so inline. No package is justified by an in-sample metric; where the only
> available number is in-sample it is labelled and the package is scoped to not depend on it.

---

## Sequencing rules for this campaign

**Serialisation points — files several packages edit, which cannot be parallelised however
independent the packages read:**

| File | Packages |
|---|---|
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (1,291 lines) | WP1 (`FilterFor`), WP4 (`SearchResultMerger` call), WP6 (`BumpAccessAsync`), WP9 (`ISettingsStore`) |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` (1,225 lines) | WP5 (v9 vec0 ladder step), WP7 (reapers) |
| `src/AiRaccoon/Tools/MemoryTools.cs` | WP2 (descriptions), WP8 (query-guard extraction), WP10 (`LogWarning`) |
| `src/AiRaccoon.Core/Memory/IMemoryStore.cs` | WP9 (settings members, DIM removal) |

**The measurement chain runs backwards from what must be proved.** Three orderings are load-bearing:

1. **The metrics gate must become non-vacuous before any ranking change is measured.** WP4 (the
   `ranking` field) and WP12 (structure fusion) both change ranking. Today the headline gate asserts
   only that nDCG/MRR/recall lie in `[0,1]`, so it would report success for either. **WP11 (held-out
   gate) precedes WP4 and WP12.** Getting this backwards produces numbers nobody can use.
2. **The servers must be restarted before the backfill, and the backfill before any corpus
   measurement.** WP3's backfill re-chunks the bank; if a pre-fix process is still writing, it
   re-introduces oversized chunks behind the backfill. And any ranking number taken before the
   backfill is measured on a different corpus than the one taken after. Order: **restart → backfill →
   measure**.
3. **WP1 extracts the shared confinement helper; WP2 then only wires one more branch into it.**
   *(Corrected in rev 2 — rev 1 said these must ship as one change, and on re-examination that is
   wrong.)* They sit on different axes: WP1 is `project:` confinement plus refusing `shared` **on the
   delete path**; WP2 is refusing `shared` **on the write path**, which is an owner decision (question
   2). What must not happen is a patch to `FilterFor` alone — that would leave the concept split across
   two functions again, which is exactly how the delete-path hole survived commit `7698dc63`'s fix to
   the write path. So WP1 lands the **one shared helper both call sites use**, and WP2 becomes a
   one-branch addition to it rather than a second implementation. WP1 is unblocked and can ship alone.

**What is *not* a serialisation point:** WP11's held-out gate is built from the committed test corpus,
not the live bank, so it can be built in parallel with WP3's backfill.

---

## Wave 1 — The delete path · one PR · release as 1.13.0

### WP1 · Confine `memory_delete_context` to the caller's project — **BLOCKER B1** — ✅ **LANDED**
**Effort:** SMALL · **Surface:** `SqliteMemoryStore.FilterFor`, `EntryBucket.For`
**Landed** on `work/wp1-delete-confinement`, merged into the campaign branch. ADR-0051.
Both negative gates were watched go red ("should throw `ContextOutsideProjectException` but did not")
with the positive case passing throughout. The size ratchet caught the guard at 1298 against a 1291
cap and, per its own note, the **delete seam came out** rather than the cap going up — `FilterFor`
moved to `ContextFilter.cs` beside `EntryBucket`, and the cap was **lowered to 1251**.

`FilterFor`'s `project:` branch binds `["projectId"] = context["project:".Length..]`, discarding the
caller's `projectId`; its `shared` branch returns `scope = 'shared'` with no project predicate and an
empty parameter dictionary. The `workspace:` and `label:` branches **both** bind the caller's
`projectId` — that asymmetry is the evidence this is a bug rather than a design.

**Change (as landed):** extracted the confinement check into `ContextScope.RequireWithinProject`, and
call it from both `EntryBucket.For` and `DeleteContextAsync`. `FilterFor`'s `project:` branch binds the caller's `projectId` and throws
`ContextOutsideProjectException` when the context names a different one. Refuse `shared` on the delete
path.

**Acceptance criteria**
- `memory_delete_context(projectId: A, context: "project:B")` is refused, not executed, at every access mode including `full`.
- `memory_delete_context(projectId: A, context: "shared")` is refused.
- The existing legitimate uses — deleting one's own project context, a workspace context, a label context — still work.
- Exactly one function decides project confinement, and both call sites use it.

**Gate — watch it go red first.** A new test creating two projects, writing to `victim`, setting
`access.mode.global = full`, then calling `memory_delete_context(projectId: "attacker", context:
"project:victim")` and asserting **zero rows deleted and a refusal**. On today's code this test must
report rows deleted — run it before the fix and record the count in the commit message. A second case
does the same for `context: "shared"`.

**Also add the derived guard** that would have caught it: a test enumerating every `[McpServerTool]`
method by reflection and asserting each one's guard requirement, mirroring the existing
`EveryTool_NamesTheProjectIdParameter`. Break it with a stub tool that omits the gate and watch it go
red.

### WP2 · Treat a write naming `shared` as a promotion request, not as a write — ✅ **IN REVIEW** — **H6**

> **Shipped as ADR-0067**, immediately after WP8 as the plan recommended. The cycle really was the
> argument for the extraction rather than an obstacle: `MemoryWriteService` composes `IMemoryStore`
> and `IPromotionQueue` from Core, which the store itself cannot. Watched red on the pre-fix path
> (`Expected: 0 rows WHERE scope = 'shared'`). The rejected one-line `EntryBucket` variant is
> recorded with its reason.
**Effort:** SMALL · **Surface:** `EntryBucket.For`, `SqliteMemoryStore.WriteAsync`, `IPromotionQueue.ProposeAsync`
**Owner ruling, 2026-08-15 — this package was redesigned, not withdrawn.**

Rev 1 proposed *refusing* `memory_write(context: "shared")`. The owner's steer is better and this
package now follows it: **an agent naming `shared` is asking for the row to be promoted, and that is
the strongest promotion signal available** — far better than a scorer inferring it. Refusing throws
that signal away; writing it straight through skips the review the shared tier exists to have.

**First, the factual correction the design rests on.** The owner asked whether `shared` is "just a
context — a label inside the project". Checked against the code and the live bank: **no, and it is the
only context string for which the answer is no.**

- `SearchContexts.For` with `scope=all` (the default) already adds the shared context **plus** the project context **plus** every custom label in the project. *Both are already searchable by default; nothing needs changing there.*
- A **label** is `scope='custom'` with `context_label` set — 12 rows in the live bank. ADR-0045's own doc comment states the rule: "the project is the isolation boundary; a context is a label inside it, not a second boundary."
- **`shared` is a distinct scope, not a label** — 138 rows across 5 projects, each retaining its originating `project_id`, and `context_label` NULL on every one. It is the one context that *crosses* the project boundary, which is precisely why it was reachable at the default `rw` mode without review.

**Change:** `memory_write(context: "shared")` writes the row into the **caller's project scope** and
enqueues a promotion candidate via `IPromotionQueue.ProposeAsync` with `Reasons` carrying
`agent-requested-share`. `QueueCandidate` already has a `Reasons` list, so this is a first-class
reason rather than a special case. The response says what happened — stored, and queued for promotion.

**Acceptance criteria**
- `memory_write(context: "shared")` at default `rw` creates **no** `scope='shared'` row.
- It creates one project-scoped row **and** one `promotion_queue` row for that hash, carrying the `agent-requested-share` reason.
- The write is not lost and the agent can find it immediately by ordinary project search.
- `memory_share` / `memory_share_extract` still reach the shared tier directly — the review path is unchanged.
- An agent-requested candidate is distinguishable in `memory_promotion_list` from a scorer-proposed one.

**Gate — watch it go red first.** A test writing with `context: "shared"` at default mode, asserting
`COUNT(*) WHERE scope='shared'` is unchanged **and** a queue row exists with the reason. Today the
shared row lands and no queue row is created — the adversarial pass demonstrated exactly that
(`{"context":"shared","stored":true}`, `memory_promotion_list` → `{"rows":[]}`). Record both before the
fix.

**Open sub-question for the owner, not blocking:** what `Score` an agent-requested candidate gets. It
should outrank scorer-proposed candidates, since an explicit request beats an inference — but whether
it should bypass the queue's capacity eviction entirely is a policy call. **Decided for now:** score it
above the scorer's range but leave eviction untouched, which is reversible and cannot silently drop the
request without it showing up in the queue's own metrics.

---

**Implementation blocked on a dependency cycle — found while starting it, 2026-08-15.**

The enqueue cannot live in `SqliteMemoryStore`. `PromotionQueueService` takes `IMemoryStore`
(`src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:14`), so injecting `IPromotionQueue`
into the store is **store → queue → store** — a genuine cycle at singleton scope, resolvable only by a
lazy/service-locator closure, which is exactly the smell the architecture lane filed as F15. And the
store is sitting at **exactly** its 1251-line ratchet cap, whose own note names `write` as one of the
four remaining seams.

So the enqueue needs a Core service composing the store and the queue — which is the same extraction
**WP8** performs for `ShareTools` and `MemoryTools`' query guard. **That makes WP2 MEDIUM, not SMALL,
and binds it to WP8.**

**A smaller WP2a was considered and rejected.** Mapping `shared` to the project scope in
`EntryBucket.For` alone is one line and closes the boundary crossing with no cycle — but it changes
behaviour silently unless `WriteResult.Reason` says so, and setting that reason means growing
`WriteAsync`, which the ratchet refuses. Landing the one line without the reason would leave an agent's
promotion request dropped with no signal — the exact failure mode this campaign exists to find. Not
worth trading one silent behaviour for another to save a wave.

**Recommendation:** sequence WP2 immediately after WP8's write-seam extraction, and treat the cycle as
the argument for that extraction rather than an obstacle to this package.

### WP3 · Make every write path budget its chunks, then restart and backfill — 🔶 **STEPS 1-2 IN REVIEW · 3-5 FOR THE OWNER** — **BLOCKERS B2 + B3, H5, H24**

> **Step 2 shipped as ADR-0063.** An unset `embedding.provider` now resolves to the bundled engine
> instead of the o200k default, so ingest-then-configure stops writing permanently-truncated
> boundaries. Watched red at **23 of 37 chunks over-window, worst 295 tokens**.
>
> **Step 1 shipped as ADR-0064.** `memory_write` now routes through the same chunker and budget
> resolution as file ingest. Watched red at a 29,200-character body storing as **1 row of 8,123
> tokens** into a 256-token window — ~97% of that note absent from its own vector. The size ratchet
> caught the growth and, per its own message, the seam came out rather than the cap going up:
> `SqliteMemoryStore.cs` 1283 → **1238**, ratchet **lowered** 1243 → 1238. It also surfaced a latent
> bug of its own: the post-insert lookup was `WHERE path = @path … LIMIT 1` with no `ORDER BY`,
> unspecified the moment a path meant several rows. **Steps 3-5 — restarting
> every running process and backfilling 1,217 documents — mutate the live 167 MB bank, are not
> revertible by git, and are deliberately left to be sequenced by hand.** Shipping the code fixes
> first is the right order regardless: backfilling before `memory_write` chunks would re-poison the
> bank on the next write.
**Effort:** MEDIUM–LARGE · **Surface:** `MemoryTools.Write` → `SqliteMemoryStore.WriteAsync`, `FileIngestor.ChunkSizeForAsync`, plus an operational restart and a backfill

> **Revised after the adversarial pass.** The first draft of this package was "restart the servers,
> then backfill" and that was **incomplete**. Two live defects remain in the code running right now,
> and no restart touches either.

**Three distinct causes, established separately:**

1. **Stale processes.** The binary was installed at **22:24:36** — not 22:10:24, which matches nothing on disk — and three long-lived `--quiet` processes predate it. A fourth (`82875`, 22:24:47) is genuinely post-update.
2. **`memory_write` does not chunk at all.** Measured on the HEAD build: a 9,360-character body stores as **one row**, `embed_state='embedded'`, embedded from its first 254 WordPiece tokens — ~85% of the content absent from its vector. Live cost: **555 rows, 320 of them over-window (57.7%), 114,883 tokens never embedded.** The only budget-aware chunking in the codebase lives in `FileIngestor`.
3. **The ingest budget is silently non-engine-aware when `embedding.provider` is unset.** `FileIngestor.ChunkSizeForAsync:199-216` returns `DefaultMaxTokens` with the o200k counter when the setting is absent, and reaches the 254-token BERT budget only when it is present: **104/258 over-window without it, 0/276 with it.** Setting the engine later re-embeds (`EmbeddingService.EngineFingerprint`) but **does not re-chunk**, so ingest-then-configure — a supported order — permanently poisons whatever was already there.

**Change, in this order:**

1. **Route `memory_write` through the same budgeted chunker `FileIngestor` uses.** This is the code fix, and it is the part with no operational workaround.
2. **Make the ingest budget engine-aware unconditionally** — resolve the window from the engine that will actually embed, or refuse to ingest until a provider is configured. Silently falling back to a counter that does not match the model is the defect.
3. **Restart every running `ai-raccoon` process**; confirm `ai-raccoon --version` and process start times against the binary's mtime.
4. **Backfill** rows whose WordPiece length exceeds the window, re-chunking and re-embedding. `EntryEmbedder` re-embeds stored text unchanged and never re-chunks, so this must go through the chunker, not the embedder.
5. **A startup log line** recording the running assembly version against the bank's engine fingerprint, so this class of drift is visible rather than requiring `ps`.

**Acceptance criteria**
- A `memory_write` of a 10,000-character body produces **multiple rows**, none over 254 content tokens.
- Ingest into a bank with no configured provider either produces in-budget chunks or refuses — it never silently uses a mismatched counter.
- EventId 414 count is **zero** over a day of normal ingest after the restart. (The prior campaign's WP7 said it "should be provably zero"; this is the package that makes that true — note that `quiet.log` currently contains **zero** 414 lines only because the running code predates the counter.)
- Post-backfill, the count of entries over 254 WordPiece tokens is zero.
- No text is lost — `SUM(length(value))` is unchanged or larger across the backfill.

**Gate — watch it go red first.** Two of them, because there are two code defects:
- A test that writes a 10,000-character body through `memory_write` and asserts every resulting row is within budget. **Today it produces one oversized row — record the token count before the fix.**
- A test that ingests a document into a bank with no `embedding.provider` and asserts zero over-window chunks. **Today it produces 104 of 258 — record that.**

Only then the operational gate: ingest a document known to overflow under the *old* budget and assert the EventId 414 counter stays at zero.

**Scope decided — whole bank (owner ruling, 2026-08-15).** The owner's rule was "if it makes a
difference for memory retrieval, whole; if not, the more performance-friendly solution." Measured, and
the choice dissolves: **chunking is a document-level operation.** A single chunk cannot be re-chunked
in isolation — re-chunking re-derives every boundary in its document. So the narrow option is not
"~6,900 rows", it is "every document containing one of them":

| | |
|---|---|
| Distinct source documents in the bank | 1,217 |
| Documents holding at least one over-window row | **1,004 (82.5%)** |
| Rows in those documents | **14,979 of 16,152 (92.7%)** |

The performance-friendly option therefore saves **7.3%** and leaves the corpus carrying two chunk
generations — some documents on pre-fix boundaries, some on post-fix ones — which is its own retrieval
hazard and a confound for WP11's gate. It is not meaningfully cheaper, so on the owner's own rule the
answer is the whole bank.

**Two row classes need different handling, and the narrow/whole axis does not cover the second:**
- **Document rows** (16,152 − 56 with a `source_file`): re-chunk the document, re-embed its chunks.
- **`memory_write` rows** — 56 with no `source_file` at all, plus 130 oversized rows with `total_chunks <= 1`. These are not documents and cannot be "re-chunked" as one. They need the WP3 code fix first, then their stored `value` treated as its own document.

---

## Wave 2 — Make the measurements honest before changing what they measure

### WP11 · A retrieval gate that can fail, on a held-out family — ✅ **IN REVIEW** — **H2 + H3**
**Effort:** MEDIUM · **Surface:** `tests/…/Integration/BaselineMetricsTests.cs`, `RrfParameterSweepTests.cs`, `scripts/baseline-queries.json`
**Precedes WP4 and WP12.**

> **Built as ADR-0056, with one correction to this package.** The prescribed
> leave-one-family-out partition is **not available on this corpus**: `jsaa-memory.db` has two
> generators (`docs/`, 112 files; `.ai-badger/`, 78) and no arasz-home-page, and the 11-query
> tuning set spans both — no family is unseen. The partition is taken at the **document** instead,
> which leaves **A8, A9, A10** held out; `S1`/`S3`/`S4`/`S5`/`S6` are *not*, because `S2` and `A4`
> tuned on their documents. Measured on one path: **in-sample 0.673, held out 0.285.** The
> reversal perturbation this package demanded works on the mean but not per query — reversing the
> ranking *improves* A8 (0.131 → 0.491) — so the per-query floors are regression pins and the mean
> is the gate. A gate fails the day a genuinely unseen family appears, which is the signal to
> promote this to the family-level control originally asked for.

`BaselineMetricsTests.cs:107-112` asserts only `ShouldBeInRange(0.0, 1.0)` on nDCG@5, MRR and
recall@5 — values those functions return by construction for any input. The file concedes it: "logged
as a data point, not asserted." Adjacent gates require `>= 1` of 19 graded queries to hit at rank ≤ 3,
which passes with 18/19 misses.

Separately, the same **11 queries** both select the RRF parameters (ADR-0006's 96-point grid) and gate
them (`RrfGateQueryIds`, `SourceAffinityGateQueryIds`). Every published nDCG figure is in-sample.

**Change:** partition the corpus by **what generated each document** (jsaa / ai-badger /
arasz-home-page), tune on some families, gate on the held-out ones. Replace the range assertions with
a pinned per-query floor measured on the held-out set.

**Acceptance criteria**
- No gate asserts only a range.
- The pinned floor is a held-out number, labelled as such, with the family partition recorded.
- Every surviving in-sample number in `docs/adr/0006-*.md` is labelled in-sample.

**Gate — watch it go red first.** Perturb the ranker deliberately (e.g. reverse the fused order) and
confirm the new gate fails. Today's gate passes under that perturbation — demonstrate that first, in
the commit message. This is the single most important red-first demonstration in the plan, because
every ranking package downstream is measured by it.

### WP4 · Stop fusing an already-fused list — ⛔ **BLOCKED ON HELD-OUT CAPACITY** — **H1**
**Effort:** QUICK · **Surface:** `SearchResultMerger.cs:26`, `SqliteMemoryStore.cs:266-273`
**Follows WP11.**

> **Owner question 7 answered — "can we do both?" — and the blocker turned out to be the corpus,
> not the ruling (ADR-0058).** Built and measured four ways. The decisive fact: **at λ=0 the code
> with and without the second fusion is byte-identical** (held-out 0.269842, in-sample 0.652739),
> because `(k+1)/(k+rank)` is strictly decreasing in rank, so re-fusing a sorted list preserves
> order. The pass has never moved a result. Its only effect is compressing the score range that
> ADR-0005's sibling-visibility floor and consolidation gap were swept against, so deleting it
> silently re-scales two tuned constants: held-out **0.2846 → 0.2571** alone, **→ 0.3333** with the
> sibling floor restated as its equivalent rank window (`0.1` below max ⇒ top ~7.78 ranks —
> arithmetic, not a re-tune), **→ 0.2818** with both constants restated. The derivation applies to
> **both**, and that row is inconclusive against today; selecting the 0.3333 row would be tuning on a
> 3-query held-out set. Ships three characterisation tests that pin the defect and go red when it is
> fixed. **Unblocked by:** enough held-out queries that a ±0.03 mean is a result rather than noise.

`SearchResultMerger.Merge` re-runs `ReciprocalRankFusion.Fuse` on a single, already-fused list,
rebuilding every score from rank position and discarding the fused modality scores. A strong match set
and near-orthogonal junk produce identical score curves.

**Corrected by the adversarial pass — the closed form is false in the dominant case.**
`SearchQuery.cs:16` declares `double SourceLambda = 0.1`, so `SourceAffinityRanker.Rank` does **not**
short-circuit: it adds a sibling boost, **re-orders**, **drops** rows via `Consolidate`, and
**re-normalises**. Measured on the current build with ingested chunks: `1, 0.9487, 0.9369, 0.9254`
against the pure formula's `1, 0.9839, 0.9683, 0.9531` — output positions 2-4 are *fused* positions 5,
6, 7. The true value is `(rankBase + 0.1 × adjacentSiblings) / max`, reducing to the closed form only
for rows with no siblings (bare `memory_write` rows). **The substance stands** — the field carries rank
position plus a structural adjacency term, and no match-quality signal — but the package must not
claim the second pass is a no-op on ordering. **It is not, and removing it will change results.**

**Change:** have `Merge` take the already-fused list and apply `SourceAffinityRanker`, the floor and
the limit directly, without re-entering `ReciprocalRankFusion`.

**Acceptance criteria**
- `ranking` varies with match quality, not only with position plus adjacency.
- ~~Result ordering is unchanged by this package alone~~ — **withdrawn.** The adversarial pass showed the second pass already re-orders and drops rows via `SourceAffinityRanker`, so ordering *will* change. The held-out gate from WP11 is what says whether it improved; there is no "safe because it only touches the score" framing available here.
- `minRelativeScore` still behaves as documented — note that its effect is defined against `rankBase`, so changing what `ranking` means changes what the floor filters.

**Gate — watch it go red first.** A test asserting that a strong candidate set and a junk candidate set
produce **different** `ranking` values at the same rank. Today they are byte-identical; record the two
identical curves before the fix.

*Owner question 7 decides whether `ranking` should carry a real score at all. If rank-order is the
intended contract, this package becomes "delete the redundant second fusion and document that `ranking`
is positional", which is cheaper and still worth doing.*

### WP12 · Stop penalising content that has no heading — ❌ **REFUTED BY MEASUREMENT** — **H4**
**Effort:** QUICK · **Surface:** `StructureFusion.cs:23-28,52-56`
**Follows WP11. Found independently by two lanes.**

> **Built, measured, rejected — ADR-0057.** The finding is correct and the fix makes retrieval worse.
> The gate corpus was checked as representative first (65.4% headless, against the live bank's 64%).
> Scoring absent structure as content-only regresses **S3 3→4, S4 3→6, S6 3→10, A2 1→2** and held-out
> **A10 0.1696→0.1461**, leaves the held-out mean flat (0.2846→0.2818), and **inverts WP11's reversal
> probe**: reversed 0.610 against 0.282 unreversed, i.e. the held-out ordering becomes anti-correlated
> with relevance. The `?? 0.0` cap is the mechanism by which the dual-vector signal favours headed
> chunks at all — remove it and a headed row wins only when `structureSim > contentSim`. `StructureFusion`
> is unchanged; what ships is the adjudication and a gate pinning the property. A real fix is per-row
> **renormalisation**, which is a new parameter needing held-out capacity the catalog does not have.

`Fused = alpha * contentSim + (1-alpha) * (structureSim ?? 0.0)` with `alpha = 0.5`. A row with no
`structure_embedding` never appears in the structure KNN list, so `structureSim` is **absent**, not
low — and defaulting it to `0.0` caps that row at half the score a headed row can reach. 64% of the
live bank has no structure embedding, by design (`EmbedIfConfiguredAsync` only computes one when a
heading parses).

**Change:** when `structureSim` is absent, score on content alone — degrade to content-only **per row**,
which is what the class comment already claims happens per bank.

**Acceptance criteria**
- A headless row and a headed row with equal content similarity score equally.
- The held-out gate from WP11 does not regress.

**Gate — watch it go red first.** A test with two entries of equal content similarity, one with a
heading and one without, asserting equal fused scores. Today the headless one scores half; record both
numbers.

---

## Wave 3 — Storage, retention and the boundary the container dissolved

### WP5 · Reclaim the vec0 chunk waste — and it is the **partition key**, not `chunk_size` — ✅ **IN REVIEW** (ladder step v9, ADR-0068) — **H11**
**Effort:** MEDIUM · **Surface:** `MemorySchema.Ddl`, `RebuildVecTableAsync`, a new **v9** ladder step

> **Revised after the adversarial pass. The first draft of this package would have shipped, passed its
> gate, and reclaimed about 2% of what it promised.**

The waste reproduces to the byte: **43,424,256 B ≈ 43.4 MB**, recomputed from `dbstat` page bytes
(`vec_entries_vector_chunks00` 56,713,216 allocated vs 24,806,400 needed;
`vec_structure_vector_chunks00` 20,480,000 vs 8,962,560). Chunks are genuinely fixed-capacity — the
`size` column is 1024 on every row and each blob is exactly 1,572,864 B (1024 × 384 × 4) whether the
chunk is full or nearly empty — so the arithmetic holds, measured rather than assumed.

**But the cause is not the 1024 default.** Without the `ctx` partition key the same 21,985 vectors need
**22 chunks rather than 49**, so **~42.6 MB — 98% — of the waste is attributable to partitioning**, at
20 distinct `ctx` values of which **13** (not 14) hold under 10 rows. Pinning `chunk_size` alone
recovers almost nothing.

> **MEASURED 2026-08-15 — and it inverts this ordering. Option 2 should be struck.**
>
> `Vec0PartitionKeyProbe` (run with `AIRACCOON_VEC0_PARTITION_PROBE=1`), 2,518 real vectors from the
> committed corpus under a partition distribution synthesised to the live bank's measured shape
> (20 ctx values, 13 of them small):
>
> | shape | chunks | chunk bytes | KNN @ k=10 |
> |---|---|---|---|
> | `ctx`-partitioned (today) | 20 | 31,457,280 | **0.343 ms** |
> | `scope`-partitioned (option 2) | 3 | 4,718,592 | **2.301 ms** |
> | unpartitioned — *not a correct replacement* | 3 | 4,718,592 | 2.274 ms |
> | **`ctx` as a metadata column (option 1, corrected)** | **3** | **4,718,592** | **1.734 ms** |
>
> **CORRECTION, same day.** The first run measured an *unpartitioned* table with no `ctx` at all, and
> queried it without a context filter — so it measured a KNN that returns the **global** top-k and is
> not correctly scoped. That is not a replacement for today's behaviour, and its 1.952 ms was the
> latency of a wrong query. The shape a correct replacement needs keeps `ctx` as a vec0 **metadata
> column** — filterable, just not a partition key. It measures **1.734 ms** at the same 4.7 MB, so the
> conclusion is unchanged and slightly better than reported: **"drop the partition key" means demote
> `ctx` to a metadata column, not remove it.**
>
> **The size win is real: 85% of the chunk bytes, 31.5 MB → 4.7 MB.** The latency cost is also real:
> **5.1× for option 1 (corrected), 6.7× for option 2.**
>
> **Option 2 measures worse than option 1 on latency at identical size, so it is not a middle ground —
> it is dominated.** Coarsening still pays the partition-filter machinery while pruning nothing once
> every row shares a scope. Strike it; the choice is between today's shape and dropping the key.
>
> **What this does not settle**, stated so nobody quotes it further than it goes: the partition
> distribution is synthetic (the vectors are real, the `ctx` assignment is not), it is one corpus at
> 2,518 vectors against a live bank of ~16k, and `k=10`. Whether the 1.6 ms gap grows or shrinks with
> corpus size is unmeasured. **The trade is now explicit — roughly 26.7 MB against +1.4 ms per KNN on
> the search hot path — and that is an owner decision, not a technical one.**
>
> **OWNER RULING, 2026-08-15: option 1 — drop the `ctx` partition key.** The measurement above is
> what it was decided on: ~26.7 MB against +1.6 ms per KNN, with option 2 struck as dominated. The v9
> migration is now a mechanical package with its shape fixed; its acceptance criteria are unchanged
> (KNN results identical against the pre-migration bank, idempotent on an already-migrated bank, and
> the recovered figure stated against the measured number rather than a projection).
>
> **The +1.6 ms is also the argument for `docs/plans/2026-08-15-performance-observability-design.md`:**
> nothing in the store times its own phases today, so this change's effect on a real bank would be
> invisible after it ships.
>
> **So the real options are, in order of preference:**
1. **Drop the `ctx` partition key and filter by context in the query instead.** Requires proving the KNN stays correctly scoped and measuring the latency cost — partitioning exists to prune before the `MATCH`, and `EXPLAIN QUERY PLAN` currently shows that pruning working. **Measure before choosing.**
2. **Coarsen the partition key** — partition by `scope` rather than by full project/label/workspace identity, cutting 20 partitions to 3 or 4 while keeping most pruning.
3. **Pin `chunk_size` as well**, worth roughly 0.8 MB on its own; only meaningful once 1 or 2 has landed.

**The ladder is append-only.** Add a **v9** step that rebuilds both vec0 tables under the new shape,
sourcing from `entries.embedding`/`structure_embedding` — which is exactly why those columns are not
deletable (see *Explicitly not doing*). Never renumber or delete an existing step.

**Acceptance criteria**
- The chosen shape is justified by a **measured** latency comparison, not by the size number alone.
- Bank size recorded before and after; the recovered figure is stated against the measured 43.4 MB, not against a projection.
- KNN results are unchanged — same top-k for a fixed query set, verified against the pre-migration bank.
- The migration is idempotent on an already-migrated bank.

**Gate — watch it go red first.** A fixture with many small partitions where the pre-migration
allocated-bytes figure is asserted, then the post-migration one. Break it by reverting the partition
change and watch the size assertion fail. **Also assert KNN equivalence** — a size gate alone would
pass a migration that silently broke scoping. Note the bank is now **172.1 MB**, not the 159 MB first
recorded; it grew ~22 MB during the review window.

### WP6 · Compute `rating` in SQL instead of losing updates — ✅ **LANDED (ADR-0053)** — **data F7**

> **Verified against `src/`, 2026-08-15.** `MemorySql.BumpAccess` is one `UPDATE` computing `rating`
> from `access_count + 1` in the same statement, so the read-then-write is gone. Gate:
> `tests/AiRaccoon.Tests/Integration/Storage/RatingBumpConsistencyTests.cs`, 4 tests, and ADR-0053
> records it was watched red first. This section read as unstarted while the work was complete —
> it was found by branching to implement it.
**Effort:** QUICK · **Surface:** `BumpAccessAsync`, `MemorySql.BumpAccess`

The read-then-write is two round trips with no transaction; `access_count` is immune (relative SQL
expression) but `rating`, computed client-side from a stale read, loses updates under concurrent hits
on one hash — and `rating` feeds sweep eligibility.

**Gate — watch it go red first.** The lane's interleaving reproduction, as a test: two connections,
both read, both write; assert `rating` reflects the final `access_count`. Today it does not; record the
two values.

### WP7 · Reap `promotion_discards` and `search_quality` — ✅ **LANDED (PR #295)** — **H12 + data F6**
**Effort:** SMALL · **Surface:** `BankMaintenanceHostedService.RunPassAsync`, `PromotionQueueSql`

965 discard rows against 19 queued and 138 shared entries, with no delete statement anywhere.
`search_quality` gets a row per search, forever, and already has an
`idx_sq_project_time (project_id, created_at)` index built for a purge query that is never issued.
`noise_entries` already has a working reaper in this exact file — generalise it.

*Retention windows are owner question 18.*

### WP8 · Move the business logic out of the MCP layer — ✅ **IN REVIEW** — **H22**

> **Shipped as ADR-0065.** A size gate over `[McpServerTool]` bodies was watched red naming both
> offenders — `ShareExtract = 51 lines`, `Search = 34` — then `ShareExtractService` and
> `QueryGuardService` moved to Core. Core carries **no logging dependency**, so the guard returns a
> `Shadowed` verdict for the host to log rather than logging inside Core; event id 920 stays in
> `MemoryTools` because the id names the event, not the file. `confirm-required` became a domain
> exception mapped to the same wire prefix, and four tests that asserted `McpException` were
> re-pointed at `ToolRefusals.PrefixFor` — they were asserting the mechanism, not the contract.
> **This unblocks WP2.**
**Effort:** MEDIUM · **Surface:** `ShareTools.cs:43-118`, `MemoryTools.cs:170-222`

`memory_share_extract` is 62 body lines against a median of 9 — a consent gate, a mode decision and
two orchestration pipelines that exist nowhere else, so the CLI and the background extraction loop
cannot reach them and they cannot be unit-tested. `EvaluateQueryGuardAsync` is a tiered policy engine
reading its own settings inside the tools file.

Extract `ShareExtractService` and `QueryGuardService` into Core. Both tool methods become the thin
delegations the other 21 already are.

### WP9 · Finish the `ISettingsStore` extraction and make three lying defaults abstract — 🔶 **F8 LANDED · F7 PART-REFUSED, PART-OPEN** — **architecture F7 + F8**

> **Verified against `src/`, 2026-08-15.**
> **F8 is done (ADR-0054).** `GetAsync` and its two siblings are abstract; `IMemoryStore.cs:15`
> carries the reason — *"a default of 'not found' is a wrong answer, not a safe one"*.
> **F7 is deliberately not done, and the code says why.** `SqliteMemoryStore.cs:717`: *"IMemoryStore
> keeps the members because ~40 call sites reach settings through the store they already hold."*
> That is a decision, not an omission, and the finding should be closed as declined rather than left
> reading as outstanding work.
> **What remains of F7 is one line.** `SqliteMemoryStore.cs:33` still hand-builds
> `new SqliteSettingsStore(factory)` beside the registered one instead of taking it injected. That
> half is real and unaddressed.
**Effort:** SMALL · **Surface:** `IMemoryStore.cs`, `SqliteMemoryStore.cs:33,720-724`, `IPromotionQueueStore.cs`

The settings members remain on `IMemoryStore` alongside the new port, `SqliteMemoryStore` hand-builds a
second `SqliteSettingsStore` beside the registered one, and three default interface members return
semantically wrong answers: `GetAsync` → "not found", `DeleteInScopeAsync` → **widens a scoped delete
into an unscoped one**, `ClaimAsync` → **deletes the row instead of claiming it**. Each protects one
test fake at the cost of a silent wrong answer.

Make all three abstract; the compiler lists the work.

### WP10 · The one-line hygiene set — ✅ **FOUR LANDED**
**Effort:** QUICK each · can share a PR
**Landed** on `work/wp10-hygiene`, merged into the campaign branch: the proxy redirect, the rekey
probe's create mode, the quiet-log framework filter, and `identifier.sqlite`. The redirect fix ships
with `HttpHandlerRedirectTests`, a source-derived gate (every `SocketsHttpHandler` must set
`AllowAutoRedirect = false`, with an anti-vacuity assertion), watched red naming `ProxyRegistrations.cs`
then green. **Neither handler had a test before, so the already-hardened sibling was unprotected too.**

*Two adjustments made while implementing, both recorded rather than silently absorbed:*
- The rekey probe went to `ReadWrite`, **not** `ReadOnly` as the lane suggested — a WAL bank needs a writable `-shm` to open, so `ReadOnly` would have traded a wrong comment for a broken probe. `Create` is gone, which is the hazard that mattered, and the comment is now true.
- `Log.SearchQualityRecordFailed` and the `limit`/`MaximumLength` validator bounds were **not** taken: the first touches `MemoryTools.cs`, a serialisation point with WP8, and the second changes a public contract (rejecting `limit > 200`) which deserves the owner's sight rather than an away-mode judgment call.

- `AllowAutoRedirect = false` on the proxy's token-carrying handler (`ProxyRegistrations.cs:19`) — its hardened sibling already does this for a documented reason (security F13).
- `Mode = SqliteOpenMode.ReadOnly` on the rekey probe, or correct the comment that claims it (security F14).
- `loggingBuilder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning)` — 10 MB of `quiet.log` is a 10-second framework heartbeat in a file deliberately never rotated (operations F3).
- **Fix the double-dispose in `NodeRunner.StartHttpMcpServer` (`:117-129`)** — `DisposeAsync()` in a `finally` after `WaitForShutdownAsync` has already run `StopAsync`. **This, not the missing catch blocks, is what produced the one real `crit` in `serve.log`** (H26). *Re-justified after the adversarial pass; see the revision note.*
- Wrap the four hosted-service timer awaits in `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`, matching `WatchHostedService` (H13). **Defensive only** — the adversarial pass refuted the claimed symptom: `Host.TryExecuteBackgroundServiceAsync` returns early with no log when the task is cancelled during `ApplicationStopping`, and `quiet.log` has **zero** `[Critical]` lines across 104,002 lines and 10+ shutdowns. Do not justify this with "it logs a crit on every shutdown"; it does not. Justify it as making a real behaviour explicit — `PeriodicTimer.WaitForNextTickAsync` **throws** on cancellation and never returns `false`, which was probed rather than assumed.
- `Log.SearchQualityRecordFailed` for the one direct `logger.LogWarning` among 111 source-generated call sites (operations F7).
- `RuleFor(x => x.Limit).InclusiveBetween(1, 200)` plus `MaximumLength` on `Query` and `Content` (security F9).
- `git rm identifier.sqlite` and a `.gitignore` rule (ci-docs F9).

---

## Wave 4 — Boundaries, gates and the things that let this happen

### ~~WP20 · Gate the `Speed` trait~~ — **WITHDRAWN. The gate already exists and works.**

The adversarial pass filed this as new finding 4: *"Nothing gates the `Speed` trait … CI's exact
partition is true today by discipline, not by a gate."* The QA lane had said the opposite, listing
`SpeedGateCoverageTests` among the self-guarding derived gates. **The orchestrator settled it by opening
the file, and the QA lane is right.**

`tests/AiRaccoon.Tests/Unit/SpeedGateCoverageTests.cs` exists and does precisely what this package
proposed: `EveryTestClass_CarriesASpeedTrait` reflects over every class with a `[Fact]`/`[Theory]`
(excluding Reqnroll's generated features, which `Category=bdd` covers) and asserts each carries a
`Speed` trait — **and** it has the anti-vacuity assertion, `TheGuardSeesTheTestClasses`, requiring the
reflection query to find more than 100 classes.

**Proved it can actually fail**, because a gate nobody has watched go red is not a gate. Dropped a
trait-less `ProbeNoSpeedTraitTests` into `Unit/` and ran it:

```
these classes carry no Speed trait, so no CI job runs them: AiRaccoon.Tests.Unit.ProbeNoSpeedTraitTests
Failed!  - Failed: 1, Passed: 1
```

Removed the probe; back to `Passed! - Failed: 0, Passed: 2`. **The partition is held by a mechanism,
not by discipline.** Nothing to build here.

*Kept in the plan rather than deleted, so the next reader can see this was checked and rejected rather
than overlooked.*

### WP19 · Fix the flaky test in the merge gate — 🔶 **HALF 1 LANDED · HALF 2 SPLIT** — **adversarial new finding 7, correcting QA F3**

> **Half 1 shipped as ADR-0061.** An unmapped exception now reaches the client as
> `unexpected-error: <Type>` and is logged at Error with the exception attached (event 912), instead
> of escaping to the SDK's eleven words. Watched red by removing the catch: the end-to-end case
> reproduced `"An error occurred invoking 'memory_stats'."` on demand. **Half 2 — reproducing and
> fixing the race — is unchanged and still open**; the flake was observed again during this work, on
> a different case each run. What changed is that its next failure will name its exception type.
>
> **AND IT DID — root cause found, 2026-08-15 (ADR-0062).** The next CI failure read
> `unexpected-error: SqliteException`, with the server log showing **SQLite error 26, 'file is not a
> database'**, hitting three services in one server at once. Error 26 is what SQLite returns when a
> **plain** bank is opened **with** a key. `TestData.EnvVarGate` serialises the classes that mutate
> the process-global `AIRACCOON_DB_PASSPHRASE` against each other, but **not** against the rest of
> the suite — and `ToolRefusalsTests`, `WatchEventSourceTests`, `ServeRestartTests` and
> `BackendLauncherTests` all open a real bank without taking that gate. **The `LoopbackPort`
> hypothesis is disconfirmed outright: no port is involved.** The fix — a seam over the environment
> read rather than serialising the suite's slowest tests — is scoped, not guessed.
>
> **Half 2 found two defects, not one (ADR-0062).** The package assumed a shared
> `LoopbackPort.BindWithRetryAsync` cause. `IdleWatchdogTests` binds no port and holds **18**
> `Task.Delay` calls where `ToolRefusalsTests` holds **zero**. The watchdog's race is root-caused,
> reproduced by injecting the timing (delays to zero → 2-3 of 8 fail every run), **fixed**, and
> falsified (suppressing `StopApplication` turns 5 of 8 red). `ToolRefusalsTests` is **not fixed**:
> it failed once during this work and then survived three clean full-suite runs and a fourth under
> ten busy loops on ten cores, so **CPU contention is disconfirmed as its trigger**. ADR-0061's
> diagnostic will name its exception type on the next occurrence; until then any cause is a guess.
> Five further files carry the watchdog's latent pattern and are listed in ADR-0062, unchanged.
**Effort:** SMALL · **Surface:** `tests/AiRaccoon.Tests/Integration/Mcp/ToolRefusalsTests.cs:218-229`

The QA lane saw `KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog` fail only when three `dotnet test`
invocations ran concurrently on one machine, and concluded — reasonably — that it was an artefact of a
condition CI never creates. **The adversarial pass corrects that:** it failed on a single
`Speed=Fast` run *and* on an unfiltered full run (`Failed: 1, Passed: 2860, Skipped: 9`), then passed
clean on an immediate rerun with no code change. It is flaky in isolation, not only under contention,
and `Speed=Fast` is the PR gate.

**It has now cost a red build on an unrelated PR — 2026-08-15, and this is the fourth and fifth
observation.** `ToolRefusalsTests.IngestFile_OutsideScope_ReturnsRefusal_WithoutAnSdkErrorLog` failed
`build-fast` on PR #291, whose diff touches only tool-inventory test files and a plan document —
nothing within reach of ingest scope or refusal mapping. The same test passes 3/3 locally. **That is
the concrete harm this package predicted: a red gate on a change that could not have caused it, which
trains the next reader to re-run rather than read.**

**And the failure mode is not port contention, which changes what the fix has to be.** The assertion
that failed was the refusal text, not a bind:

```
Shouldly.ShouldAssertException : text
"An error occurred invoking 'memory_ingest_file'."      # expected a "path-outside-scope:" prefix
```

The tool threw something `ToolRefusals.Filter` does not map, so the SDK returned its **generic**
message. Under CI load the most likely candidate is a bank open losing the 5-second busy timeout and
surfacing a `SqliteException` where a `PathOutsideScopeException` was expected — but *that cannot be
read from the failure*, because the generic message carries no detail.

**This answers the consumer-surface lane's open question** — "whether an unmapped exception reaches the
MCP client with any more diagnostic text than a mapped refusal, or just becomes a generic protocol
error". It becomes a generic protocol error with nothing in it. So the package now has two halves:

1. **Make an unmapped exception diagnosable** at the MCP boundary — its type at minimum. Today an
   agent, an operator and a CI log all get the same eleven words. *(Product behaviour change; sequence
   it deliberately rather than folding it into a test fix.)*
2. **Then** reproduce and fix the race, which is only tractable once the first half says what threw.

**A third independent observation, from WP1's own verification run.** Two `Speed=Fast` failures
appeared on one run and a different single failure on the next, with all 52 passing in isolation —
`ServeRestartTests.AnExistingServer_IsCycled_AndTheRestartOwnsThePort` joins `ToolRefusalsTests` and
`BackendLauncherTests` in the same family. All three bind real loopback ports through
`LoopbackPort.BindWithRetryAsync`, which retries only on `SocketError.AddressAlreadyInUse`. **The
varying failure set across runs is what makes this flakiness rather than regression** — but it also
means a real regression in these files would be indistinguishable from noise, which is the actual cost.

**Why this matters more than one test.** This repo has already been bitten by a gate nobody trusted:
`build.yml:85-90` documents four red tests merged through a green PR, and `nightly.yml:5-7` documents a
backstop that had failed on every run it ever had. A gate that goes red at random trains people to
re-run rather than read — the same failure mode arriving by a different door.

**Gate — watch it go red first.** Find the actual race (the test binds a real loopback HTTP server;
`LoopbackPort.BindWithRetryAsync` retries only on `SocketError.AddressAlreadyInUse`), then reproduce it
deterministically — by injecting the timing rather than by looping the test — before fixing it. A flake
"fixed" without a reproduction is a flake that moved.

### WP21 · Stop calling async ADO.NET methods on SQLite — ⏸ **DEFERRED by owner, 2026-08-15** — *new, owner-supplied source*
**Effort:** LARGE · **Surface:** every `ExecuteAsync`/`QueryAsync*` call in `src/AiRaccoon.Infrastructure/`

> **Owner ruling, 2026-08-15: "we can leave the async access for now."** Kept in the plan rather
> than deleted — the citation and the reasoning are the expensive part, and the trap it explains
> (a concurrency test that runs sequentially) will catch someone again whether or not the calls change.

Microsoft's own page for this provider says it plainly
([Async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)):

> "SQLite doesn't support asynchronous I/O. Async ADO.NET methods will execute synchronously in
> Microsoft.Data.Sqlite. **Avoid calling them.**"

The data layer calls them throughout. They buy no concurrency and cost a state machine per call — but
the real damage is that they *look* like concurrency. **That is what made WP6's first reproduction pass
against a live defect** (ADR-0053): `Task.WhenAll` over a `Select` of `SearchAsync` ran 200 searches one
after another, and the lost-update race the test existed to catch could not occur. Any future test
reasoning about concurrent bank access has the same trap waiting.

**Why this is LARGE and not a sweep.** The signatures are `async` all the way up to the MCP tool
surface, which genuinely is async (HTTP). Converting the data layer to synchronous calls without
converting the layers above it just moves the state machines; converting the layers above changes the
tool surface. **The decision is not "remove async" — it is where the async boundary belongs**, and that
is an architecture question, not a mechanical edit.

**What is cheap and worth doing first, independent of the above:**
1. **A note where it will be read** — on `SqliteConnectionFactory` and in the testing docs — that async here is synchronous, and that a concurrency test must use `Task.Run`. This is the part that already cost real time.
2. **A gate**: a test asserting that any test claiming to exercise bank concurrency uses real parallelism. Hard to express directly; the practical version is the note plus a review checklist item.

WAL, which that page recommends instead, is already enabled (`SqliteConnectionFactory`), so the
performance advice is satisfied — it is the "avoid calling them" half that is outstanding.

### WP13 · Wire up the architecture test that is already paid for — ✅ **IN REVIEW** — **H23**
`tests/Directory.Packages.props:18` pins `TngTech.ArchUnitNET.xUnitV3` and no project references it.
The only mechanical layering guard in the repo is a missing `ProjectReference`, which catches
assembly-level leaks and nothing else — not the string-matched Infrastructure dependency in Core, not
the domain service in Infrastructure, not the ranking domain under `Sqlite/`, not the concrete-type
injections. **Every architecture finding in this review is invisible to CI**, which is why they
accumulated at 0 warnings.

Three starter rules, each watched fail against today's code first: Core depends on no other project
assembly; no type in `AiRaccoon.Core.*` references `System.Net.*`; every `[McpServerTool]` class's
constructor parameters are interfaces. **Rules two and three fail today** — that is the demonstration.

### WP14 · Close the port boundary the DI helper dissolved — ✅ **SUBSUMED BY WP13** — **H19**

> **Owner question 10 is no longer blocking (ADR-0059).** WP13's rule 3 fixes the same defect from the
> consumer side and needs no registration change: it found **12** concrete injections, not 8, and every
> one of the four extra types (`SharedExtractionRunner`, `SweepService`, `ForgettingPolicyService`,
> `SyncCloudStoreFactory`) already had an interface, so the fix was parameter types and nothing else.
> Narrowing `AddRequiredSingleton` to register only the interface remains available as separate
> hardening, and is what question 10 should now be read as asking.
`AddRequiredSingleton` registers each implementation under both its concrete type and its interface, so
injecting the concrete Infrastructure class is exactly as easy as injecting the port and nothing
reports the difference. 8 of 8 tool classes inject the concrete `ToolGate`. Register only via the
interface and fix the compile errors — each one is a consumer that was bypassing a port. *Owner
question 10.*

### WP15 · Derive the tool list everywhere it is pinned — ✅ **IN REVIEW (PR #291)** — **QA F1 + surface F5/F8/F9 + security F16**
Nine stale or pinned copies of the 26-tool surface across four lanes' findings. The numeric assertions
are all correct today; the names and prose are not (`ToolsNamespace_ExposesAll24SpecTools` asserts 26;
one E2E file carries three different numbers; `SECURITY.md` says 23; `docs/reference/README.md` and
`docs/explanation/architecture.md` say 22). `ToolInventoryTests.cs:124-149` already does this correctly
for the packaged README — apply that pattern to the rest and delete the pins.

**Landed on `work/wp15-derive-tools`.** `TestHelpers/RegisteredTools` is the one source; the 26-entry
`ExpectedToolNames` array is deleted. `McpServerSetupHostTests` gained real coverage in the process —
it compared a count plus six sampled names, which passes while the other twenty drift, and now
compares the whole derived set, so it also catches a tool declared and never registered.

**A tautology was introduced and caught here, and it is worth recording.** The first version asserted
`tools.Count.ShouldBe(RegisteredTools.Count)` with *both sides derived from the same reflection* — a
comparison that can only ever pass. The red-first probe is what exposed it: a 27th `[McpServerTool]`
turned five other checks red and left that one green. The count assertion was removed; the count is
guarded only where it has an **independent** second source, the packaged README. **Deriving a value is
not automatically safer than pinning it — a derived expectation compared against its own source is
strictly worse than a pin, because a pin can at least go stale visibly.**

### WP16 · Platform coverage in CI — ⏳ **OPEN, confirmed** — **H16**

> **Verified against `.github/workflows/`, 2026-08-15.** Every `runs-on:` in `build.yml`,
> `nightly.yml` and `labeler.yml` is `ubuntu-latest`; `publish.yml` matrixes RIDs but not runners.
> No macOS or Windows leg exists. Still blocked on owner question 15.
Add `macos-latest` and `windows-latest` legs to `build-fast` only, for cost control. ADR-0049 already
measured a 0.070 nDCG spread across host CPUs against a 5e-3 tolerance, and ADR-0050 documents the
fixture-pinning workaround it forced. Six RIDs ship with no PR gate on four of them. *Owner question 15.*

### WP17 · Documentation and decision-record truth — ✅ **LANDED IN FULL**

> **Verified against the tree, 2026-08-15 — every bullet below is now closed.** `SECURITY.md:44`
> reads "26 tools" against an actual 26 `[McpServerTool]` attributes (derived, not counted by hand).
> `SECURITY.md:89` now carries *"**`ro` is not literally read-only**"* naming `access_count`,
> `last_accessed_at` and `rating`. ADR-0043's gap is headed *"Known gap — closed 2026-08-14"*.
> ADR-0048 carries a 2026-08-15 scope amendment stating the delivered guarantee is **fence balance**
> and that the title generalises further. `agent-memory-server.md:58` documents `includeFullValue`.
- ✅ **LANDED.** **Stale `Status:` fields — swept exhaustively by the orchestrator, closing the lane's open item and finding a fourth.** Of every ADR the index records as superseded or reversed, exactly one (**ADR-0002**) self-updates correctly (`Status: **Superseded** — 2026-08-09. Superseded in parts by ADR 0008…`). Four still read `Accepted` in their own files:

  | ADR | What the index says | What the file says |
  |---|---|---|
  | 0013 | "supersedes ADR-0013 in full" (via 0016) | `Status: Accepted` |
  | 0029 | "superseded in part by ADR-0033 and ADR-0039" | `## Status` → `Accepted` |
  | 0030 | "reversed by ADR-0034, which found the assignment never reached the database" | `## Status` → `Accepted` |
  | **0033** | "superseded by ADR-0039, which restores the substrate without a scoring model" | `## Status` → `Accepted` — **the lane did not find this one** |

  A reader who opens 0029 or 0030 directly sees a live-sounding decision describing a filter and a TTL
  policy that no longer exist in `src/`, with no forward pointer at all — and `docs/adr/README.md:3-4`
  calls ADRs "immutable, frozen", so the index is not where a reader is taught to look.

  **Gate — and this one is mechanizable.** `tests/AiRaccoon.Tests/Unit/Docs/AdrIndexTests.cs` is already
  a derived guard comparing disk against the index. Extend it: any ADR whose index row contains
  "supersed" or "revers" must not read `Accepted` in its own Status. Break it by reverting ADR-0002's
  Status line and watch it go red. Note the three header formats in play (`## Status\nAccepted`,
  `Status: Accepted`, `Status: **Superseded** — …`) — the check must tolerate all three or the
  normalisation becomes a fourth source of drift.

  **Landed.** All four fixed; ADR-0013's index row gained the passive phrasing it lacked so the gate can see it. `AdrIndexTests.SupersededAdrs_DoNotStillCallThemselvesAccepted` derives the set from the index and matches the **passive voice only** — "supersedes ADR-0013" in 0016's row means 0016 is fine and 0013 is not. Watched red twice: reverting all four named 0029, 0030, 0033; reverting 0013 alone named 0013.
- `SECURITY.md`: correct the tool count, correct "`ro` mode allows only reads" (search writes `access_count`, `last_accessed_at` and `rating`, including on shared rows), and add exception messages and stack traces to the "what leaves the process" table — OTLP export ships absolute filesystem paths today.
- ADR-0043's "Known gap" describes a defect that `ServerRestart.cs:160` has since closed.
- ADR-0048 claims "a chunk is a well-formed markdown fragment"; what it delivers is fence balance. A 200-row table splits with 33 of 34 chunks carrying orphaned body rows. Narrow the claim or widen the guarantee.
- `docs/reference/agent-memory-server.md` omits `memory_promotion_list`'s `includeFullValue` — the one existing route to a full entry body.

### WP18 · Python packaging honesty — ⏳ **OPEN, confirmed** — **ci-docs F6 + F7**

> **Verified, 2026-08-15.** `pyproject.toml` has no `dependencies` key at all — not an empty one —
> while `scripts/` imports `numpy`, `httpx` and three `sklearn` modules. `uv.lock` is still 8 lines.
`pyproject.toml` declares zero dependencies while scripts import `numpy`, `scikit-learn` and `httpx`;
`uv.lock` is 8 lines and locks none of them, so `uv sync` on a clean checkout cannot run the code it
covers. Delete the three unreferenced version-pinned checklist scripts (~1,671 lines, forked per
release and abandoned after 1.10.1) or generalise one.

---

## Explicitly not doing

- **Re-tuning the RRF parameters.** ADR-0006's own amendment already found `k=120, 2:1` scores higher on the regenerated corpus and deliberately declined to re-pick. That judgment stands until WP11 gives a held-out number; re-tuning against an in-sample grid would be the circular-benchmark failure a second time.
- **Deleting `entries.embedding`/`structure_embedding`.** The orchestrator proposed it as ~31 MB of vestige; the data lane refuted it by finding the reader (`RebuildVecTableAsync`). They are rebuild insurance — and **WP5 is now a vec0 rebuild**, so this campaign would have had to re-embed 16,000 rows through the ONNX pipeline had they been deleted. The refutation paid for itself inside one plan revision.
- **Restructuring the folder tree** (`Core/Isolation` → `Core/Workspaces`, a `Core/Promotion`, moving the ranking domain out of `Sqlite/`). Real findings, but a wide rename during a campaign this size is a merge-conflict generator with no behavioural payoff. Owner questions 11 and 12 decide it; if approved, it is a separate campaign after this one merges.
- **The `jsaa-memory.db` fixture.** Owner question 9 — nobody but the owner can settle whether another project's documentation belongs in this repo.

---

## What this revision changed

Rev 1 was written from the lane reports. Rev 2 folds in the adversarial pass, which attacked ten
load-bearing claims and refuted or corrected five. Listing the changes because a plan whose corrections
are invisible teaches nobody, and a reader cannot otherwise tell a considered rejection from an
overlooked one.

| Package | Change | Why |
|---|---|---|
| **WP3** | **Scope grew from "restart + backfill" to "two code fixes + restart + backfill".** | `memory_write` does not chunk at all in the code running now (555 rows, 114,883 tokens), and the ingest budget is silently non-engine-aware when `embedding.provider` is unset (104/258 over-window vs 0/276). Neither is fixed by a restart. Rev 1 would have restarted the servers, run a backfill, declared B2 closed, and left two live truncation paths. |
| **WP5** | **Rewritten. The fix changed target entirely.** | `chunk_size` is ~2% of the waste; the `ctx` **partition key** is 98% (22 chunks needed without it, 49 with). Rev 1's package would have shipped, passed its gate, and reclaimed almost nothing — a gate measuring the wrong quantity. It now requires a measured latency comparison and a KNN-equivalence assertion, because a size gate alone would pass a migration that silently broke scoping. |
| **WP4** | Acceptance criterion **withdrawn**; corrected formula added. | Rev 1 claimed ordering was unchanged because this is "a score-reporting fix". False: `SourceLambda` defaults to **0.1**, so `SourceAffinityRanker` already re-orders, drops and re-normalises. Measured output positions 2-4 are fused positions 5, 6, 7. |
| **WP10** | Hosted-service catches **re-justified**; a new item added. | The claimed symptom — a false `crit` on every graceful shutdown — is **refuted**: `Host.TryExecuteBackgroundServiceAsync` returns early with no log in exactly this case, and `quiet.log` has zero `[Critical]` lines across 104,002 lines. The one real `crit` comes from a **double-dispose in `NodeRunner.cs:117-129`**, now its own item. The catches stay as defensive work with an honest justification. |
| **WP20** | **New.** | Nothing gates the `Speed` trait. The campaign's strongest healthy claim holds by discipline, not by a mechanism. |
| **WP19** | **New.** | A flaky test in the PR gate, correcting the QA lane, which saw it only under artificial concurrency. |
| **WP17** | Extended from three stale ADRs to **four**, plus a derived gate. | An orchestrator sweep of every ADR the index marks superseded found ADR-0033 as well, and three different `Status:` header formats — which is why nothing catches it today. |
| **B1 / WP1** | Evidence upgraded from READ to **MEASURED**, and the fix's scope narrowed. | The pass **exploited it end to end** (`{"deleted":2}`, victim's project emptied). Separately, an orchestrator sweep found `FilterFor` has exactly three call sites and only `DeleteContextAsync` takes an untrusted context — so this is one call site, not four. |

**What survived unchanged:** both blockers, the vec0 waste figure (to the byte), the 63.88% structure-null
fraction (exact), the CI partition, and every finding in WP1, WP2, WP6–WP9 and WP11–WP18. The failures
were in supporting numbers and causal stories — which is exactly what gets quoted later.

**One claim got *stronger* under attack.** B2's hedge that external-content FTS5 might not index the
truncated tail was refuted in the finding's favour: four terms drawn from the last 5% of the longest
live row (34,010 chars) each match through the index. The tail is genuinely keyword-reachable and
genuinely vector-unreachable, which is what makes this a ranking defect rather than data loss.

## Risks

- **`SqliteMemoryStore.cs` is edited by four packages.** Sequence them; do not run WP1, WP4, WP6 and WP9 concurrently in separate worktrees.
- **WP3's backfill runs against live user data.** Snapshot the bank first (`VACUUM INTO`), and verify `SUM(length(value))` is non-decreasing across the pass.
- **WP4 may change result ordering** even though it is framed as a score-reporting fix. That is precisely why WP11 precedes it.
- **WP14's fix produces a large mechanical compile-error sweep.** Land it alone, not inside a wave.
- **Restarting the servers (WP3 step 1) interrupts every agent session using them.** Coordinate; it is not a background action.
