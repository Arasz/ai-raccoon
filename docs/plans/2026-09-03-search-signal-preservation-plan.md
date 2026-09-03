# Search signal preservation (Stage 1) — plan + specification

**Date:** 2026-09-03
**Status:** Proposed (no code changed)
**Problem:** `memory_search` computes absolute relevance signals, then destroys them.
Every response max-normalizes rank 1 to exactly 1.0 — twice (`ReciprocalRankFusion.Fuse`
divides by max; `SourceAffinityRanker.Rank` re-normalizes after the affinity boost,
per ADR-0047). `MemorySearchResult` carries only the post-normalization `Ranking`
(`Core/Memory/MemorySearchResult.cs:3-10`); leg ranks, cosine similarities, and raw
fusion sums never leave the fusion seam. Result: "best of a bad lot" is
indistinguishable from "genuinely good hit" across queries. Stage 1 stops the
destruction. Stage 2 (calibration, §7) becomes possible only after it.

## 1. Goals / non-goals

**Goals:**
- Every returned result carries its absolute pre-normalization evidence: how strongly
  the legs agreed, which legs, at which ranks.
- Every response carries its shape: margin structure that exposes thin responses.
- Zero behavior change: ordering, `minRelativeScore` semantics (ADR-0047), limits,
  snippets, scores-as-seen all identical. Additive fields only (d-398 playbook).
- Live traffic starts accruing the Stage-2 dataset for free (§5).

