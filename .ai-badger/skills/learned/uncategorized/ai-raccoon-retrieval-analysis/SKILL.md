---
name: ai-raccoon-retrieval-analysis
description: Analyze AiRaccoon retrieval quality or plan improvements.
---

# AiRaccoon Retrieval Quality Analysis

Analyzing retrieval quality for AiRaccoon's hybrid search: understanding the pipeline,
diagnosing failures, mapping symptoms to missing features, and structuring improvements.

## Pipeline Architecture (quick reference)

```
Query → FtsQueryNormalizer (OR-joined alphanumeric tokens)
     → EmbeddingService (all-MiniLM-L6-v2 or OpenAI-compatible)

FTS5 path:  entries_fts MATCH @query → BM25 ranking → LIMIT CandidateWindow
vec0 path:  vec_entries ORDER BY vec_distance_cosine → LIMIT CandidateWindow

Both → ReciprocalRankFusion.Fuse(k=60, equal weights)
     → normalize scores to max=1.0
     → minScore filter (0.7 default)
     → SearchResultMerger (per-context batches → uniform context weight)

Chunking: MarkdownChunker (line-granular, fence-aware, 256 tokens max, 48 overlap)
         TokenizerChunker wraps o200k_base tokenizer

Schema: entries(value) → FTS5 external-content index + vec0(embedding float[384])
```

For the full pipeline trace, see `references/pipeline-architecture.md`.

## Methodology: Retrieval Baseline Analysis

### Step 1: Read the baseline report
Identify overall match rate, rank distribution, category breakdown, individual query
results (matched, near-miss, not-found), and total coverage.

### Step 2: Trace failures through the pipeline
For each missed query: query normalization → chunking → FTS5/BM25 rank → vector
rank → RRF fusion effect → candidate window coverage.

### Step 3: Map symptoms to missing features
Classify each failure: **Structural** (missing data model), **Algorithmic** (query
construction, ranking logic), **Parametric** (tunable constants), **Measurement**
(test coverage gaps).

### Step 4: Propose new baseline cases
Design tests that expose blind spots: source-aware relevance, cross-document
competition, heading-aware retrieval, difficulty stratification, ablation suite.

### Step 5: Structure into improvement waves
Prioritize: structural foundations first, then algorithmic quick wins, then ranking
improvements, then measurement, then parameter optimization. Each wave: what changes,
what gate proves it, expected metric improvement.

### Step 6: Measure with retrieval metrics
Use standard IR metrics (already in `RetrievalMetrics.cs`): nDCG@k, Recall@k, MRR.
Binary isExpectedSource is insufficient — grade relevance per query.

### Content-verify every non-obvious rank change (user rule, 2026-08-04)

A rank delta against the expected source is NOT evidence by itself. When a change
moves an expected file/chunk (either direction) and the new top result is not an
obvious miss, READ the competing chunks before judging: map result hash prefixes to
chunk keys via `scripts/chunk-hash-map.json`, read `entries.value` (plain sqlite3
works), and ask whether the rank-1 carries the same knowledge. Rank deltas alone are
not evidence; same-knowledge alternatives at rank 1 are a bounded trade, not a
regression.

Measured cases where rank movement was NOT a regression:
- A1 ("Why was shadcn/ui chosen over gluestack.io?"): rank-1
  frontend-architecture.md#3 states the pivot evidence and links "The formal decision
  record is ADR-0011 §1"; ADR-0011 links back "Full evidence:
  docs/frontend-architecture.md §3" — same knowledge, cross-linked; expected file at
  rank 2.
- A4 ("What happened to the MCP server?"): rank-1 behaviour-specification.md#3 states
  "The MCP server was deleted; see ADR-0060" — direct same-knowledge answer.
- A6 ("How does the project handle data erasure?"): rank-1 ADR-0069#consequences
  (retention sweep, cross-links ADR-0068) — legitimate erasure-adjacent answer; the
  expected ADR-0067 file actually IMPROVED 4 → 2.

A genuine content miss looks different: S2 ("What does ADR-0011 decide?") ranked the
ADR's metadata header first (title/date only, no decision), decision chunk at rank 5.
The header-first pattern is characteristic of identifier-heavy queries — the fix
domain is within-file/document-first ranking, not the fusion.

