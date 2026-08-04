# Retrieval Improvement Plan C — Measured Structure (Revision 1)

> **Based on:** Plans A (source-first architecture) and B (measurement-first, algorithmic).
> **Revised:** 2026-08-04 — Wave 0 delivered, research comparison completed, plan updated to measured reality.

---

## 0. Wave 0: Reproducible Baseline — DONE ✓

Delivered by task `fix-baseline` (merge 08144d7). The canonical corpus and measurement
infrastructure are in place:

- **Corpus**: `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — 762 chunks, 762 embedded
  (all-MiniLM-L6-v2 in-process ONNX), project_id=`job-search-ai-assistant`. Paths are
  SHA256-derived hashes; structured path→hash mapping in `scripts/chunk-hash-map.json`.
  Curated content: `docs/adr/` (456), `ai-badger/` (78), `docs/explanation/` (20),
  `docs/` other (187). Excluded: `docs/work/`, `docs/state.json`, `docs/now.md`,
  `.ai-badger/state.json`.
- **Tests**: `RetrievalBaselineTests` — real expected-source matching (chunk-hash-map.xml.xml.inverted),
  corpus integrity assertions. `BaselineMetricsTests` — nDCG@5 / MRR / recall@5 per category,
  per-query ablation (hybrid / FTS-only / vector-only), determinism double-run.
- **Gate**: 414 passed, 0 failed (12 s). Two consecutive runs produce identical top-5 hashes at
  identical ranks. Report includes modality attribution per query.
- **Current metrics** (hybrid, ADR queries A1–A7): nDCG@5 0.642, MRR 0.893, recall@5 0.544.
  Invariant queries C1/C2/C5 (now reachable): nDCG@5 1.0, MRR 1.0. Key per-query:
  A2 rank 1, A7 rank 1, A6 rank 4, C1 rank 1.

---

## 1. What Works (verified on the reproducible baseline)

1. The hybrid (FTS5 BM25 + vector cosine, RRF k=60, 1:1) delivers A7 at rank 1 on the clean
   corpus — the identifier-query failure on the old corpus was a corpus-pollution symptom
   (cross-referencing ADRs + 71% docs/work noise), not a fundamental retrieval gap.
2. Invariants (C1/C2/C5) at rank 1 — short, keyword-dense documents dominate reliably.
3. Deterministic ONNX embeddings + SQLite determinism guarantees → identical top-5 hashes
   across consecutive runs.
4. Measurement infra: `SweepRunner`, `SweepMatrix`, `RetrievalMetrics`, `ManagedHarness`.

---

## 2. What's Broken — Root Causes (updated for measured evidence)

### 2.1 ADR chunk sibling competition (from A §2.1)

ADR-0067's chunks compete against ADR-0068's chunks. A6 "How does data erasure work?" returns
ADR-0068 at rank 1 but ADR-0067 at rank 4, degrading nDCG@5 to 0.146. The system has no
document-level identity — treating each chunk as an independent candidate.

### 2.2 FTS query construction loses precision (from A §4.2D)

`"What is ADR-0070 about?"` → `what OR is OR adr OR 0070 OR about`. Stopwords drown signal.
A7 is already rescued on the clean corpus by the hybrid's vector modality, so the FTS-only
failure is a diagnostic showing the FTS path's weakness, not an end-user blocker today. But
FTS-only ranks for identifier queries are fragile (the swept corpus showed A7 rank 11 under
pure FTS `adr AND 0070` — cross-referencing ADRs held more `adr`+`0070` occurrences).

### 2.3 Source identity is write-only (from B §2.2)

`memory_write(context="docs:adr")` stores `scope='custom'` — invisible to `SearchContexts.For`.
The ingest script writes structured path metadata into chunk content text, polluting BM25 and
making hash matching fragile.

### 2.4 No document-level ranking signal

Chunks from the same source compete as strangers. No adjacent-chunk boost, no source
consolidation, no document-first ranking. A6 is the canonical casualty.

### 2.5 AND-for-short query construction regresses (new — from retrieval-improvement-cont research)

On the old (polluted) corpus, applying AND semantics to queries with ≤4 content tokens zero-matched
11 of 35 queries, including A2 ("governs"/"choice" absent from ADR-0004). The hybrid on the clean
corpus masks this today (the vector side rescues A2 at rank 1), but the FTS-only path is fragile
and an AND-only regime will regress the moment the corpus grows or the vector modality is
unavailable. The mitigations are AND-with-OR-fallback (zero-match → retry OR) and the FTS source
column (identifier tokens match the path, not body text). **Not yet measured on the clean corpus.**
Evidence: tools/AiRaccoon.FtsPlanPrototype results-plan.md (prototype/dual-vector-alpha spike).

### 2.6 No section-targeted retrieval mechanism

The flat index treats all chunks equally; no signal separates a Decision-section chunk from a
Context-section chunk. Plan C's structural queries (S2 "What does ADR-0011 decide?", S4
"Consequences of ADR-0011?") cannot be answered by the current pipeline. The retrieval-improvement-cont
research measured a dual-vector approach (content embedding + heading-path embedding with
fixed-α≈0.58 fusion) that delivers structural hits — 6/6 section-targeted queries vs content-only
4/6 and FTS 1/6 — on the old (polluted) corpus, **but not yet re-measured on the clean 762-chunk
corpus.** The heading-path signals are present in the chunk content (## Decision headings); storing
and embedding them is a schema + pipeline change.

---

## 3. Improvement Plan (renumbered — old Wave 0 removed)

### Wave 1 — Query Construction: stopwords + bigrams + guarded AND

**Re-spec from original Wave 1 (AND-for-short removed; research showed it regresses).**

1. **Stopword removal** in `FtsQueryNormalizer`: strip `what, is, the, how, does, about, are, do,
   can, should, will, would, could, has, have, been, was, were, being, a, an, in, on, at, to,
   for, of, by, with, from`.
2. **Bigram phrase extraction**: for queries with ≥3 content tokens, add adjacent token pairs as
   quoted FTS5 phrases (e.g. `"shadcn ui"`). Under AND semantics (short queries), bigrams add no
   constraint — skip them.
3. **AND with OR fallback**: join remaining tokens with AND when ≤4 tokens; if the MATCH
   returns fewer rows than `max(content-token count, requested limit)` — an AND list that
   small cannot be a useful ranked signal on its own (A6/C2 measured cases) — retry with
   OR-join. The OR retry **includes bigrams** when the original content had ≥3 tokens (don't
   lose the precision signal when falling back). This captures AND's precision benefit for
   short queries while preventing the zero-match/under-match regression measured on the old
   corpus (A2, A6).
4. **Re-run full baseline** against the clean corpus, including the diagnostic triplet: Q1 "What
   is ADR-0070 about?" (full question), Q2 "ADR-0070" (identifier-only), Q3 "documentation
   structure trust model" (content-only).

**Gate**: No query regresses vs the Wave 0 baseline (every expected-source rank ≤ Wave 0 rank).
Diagnostic triplet confirms FTS-only path answers A7 on the clean corpus. ADDITIONAL FTS-only
guard: FTS-only file hit@5 ≥ 6/7 and FTS-only MRR ≥ 0.70 — no regression below the status-quo
F1 ranker (the best file-level MRR of all measured arms on the old corpus). The AND-fallback
prevents any zero-match.

---

### Wave 2 — Source as First-Class Citizen (structural foundation)

> **Status: DONE ✓ (2026-08-04, branch task/w2-source-identity, ADR 0003).** Delivered:
> `source_file` + `section` columns (migrated on open), weighted FTS (bm25 1.0/8.0/16.0),
> source identity on `MemorySearchResult`, provenance removed from chunk content,
> source-path queries matched against the source columns, searchable `contextLabel`.
> Measured deviations from the gate as written: (1) S2's Decision chunk ranks ~13
> FTS-only / beyond top-30 hybrid — FTS5 has no stemming (`decide`≠`decision`) and bm25's
> document-length normalization crushes the 13.8 KB decision chunk; the section-level ≤3
> target is Wave 6's dual-vector signal. (2) C2's hybrid rank collapsed (vector >100 on
> clean content); it holds at FTS-only rank 1, fusion weighting is Wave 4's sweep.

Schema changes to give the system document-level self-awareness.

#### 2a. Schema: add `source_file` column

```sql
ALTER TABLE entries ADD COLUMN source_file TEXT;
```

For ingested files: original relative path (e.g. `docs/adr/0011-frontend-chassis-stack.md`).
All chunks from the same file share the same `source_file`.

#### 2b. Expose source identity in `MemorySearchResult`

```csharp
public sealed record MemorySearchResult(
    string Hash, int Seq, double Ranking, string Path,
    string Snippet,
    string? SourceFile,      // original file path
    int ChunkIndex,           // position within source (0-based)
    int TotalChunks           // total chunks in source
);
```

#### 2c. Index source_path in FTS as a weighted column (the identifier-query fix)

Add the structured source path to the FTS index as a weighted column, so `ADR-0070` and
`decision` match the *source path*, not just body text. FTS5's `bm25(fts, weight_content,
weight_source)` per-column weights let source matches carry more signal than body-text matches.
This is the mechanism that addresses the A7 identifier-query case at the FTS level (research
showed A7 rank 11 under FTS-only `adr AND 0070` on polluted corpus — cross-referencing ADRs
held more term occurrences; the source column eliminates that competition).

Requires rebuilding `entries_fts` (currently external-content on `entries(value)`) to include
`source_file` as a weighted column — a non-trivial schema migration.

#### 2d. Stop embedding provenance in content (B's insight)

Remove `## Source:` header and `[context]` prefix from chunk text — move provenance to the
`source_file` column. This cleans BM25 scores and hash matching.