**Non-goals:**
- No relevance judgments, no fitted map, no `relevance` value (that's Stage 2).
- No change to `Ranking` semantics, floor, weights, k, or any default.
- No gating, flagging, or reordering on the new signals (ADR-0054: no absolute bars
  without measured false-empty rates).

## 2. Capture seams (exact)

| # | Seam | File | What is known there and nowhere else |
|---|---|---|---|
| S1 | Weighted multi-leg RRF | `Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:644` → `ReciprocalRankFusion.Fuse` | (leg, rank) pairs per hash; raw RRF sums pre-`/max`; participating weights + k |
| S2 | Leg producers | `SqliteMemoryStore.Search.cs:91-97` (`BuildFtsResults`, raw `bm25(...)` in `MemorySql.cs:114`) and `SqliteMemoryStore.cs:834` (`BuildDualVectorResults`, fused cosine via `SimFromDistance`) | FTS BM25 magnitude; **cosine similarity — the one signal absolute across queries** (same embedding space) |
| S3 | Tool boundary | `Tools/MemoryTools.cs` result mapping | where evidence joins the MCP envelope by hash |

Leg vocabulary already exists: `Core/Memory/Fusion/ModalityLeg.cs` (`Name, Queried,
Candidates`). Reuse its names at S1 (verify exact strings at implementation; `Fuse`
currently takes weight-only `WeightedResults` — it gains the leg name).

Out of scope for S1: the code-corpus fusion (`SqliteCodeSearchService`, own
k+rank/max-normalize path) — named follow-up §8. The second unit-weight re-fuse in
`SearchResultMerger.Merge:29` needs no capture: S1 signals attach to the hash at S1
and ride along; it only reorders.

## 3. The three signals (normative formulas)

Notation: for hash `h`, `raw(h) = Σ w_l/(k+rank_l(h))` over participating lists (already
computed in `Fuse`). `maxPossible = Σ w_l/(k+1)` over the same participating lists
(non-empty — matching `Fuse`'s skip, so strength 1.0 always means "rank 1 in every leg
that fired," never penalizes a disabled leg).

- **`fusionStrength(h) = raw(h)/maxPossible ∈ (0,1]`** — absolute across queries given
  the query's own k/weights (shipped: k=60, 1:1 → max ≈ 0.0328). Reading: fraction of
  the strongest agreement this query could have produced. ~0.95 = everything agrees;
  ~0.2 = thin. Rank-1-in-both-legs always scores 1.0 regardless of query.
- **`legs(h) = [(legName, rank)]`** — discrete, names the *why* (fts-agreed?
  vector-only? at which depths?). A single-leg entry is itself the thin-response tell.
- **Response `topMargin = (raw₁−raw₂)/raw₁` + `topVsMedian = (raw₁−raw_median)/raw₁`**,
  computed on pre-max raws at S1, `null` when fewer than 2 results (median variant
  needs ≥3, else null). Flat distribution + single-leg top = the measurable "best of a
  bad lot" signature. Today this shape is unobservable; after S1 it is one subtraction.

Cosine capture (S2): carry the fused vector-leg similarity into the evidence for
vector-participating hashes. It is the only cross-query-comparable magnitude in the
pipeline (BM25 magnitudes are query-dependent; ranks are ordinal). Model-version
pinning for cosine comparability across re-embeds is a §8 item, not a blocker
(single model in practice; record the model id alongside if cheap).

## 4. Carrying design (equality-safe)

New internal sidecar, e.g. `RetrievalEvidence(Hash, FusionStrength,
IReadOnlyList<LegRank>, double? Cosine)` + response-level `FusionStats(TopMargin,
TopVsMedian, MaxPossible, ParticipatingLegs)`, produced in `Fuse` (it already builds
per-hash aggregates — no extra pass, O(n) attach) and carried beside results through
the existing internal envelope flow (`AdjustedSearchResult`/`DeferredSearchResult`
chain), joined by hash at S3 into the MCP response.

**Why a sidecar, not wider `MemorySearchResult`:** the record is positional and
golden-compared across dozens of tests — appending fields with defaults still breaks
record-equality assertions (expected-built-without vs actual-with). The sidecar keeps
the hot record and every existing assertion byte-identical; `Rank`/`with`-chains never
learn evidence exists. Rejected alternative recorded so it isn't re-litigated.

MCP envelope: three additive per-result fields + one additive response-level object.
`Ranking` untouched in name, position, and semantics.

## 5. Metrics: yes — as feature records on the existing label pipeline (decision)

**Answer to the open question: collect, in two grains, on infrastructure that already
exists for exactly this shape.**

| Grain | Where | Precedent | Content (features only — hashes, scores, ranks, leg names; never values/snippets) |
|---|---|---|---|
| Per-query response stats | `metrics` table (`name/kind/value/unit` + `query_hash` + `correlation_id` + `tags` all exist) | designed for this | `search.fusion.top_strength`, `search.fusion.top_margin`, `search.fusion.legs_fired` |
| Per-result features | `search_quality`, new compact JSON `result_features` TEXT column | `top_source_files` TEXT blob precedent; row keyed by `correlation_id` | `[{hash, strength, legs, cosine}]` for returned rows |

Why this shape, not alternatives:
- The `correlation_id` join to **labels is free**: `usefulness_grade` and
  `follow_through_*` already live on `search_quality` — every graded/followed-through
  search becomes a labeled Stage-2 training row with zero later joinery. That is the
  whole point: the dataset accrues from live traffic at ~zero marginal cost.
- `metrics` (scalar grain) is wrong for per-result vectors; a new table is sprawl the
  JSON column avoids. Rejected alternatives recorded.
- Immediate ops value before any Stage 2: production distribution of top-strength /
  margins quantifies how often "thin" responses happen and validates the §3 signature
  against `memory_performance` reporting.

Honesty constraints (normative):
- **Labels are the bottleneck, not features.** Grades/followthroughs are sparse and
  opt-in; live traffic is biased (queries agents chose to run, results they opened).
  Telemetry is the *supplementary* Stage-2 source; controlled eval-harness replay
  (graded sets, §7) stays primary. Never claim "n live searches" as validation n —
  validation n is labeled rows.
- **Fail-open:** telemetry write failure never fails a search (log-and-continue, same
  as the keyword-modality failure posture in `QueryFtsBatchAsync`).
- **Retention:** follow `search_quality`'s existing policy — if none exists, defining
  one is a prerequisite task in the implementing lane, not an afterthought (§8).

## 6. Acceptance gates (each runnable, each can fail)

- **G1 — order/floor preservation:** golden test on fixed multi-leg inputs through
  `Fuse`+`Rank`+floor — `Ranking` sequence byte-identical with and without S1 wiring.
  (The zero-behavior-change proof.)
- **G2 — formula goldens:** hand-computed `raw/maxPossible` on a 2-leg, k=60 fixture
  (incl. an empty skipped leg and a weight-0 leg) asserted to 1e-9; midrank-free,
  no ties special-casing needed — ties share raw sums naturally.
- **G3 — degenerate inputs:** empty response → no stats, no crash; single result →
  margins null, strength defined; single-leg → legs length 1, maxPossible over that
  leg only; NaN/negative leg scores → evidence null for that hash, search continues.
- **G4 — envelope compat:** existing `memory_search` surface tests (incl.
  tool-inventory counts) pass unmodified; new fields asserted in new tests only.
- **G5 — no extra queries, bounded cost:** capture is O(n) over already-materialized
  lists; telemetry is one `metrics` batch + one column on the already-written
  `search_quality` row — perf-asserted or profiled on the search path, no new
  per-result round-trips.
- **G6 — telemetry honesty:** kill-switch/failure-injection test proving a telemetry
  write failure still returns full results; retention rule exists and is cited.
- **G7 — docs say what it is:** tool description defines strength/legs/margin with
  the worked reading ("0.95 = legs agree" / "flat margin + single leg = thin
  response, not a verdict"), and states what is *not* claimed (no relevance).

## 7. Stage 2 continuation (when S1 has accrued + eval replay exists)

Fit `P(relevant | strength, legs, margin, cosine)` — isotonic primary — on (a) graded
eval replay (primary: `search_quality_eval.json` grades, tuning corpora,
`baseline-queries.json`) and (b) the §5 telemetry joined to grades/followthroughs
(supplementary, bias-noted). Ship as an external version-pinned map (same honesty
pattern as the promotion-calibration proposal: param change → `null`+stale, never a
number). Per-result `relevance` ∈ [0,1] + validated weak-response flag. Gated then on:
beats raw-strength-only baseline on held-out log-loss/ECE; monotonicity in strength;
false-empty rate measured before any gating use. Not specced further here on purpose.

## 9. Plan-review fold (d-404, SHIP-AFTER-FIXES — normative for all lanes)

Plan d-403 (P1–P7) reviewed by code-reviewer lane d-404. Verdict: backbone sound;
8 MUSTs folded below. Every implementation lane reads this section as overriding
anything it contradicts in §§1–8 or the d-403 package text.

### MUST resolutions
- **M1 (P6):** no `CurrentVersion` ladder bump. Schema change = digest-`Ddl` string
  change + `EnsureSearchQualityResultFeaturesColumnAsync` tolerant-ensure
  (`pragma_table_info` probe + `duplicate column` catch, `EnsureCodeEmbedAttemptsColumnAsync`
  pattern) called from the digest-mismatch branch. Spec §8 "no migration" stays overturned.
- **M2 (P6):** scope += `ISearchQualityService` typed Core evidence param,
  `SearchDispatcher` pass-through (join served `Results` to sidecar),
  mechanical updates to the 2 named fakes (`MemorySearchKindToolTests.cs:478`,
  `TestData.cs:589`), NaN/Infinity→null sanitization in `SqliteSearchQualityService`
  (Core stays JSON-free; `System.Text.Json` throws on non-finite doubles).
- **M3 (P3):** re-scoped — NO production file. (a) Contract test pinning implicit
  transport (vector-candidate `Ranking` == `FusedRank.Score` through
  `BuildDualVectorResults`/`ByCosine`); (b) cosine-extraction + null/NaN rule at the
  `FuseWithEvidence` consumption point (home: P1 helper or P2 file — lane states it).
  Consequence: **P3 ∥ P4 legal** (both serial after P2 only).
- **M4 (G3 reassignment):** Fuse never reads scores — no NaN tests at Fuse. (i) NaN
  cosine → `Cosine` null, strength/legs kept (**P3**); (ii) non-finite weight-derived
  raw → whole-hash evidence null, search continues (**P1** guard+test); (iii) negative
  BM25 is expected and must not null anything (state in code comment). P2 keeps
  empty/single-leg/maxPossible G3 tests.
- **M5 (P5):** name envelope members (dict-by-hash vs parallel list — lane picks and
  states); `JsonIgnore(WhenWritingNull)` so absent-evidence serializes byte-identical
  (`Code`-property precedent); absent-evidence test asserts **serialization bytes**.
- **M6 (G5):** P6 AC gains single-statement-per-search assertion on the quality path;
  delete all "or profile note" escape hatches (P4/P6); **P7 owns the end-to-end G5
  assertion** (capture + metrics + quality column jointly).
- **M7 (P7):** glue enumerated — expected none beyond test wiring + the two named
  hookups (`SearchDispatchResult`→`MemoryTools` threading, telemetry call-site).
  Split trigger: any new production type → split P7.
- **M8 (P6):** skip-null-series for margins (single-result search emits `top_strength`
  + `legs_fired`, omits `top_margin`); test it. `0.0` sentinel for missing is forbidden
  (would poison Stage-2 distributions).

### Accepted SHOULDs (test-scope additions, all lanes)
S1 even-count median = average-of-two-middles + golden · S2 G1 fixture must include
sibling groups with `lambda>0`, a floor-boundary tail value, output ties, and
shared/project duplicates (ADR-0035 dedup runs pre-Fuse) · S3 reorder-forcing
(affinity-on) evidence-identity test · S4 G2 literals derived off-implementation
(Python/`bc`), never via `FromRaws`; 1e-9 rationale stated · S5 document pre-floor
population in tool description + P7 stats-invariance-under-limit/floor test ·
S7 all-weights-zero → evidence null; `raw₁≤0` → stats null · S8 `kind=code` case
(`result_features` null, row written) · S10 bounded payload assertion (returned rows
only, ≤ limit) · S6 P6 SPLIT into P6a/P6b (below) · S9/S11 confirmed, no action.

### Revised dispatch order
**P1 → P2 → (P3 ∥ P4) → (P5 ∥ P6a) → P6b → P7.** P6a = interface + dispatcher
threading + fakes (no Sqlite service); P6b = tolerant-ensure + service write +
metrics series + all P6 tests (depends on P6a). P5∥P6a merge order: P5 first, P6a rebases.

## 8. Open questions / implementation notes

- Code-corpus fusion capture (`SqliteCodeSearchService`): same three signals or
  corpus-specific? Decide in-lane; doc-leg S1 must not be blocked on it.
- Hash-namespace join safety at S3 (doc vs code hashes) — verify, don't assume.
- `ModalityLeg` name strings reuse; cosine model-id pinning across re-embeds.
- `search_quality` retention policy existence check (G6 prerequisite).
- Estimated shape: S — no migration, no threshold semantics, no scorer contact.
  The review weight is on G1/G5 proofs, not volume.