When reviewing or validating a baseline-regeneration plan (corpus counts, hash-map
matching, model provisioning, WAL-safe DB copy, determinism chain), see
`references/baseline-regeneration-verification.md` for the verified recipe.

For the per-merge integration protocol (measure → content-verify → append to
comparison-clean.md), the live MCP-probe recipe, the α-sweep mechanism, and the full
A1 debugging timeline (wrong query text, stale build-output db, vec0 CLI limits,
silent `memory_configure` failures): `references/integration-rank-verification.md`.

## Methodology: Live-bank diagnostic (search + promotion quality)

When asked to audit the RUNNING server's bank (logs/reports → store copy → perceived-quality
report — the docs/work/2026-08-11-ai-raccoon-diagnostic.md shape): the evidence inventory
(which logs cover what), the WAL-safe copy recipe (Python sqlite3 backup API on the live
149 MB bank, 0.3 s, integrity ok), the queue-audit SQL (already-shared value-join, noise
tags, shared-tier timeline), and the meta/tool semantics that mislead audits
(`waitingPromotionsCount` is project-scoped; `memory_share` reports `shared:true`
unconditionally — an audit can over-count promotions 3×): `references/live-bank-diagnostic.md`.

For the coverage audit (three denominators: graded/logged vs logged/searched vs
graded/searched) and the auto-grading algorithms catalog (G-Eval, Prometheus, ARES/PPI
calibration, RAGAS context precision, implicit feedback) that answers the diagnostic's
"raise grading coverage" recommendation: `references/auto-grading.md`.

## Methodology: Semantica Causal Graph Persistence Bridge

When integrating session-scoped knowledge graph reasoning (Semantica MCP):
1. **In-Memory Reset**: Semantica's graph lives in process memory and resets on session exit.
2. **Persistence Bridge**: `.ai-badger/skills/semantica-knowledge-graph/scripts/export_semantica_graph.py` exports graph schema (`nodes`, `edges`, `decisions`) to `.ai-raccoon/semantica-graph.json`.
3. **AiRaccoon Watch & Ingestion**: AiRaccoon registers `memory_watch_add` on `.ai-raccoon/semantica-graph.json`, chunking entities, relations, and decision rationale into `memory.db`.
4. **Auditable Decision Chains**: `memory_search` in AiRaccoon returns both textual rationale and structural graph relations, enabling cross-session retrieval of auditable decision chains ("why" choices were made).
5. **Pre-Flight Evaluation Checklist**: Execute `ai-raccoon-state-checklist` after server restarts to verify global tool binaries, memory write/read, watch sync, promotion queue, Semantica graph JSON chunking, and Prometheus auto-grader unit tests green.


## Methodology: A/B Comparison (candidate vs baseline)

When comparing two candidate retrieval improvements head-to-head (e.g. dual-vector vs
plan FTS fixes), the evidence is only as honest as the measurement design.

### The shared-scorer rule

Two harnesses computing their own metrics WILL drift (tie-breaking, case handling,
consolidation edge cases). **Every arm's raw ranked list goes through ONE scorer.**
Each harness emits `{arm: [{rank, hash, path, headingPath, score} × top-100]}` per
query; the scorer computes file hit, section hit, MRR, per-query ranks for all arms
from one implementation. See `references/comparison-methodology.md` for the full
recipe (arms, pre-registered win rule, section-level primary, heading-path semantics,
F+V RRF hybrid, memcap).

### Pre-registered win rule

7 queries = low statistical power; a single flip can sway MRR by 0.14. Pre-register:
an arm BEATS the baseline iff either ≥2 section-level hit flips OR MRR(file) delta ≥
0.1; below = tie. Publish the per-query rank table so single-query flips are visible.

### Section-level is the primary metric

File-level hit@5 can pass on the wrong chunk. When expected sources carry `#decision`
fragments, the verdict keys on section-level hit@5 + section-level MRR. Pre-flight:
verify the expected file's decision-section chunk appears in ANY arm's top-100 ranked
list; if not, section ground-truth is unavailable for that query (report it, don't
assume).

### Cost side

Report peak RSS, wall time, and storage overhead per arm. A mechanism that delivers
+5/6 section hits at 2× vector storage is a different recommendation than one that
delivers +1/7 at zero added cost.