#### 2e. Make context labels searchable

Include `scope='custom'` rows when a context filter is requested, or map context labels into
the project scope with a `context_label` filter on `memory_search`.

**Gate**: S2 ("What does ADR-0011 decide?") finds the Decision-section chunk at rank ≤3.
Results carry source identity (SourceFile, ChunkIndex, TotalChunks populated for ingested
content). Q2 ("ADR-0070" identifier-only) returns ADR-0070 at FTS-only rank ≤3 — proving the
source-column fix works without the vector crutch. A query for a source path
(e.g. `docs/adr/0011-...#decision`) returns that exact chunk. No regression on invariants
(C1/C2/C5 at rank 1).

---

### Wave 3 — Source-Affinity Scoring (ranking improvement)

1. **Adjacent chunk boost**: chunk N±1 from the same source gets a λ boost (sweep λ ∈
   {0.05, 0.1, 0.2}).
2. **Source consolidation**: for each source file, take the best-scoring chunk and optionally
   merge its adjacent siblings into a single result.
3. **Document-first ranking**: after per-chunk scoring, compute a document score = max(chunk
   scores), use as a secondary sort key.
4. **Parameter sweep** over λ, consolidation threshold, and document-score formula against
   the full baseline.
5. **BM25 length normalization** investigation: on the clean corpus A7 is already hybrid rank 1,
   so BM25 length is a solved problem for the primary metric. **Deprioritized** — re-prioritize
   only if a new length-attributable regression surfaces (e.g. a future query where a long ADR
   chunk loses to a short doc under FTS-only).