## Key Pitfalls

### FTS5 OR-join drowns signal
`FtsQueryNormalizer.Normalize("What is ADR-0070 about?")` produces
`"what OR is OR adr OR 0070 OR about"`. Stopwords contribute noise; ADR numbers
compete with noise tokens in BM25. Strip stopwords or use AND for short queries.

**Measured (2026-08 FTS plan-fix harness, jsaa corpus, 35 baseline queries):** the planned
"fixed" normalizer (stopword strip + AND for ≤4 tokens + identifier `adr AND <number>` +
OR-mode bigrams) is WORSE than the current OR-join on this corpus — file hits @5 3/7 vs 6/7,
MRR 0.4286 vs 0.6429, and it creates 11 zero-match queries (A2, A6, B1, B2, C3, C5, C6, D3,
F3, G2, H3). Root cause: **FTS5 AND requires all tokens to co-occur inside a SINGLE chunk**;
ADR docs are chunked by heading section, so tokens that exist corpus-wide (e.g. `governs`=13,
`uuid`=9, `choice`=89) never land in the same chunk. Chunk-level AND is a recall killer on
section-chunked corpora; OR (or per-chunk AND over a wider window) is the safer default.
Identifier handling (`adr AND 0070`) is the one F2 piece that clearly helps (A7 window rank 11
vs miss under F1). Verify stopword expectations against the actual list — `why`/`over` are NOT
in the specified stopword set and stay as content tokens. To diagnose a zero-match FTS query,
compare token presence (`MATCH '<token>'` count) vs co-occurrence (`MATCH 't1 AND t2'` count):
all-present-but-zero is a chunking artifact, only absent tokens are a vocabulary gap.

**Resolution (Wave 1 delivered, 2026-08-04):** the finding above falsified AND-for-short
WITHOUT a fallback. The shipped design — stopwords + bigrams + AND-with-OR-fallback,
trigger `hits <= max(TokenCount, limit)` (boundary inclusive: A4's AND primary matched
exactly 5 rows yet excluded the target chunk) — measured BETTER than the OR status quo:
nDCG@5 0.642 → 0.652, FTS-only file hit@5 7/7, FTS-only MRR 0.825, zero zero-match
queries. The trigger is decided PER CONTEXT and the vector pass runs once per context.
A4's boundary case is pinned by `QueryConstructionTests.AndPrimary_AtBoundary_A4DecisionChunkRestoredByFallback`.

### Dual-vector structure signal delivered (Wave 6) — and α is now tunable