**Gate**: A6's expected-source rank improves from 4 to ≤3; nDCG@5(ADRs) improves over
Wave 0 baseline. No regression on invariants.

---

### Wave 4 — RRF Parameter Optimization

Grid search using the existing `SweepMatrix`:
- k ∈ {10, 30, 60, 120}
- Weight ratios: (1:1), (1:2), (2:1)
- minScore ∈ {0.0, 0.3, 0.5, 0.7}
- Candidate window: max(limit×3, 100) vs max(limit×5, 50)

Select the Pareto-optimal point on nDCG@5 and MRR. Document in an ADR with sweep data.

**Gate**: Chosen parameters beat the current defaults (k=60, 1:1, minScore=0.0) on nDCG@5
without regressing invariants. RRF hybrid ≥ max(FTS-only, vector-only) for every expected-source
query (no fusion regression). Sweep results committed alongside ADR. **C2 acceptance (from the
Wave 2 integration analysis, 2026-08-04): C2 hybrid rank ≤ 3 after the sweep — restoring the
invariant's hybrid visibility lost when the 2d provenance cleanup removed the vector crutch
(hybrid 18 / FTS-only 1 / vector >100 at k=60, 1:1). If no sweep point achieves it, the fusion
design (weights/minScore/candidate window) is revisited before Wave 5b.**

---

### Wave 5 — Baseline Enrichment & Corpus Hygiene

Split into 5a (can run early) and 5b (needs the ranking waves).

#### 5a (after Wave 1 — guardrails before ranking work)
1. Add query difficulty stratification (easy/medium/hard/very-hard)
2. Add per-query relevance grading (1-5 scale)
3. Permanent corpus-integrity assertions in the harness

**Gate 5a**: H1-H3 negative tests pass because the corpus excludes the content. Query
difficulty strata defined and assigned.

#### 5b (after Waves 3+4+6 — measures final state)
4. Add structural and cross-document test cases (S1-S6 from Appendix A)
5. Compute nDCG@5, recall@5, MRR for all queries including structural cases
6. Publish comprehensive baseline report