The structure modality shipped: `heading_path` + `structure_embedding` columns, an idempotent
content-preserving backfill (`tools/AiRaccoon.StructureBackfill <db> [--alpha]` — safe to
re-run after any corpus regeneration; content hashes never change), and fixed-α fusion
(default 0.5). Measured on main (post-W1+W2+W6, 511/0/43): **C2 hybrid restored to rank 1**
(the 2d provenance-cleanup collapse is fixed — the structure signal matches the invariant's
heading path), A6 file 4→2, A7 exact chunk restored to rank 4, section hit@5 6/6, S2 file@1
with the decision chunk at rank 5 (top-1 is the ADR's metadata header — within-file sibling
competition; the S2 ≤3 gate moved to Wave 3's document-first ranking). Bounded trade: A1/A4
file ranks 1→2 with content-verified same-knowledge alternatives (content-verify rule above).
ADR nDCG@5 0.650 / MRR 0.786 / recall@5 0.581.

`memory_configure` still only accepts embedding-engine params, but α is NOW settable via the
`memory_set_structure_alpha(projectId, alpha)` MCP tool (rw-tier, validated [0,1], writes
`retrieval.structureAlpha`, applies to subsequent searches, no re-embed). For probes, the
direct settings-row write (plain sqlite3) still works and does not require the tool.

### Wave 3 delivered (source-affinity scoring) — best measured state so far

The S2 ≤3 gate that Wave 6 moved to Wave 3's document-first ranking is HIT. Chosen sweep
point (32-point sweep, ADR-0005 + docs/work/2026-08-04-wave3-source-affinity-sweep.md):
λ=0.1, consolidation threshold=0.1, doc-score formula Max. Measured on main (post-W1+W2+W6+W3,
559/0/43): **exact-chunk @3 = 11/11** (post-W2 it was 6/11), A1/A4 RESTORED to file rank 1
(the W6 same-knowledge trade is reversed — rank-1 is now a chunk of the expected file itself),
A6 exact chunk surfaced at rank 2 (was a miss; file 2), A7 exact 4→2, S2 decision chunk ≤3,
invariants C1/C2/C5 1/1/1. ADR nDCG@5 0.722 (W6: 0.650), MRR 0.929 (W6: 0.786), recall@5
0.617 (W6: 0.581) — every metric above every prior wave. Mechanism: adjacent-chunk boost +
per-source consolidation + document-first tie-break operate on the normalized-RRF fused list
(λ=0 short-circuits to identity; source-path queries pass λ=0). A5 exact moved 1→3 but
content-verified as same-file siblings above the decision (file rank 1 held) — the within-file
ordering artifact, not a knowledge regression.

### ADR chunk sibling competition
Long ADRs chunked by heading sections share the same `path`. RRF treats them as
independent — they compete rather than cooperate. No document-level relevance boost.

### Candidate window starvation
`CandidateWindowFor(limit=5)` = 100. With ~762 chunks (jsaa HEAD 0bb8ff8a, verified
2026-08-04 — the older "681 chunks" figure is a stale seed log line), expected chunks
ranked 20-100 in one modality may be excluded if the other modality fills the window.

### Baseline blind spots
The grading gap closed in Wave 5a (2026-08-04): the committed catalog (36 queries, 11
expected-source) now carries per-query `difficulty` (easy/medium/hard/very-hard — measured
hybrid exact-chunk rank for expected-source queries, structural dispersion for coverage)
and `relevanceGrade` (0-5: 5 = expected chunk fully answers, 4 = answer spans 2+ authoritative
chunks, 0 = ungraded coverage/non-evidential H1-H3) in `scripts/baseline-queries.json`,
pinned by `BaselineQueryCatalogTests`; corpus-integrity assertions now live in
`RetrievalBaselineTests` (rubrics + measured ranks: ai-raccoon-development →
`references/corpus-integrity-assertions.md`). **Wave 5b DONE (2026-08-04):** the catalog is
44 queries / 19 expected-source — S1-S6 structural set completed (all six section targets
≤ 3 at limit 10, definitions pinned in the plan's Appendix A), A8-A10 catalog
reconciliation (ADR-0013/0086/0014), per-category metrics incl. a Structural aggregate,
and the comprehensive final baseline report (ablation + stratification + integrity +
per-query table) appended to comparison-clean.md. Still open: graded-nDCG application of
the relevance grades and cross-chunk relevance checks. Adding queries has its own traps:
`ai-raccoon-development` → `references/baseline-catalog-additions.md` (sweep gate-set
pinning, probe-first measurement, limit dependence).

### Rank claims are limit-dependent — state the limit

The Wave 1 fallback trigger `hits <= max(TokenCount, limit)` makes the FTS result list
(and hence every fused rank) a function of the caller's limit. Measured (Wave 5b, A9
"What design system does the frontend use?"): limit 10 → exact@3 file@1; catalog
searchLimit 5 → the expected file leaves the top-5 entirely (top-5 = same-knowledge
frontend-architecture.md chunks). A "miss" at one limit is not a miss at another — every
per-query table, gate, and difficulty pin in this repo is measured at limit 10; the
catalog's `searchLimit` field (5) is the production default, not the measurement limit.
When a probe or a reviewer reports a rank, the limit is part of the claim.

### FTS5 bm25() zero-match Dapper crash

When an FTS5 MATCH returns zero rows (e.g. `adr AND governs AND uuid AND choice` — all
four tokens never co-occur in one chunk), the bm25() aggregate returns a result typed as
`Byte[]` (SQLite column affinity quirk on empty sets) instead of `Double`. Dapper's
record-ctor mapping then fails with `InvalidOperationException: A parameterless default
constructor or one matching signature...`. Fix: guard with a COUNT(*) pre-check or
query `rows > 0` before reading bm25; treat zero-match as a finding (empty result list),
not a crash. See `references/comparison-methodology.md` §Zero-match handling.

### Heading-path any-segment matching

If an ADR's Decision section carries sub-headings (`Decision > D9 — Orchestration`),
the last segment of the heading path is the sub-heading text, NOT `decision`. Matching
only the last segment misses the chunk entirely. Use **any-segment** matching:
`any(seg.lower() == "decision" for seg in heading_path.split(" > "))`. Both harnesses
and the shared scorer must agree on this rule — last-segment and any-segment produce
different section-hit counts (4/7 vs 6/7 on the old corpus).

### Probe with the REAL query text, not the assumed one

Retrieval probes must read the query from `scripts/baseline-queries.json` by id —
never reconstruct it from memory or from the expected source. A whole A1 debugging
session ran against "What is ADR-0011 about?" while the baseline A1 is actually "Why
was shadcn/ui chosen over gluestack.io?" — every probe rank was meaningless for the
gate. Same for limits: each baseline query carries its own `searchLimit`; the test
harness and the MCP tool default can differ (tool default minScore=0.7 vs test 0.0).

### Test-vs-server ranking divergence: check WHICH db file each side opened

Tests copy the corpus from the BUILD OUTPUT (`bin/Debug/net10.0/Resources/jsaa-memory.db`,
CopyToOutputDirectory=PreserveNewest); live probes copy from the source
`tests/AiRaccoon.Tests/Resources/`. After regenerating or backfilling the source db,
the build output can be stale — PreserveNewest compares mtimes and equal-second writes
can skip the copy, so `dotnet build` then `dotnet test` may still run against the old
db. The schema migration also runs on open, so a test's temp copy can diverge further.
When in-process test ranks differ from a server probe on the "same" db: `shasum` both
files (they can be byte-identical while logical state differs — check -wal/-shm sizes
too), rebuild to refresh the output copy, and re-run. A vec_structure backfill only
reaches the test db after a rebuild — an empty-structure test db produces
content-only-equivalent ranks and can flip hybrid results.

**First full-suite run after a build/merge can fail ONCE spuriously** (2026-08-04, seen
three times): one test fails (e.g. the hybrid no-regression gate), the identical re-run
passes — the build's PreserveNewest copy of the corpus db races the first test run, or
the sweep test's report regeneration collides with a parallel test. Re-run before
debugging; a stable re-run means the failure was the copy race, not the code.

### System sqlite3 cannot read vec0 tables

The macOS `sqlite3` CLI lacks the vec0 module: `SELECT count(*) FROM vec_structure`
fails with "no such module: vec0" — twice misread as a missing table. Plain tables
(settings, entries, entries_fts) work fine; vec0 tables must be inspected through the
app's Microsoft.Data.Sqlite (e.g. re-run the backfill tool, which prints counts) or a
live server probe. FTS5 tables DO work in the CLI.

### A parameter sweep that never changes anything = the parameter was never applied

`memory_configure` rejects unknown keys (e.g. structureAlpha) with a generic "An
error occurred invoking 'memory_configure'" — the real exception is only in the
server log, and the tool call looks like it succeeded if the probe swallows the
error. A swallowed failure then "swept" α=0.5..1.0 all at the default 0.5, and the
α-invariant results were wrongly read as "the structure signal dominates at every α".
When a sweep is invariant: read the settings row directly (plain sqlite3 works), check
the server log for tool errors, and confirm the parameter is read per-call (no cache).
Only then may you conclude the parameter genuinely doesn't matter.

### FTS5 has no stemming

`decide` ≠ `decision` in MATCH. Section-verb queries ("What does ADR-0011 decide?")
under-match the noun sections; this is a keyword-modality limit, not a bug — the
structure modality (heading paths) is the intended fix.

### Content-embedding keying (the 0/7 bug)

If embeddings are keyed by chunk TEXT but lookup is by chunk HASH, every content
similarity returns `0.0` — content-only arm scores identically zero. Symptom:
`content-only: Positive Results 0/35`. Fix: generalize `EmbedBatchedAsync` to accept
`(string Key, string Text)` pairs and key by the caller's identifier (hash for content,
heading-path string for structure). Verify by asserting content-only top-1 score > 0
in any test/verification run.