**Gate 5b**: Baseline report includes ablation breakdown, retrieval metrics, difficulty
stratification, corpus integrity checks. Structural queries S1-S6 scored.

---

### Wave 6 — Section-Targeted Retrieval: Dual-Vector Structure Signal

> **New** (2026-08-04 — from retrieval-improvement-cont research, measured on old corpus.
> **Pre-gate**: re-run the dual-vector comparison on the clean Wave 0 corpus (762 chunks,
> all embedded) before proceeding. All numbers below are corpus-conditional — acceptance
> requires clean-corpus confirmation.

1. **Store heading path with each chunk**: parse markdown headings during ingest, assign each
   chunk the heading-path context it belongs to (e.g. `ADR-0011: Frontend Chassis Stack > Decision`).
2. **Generate a structure embedding**: embed the heading-path string as a second vector
   alongside the content embedding (2× vector storage per chunk).
3. **Fixed-α fusion**: `score = α × sim(q, content) + (1-α) × sim(q, structure)` with a
   configurable constant α (≈0.5–0.6). The per-query sigmoid α machinery adds nothing over a
   fixed blend (confidence is query-invariant; mean α clusters at ~0.58).
4. **Gate**: (a) Pre-gate: dual-vector comparison re-run on clean Wave 0 corpus demonstrating
   no regression vs content-only baseline. (b) Section-targeted queries (S2, S4) at rank ≤3.
   Section-level hit@5 over A1–A7 ≥ 4/6 on the clean corpus. No regression on content-only
   file-level ranks.

**Gate amendments (Wave 6 integration, 2026-08-04 — measured on the merged W1+W2+W6 state):**
- (b) measured: S4 Consequences-chunk ≤ 3 ✓; S2 file rank 1 ✓ but the Decision chunk ranks 5
  (top-1 is the ADR's metadata header — within-file sibling competition). **S2's decision-chunk
  ≤ 3 target moves to Wave 3's gate** (source-affinity/document-first ranking is the mechanism).
  Section-level hit@5 measured 6/6 ≥ 4/6 ✓.
- File-level trade (bounded, content-verified): A1 and A4 expected files move 1 → 2 — the
  rank-1 results are same-knowledge alternatives (A1: frontend-architecture.md#3 is the
  evidence section ADR-0011 links to; A4: behaviour-specification.md#3 states "The MCP server
  was deleted; see ADR-0060" — both chunks read and verified). A3 decision chunk 1 → 3
  (file rank 1 held). **Improvements from the same change: C2 hybrid restored to rank 1 (the
  Wave 4 C2 acceptance criterion is already satisfied), A6 file 4 → 2, A7 exact chunk restored
  to rank 4, ADR recall@5 0.559 → 0.581.**
- Open question (follow-up, not blocking): `structureAlpha` is read from settings but
  `memory_configure` cannot write it — the constant is effectively fixed at 0.5; expose the
  setting (or a dedicated tool) when α tuning is next needed.

**Research backing**: On the old (polluted 6675-chunk) corpus, the dual-vector with fixed-α=0.5
lifted section hits from 4/6 (content-only) to 6/6 and MRR(section) from 0.37 to 0.46–0.56.
FTS alone achieved 1/6. The heading-path information is present in the corpus markdown — storing
and embedding it is a schema addition. Spike: prototype/dual-vector-alpha branch,
`tools/AiRaccoon.DualVectorPrototype` + `scripts/compare-harnesses.py`.

**Cost**: 2× vector storage (content + structure, ~762+estimated-200 unique heading paths),
one extra embedding pass (~81 s for 6675 chunks on the old corpus; ~15 s for 762 on clean),
peak RSS ~0.8 GB (ONNX inference in-process).

---

## 4. Success Metrics

| Metric | Old | Current (Wave 0) | Target |
|--------|-----|-----------------|--------|
| Baseline reproducible from clean checkout | No | Yes | Yes |
| Expected-source match rate @3 | Unverifiable | 85.7% (6/7 ADR; C1/C2/C5 @1) | ≥90% |
| ADR-0070 found in results | No | Hybrid rank 1 | FTS-only rank ≤5 |
| ADR-0067 + ADR-0068 both in top 5 "erasure" | 0068@1, 0067@4 | 0068@1, 0067@4 | Both in top 3 |
| Invariant match rate @1 | 100% (of those in corpus) | 100% | 100% |
| Modality attribution per query | None | Yes | Yes |
| nDCG@5 / MRR / recall@5 computed | No | Yes | Yes |
| Excluded-corpus integrity | 71% pollution | 0% pollution | 0% |
| Source identity in results | No | No | Yes (Wave 2) |
| Section-targeted hit@5 | Not measured | Not measured | ≥4/6 (Wave 6, old corpus†) |
| Dual-vector MRR(section) | — | — | ≥0.40 (Wave 6, old corpus†) |

† Targets measured on the old (polluted 6675-chunk) corpus; re-measurement on clean corpus
required before Wave 6 acceptance (see Wave 6 pre-gate).

---

## 5. Out of Scope

- Embedding model change (all-MiniLM → OpenAI text-embedding-3) — separate evaluation
- Chunk size changes
- Cross-encoder re-ranking
- LLM query expansion
- Shared-context / workspace-scoped search improvements
- Cross-project retrieval

---

## 6. Open Questions

1. **Source-file contract across repos**: if jsaa ingests via its own pipeline, the structured
   path format must be agreed between repos.
2. **Heading-path storage format**: store as a column in the `entries` table, or compute from
   chunk content at search time? Storing requires schema migration + re-ingest; computing is
   fragile (heading parse must match ingest time). Spike proved storing is viable.
3. **Fixed α value**: ~0.5–0.6 measured on the old corpus. Needs re-measurement on clean corpus
   (expected to be stable — heading structure is corpus-invariant, not content-dependent).
4. **Dual-vector cost ceiling**: 2× storage + 2× embedding cost per chunk. Is the section-targeting
   benefit worth it? Gate number will answer.
5. **Structure embedding model**: same ONNX model (all-MiniLM-L6-v2) for both content and structure,
   or separate? Separate model adds model-size cost; same model dilutes the embedding space (both
   vectors from one model may co-vary). Spike used same model; the signal was still discriminative.
6. **Document-first ranking vs shared context**: shared entries have no `source_file`. How do they
   participate in document-first ranking?

---

## 7. Dependency Graph

```
Wave 0 (DONE ✓ — reproducible baseline)
 ├── Wave 1 (query construction) ── independent, can start now
 ├── Wave 2 (source identity)    ── independent, can start now
 │    ├── Wave 3 (source-affinity scoring) ── depends on 2 (needs source_file)
 │    │    └── Wave 4 (RRF sweep) ── depends on 3 (ranking changes affect sweep)
 │    └── Wave 6 (dual-vector) ── independent (heading-path parse + second embedding,
 │         does NOT need source_file — headings are in chunk content)
 ├── Wave 5a (integrity + difficulty) ── independent, can run after Wave 1
 └── Wave 5b (structural queries + final report) ── depends on 3 + 4 + 6
```

Wave 6 is parallel with Wave 3 — it touches the embedding/ranking pipeline (heading-path
storage + second vector + fusion) and does NOT depend on the source_file schema from Wave 2
(the heading hierarchy is reconstructible from chunk content text, not the source identity).

---

## 8. Differences From Plans A and B

| Aspect | Plan A | Plan B | Plan C (original) | Plan C (revision 1) |
|--------|--------|--------|-------------------|---------------------|
| First step | Source schema | Reproducible baseline | Reproducible baseline | ✓ DONE |
| Query construction | AND-for-short, bigrams, stopwords | Stopwords, identifier AND | Both merged | Stopwords + bigrams + AND-with-OR-fallback (AND-for-short removed — measured regression) |
| Identifier query fix | — | — | Wave 1 identifier AND | Wave 2c FTS source column (AND-based body matching fails — terms compete across docs) |
| Section-targeting | — | — | Out of scope | Wave 6 dual-vector structure signal (fixed-α fusion, measured mechanism) |
| Source identity | source_file + ChunkIndex/TotalChunks | FTS source column + searchable context | Both | Both |
| Ranking | Adjacent boost, consolidation, document-first | RRF sweep, minScore, BM25 length | Both sequenced | Both sequenced |
| Corpus hygiene | Not addressed | Wave 4 | Wave 5 | Wave 5, now with structural queries |

---

## 9. Appendix A — Query Catalog

> Unchanged from original Plan C (A1–A10 expected-source, C1–C5 invariant, H1–H3 hygiene,
> S1–S6 structural). Note: C1/C2/C5 are reachable since Wave 0 (ai-badger:invariants included
> in corpus). A8/A9/A10 have expected sources in chunk-hash-map.json but no matching queries
> in baseline-queries.json — a catalog reconciliation gap (Wave 5 addresses it).
