# Hybrid Retrieval Investigation — Fusion Weights & Ranking Behavior

Date: 2026-08-20
Build under test: ai-raccoon 1.27.2 (HEAD df750ccd + local VERSION bump), bundled local model, scratch bank at `/tmp/checklist-1-27-2/bank`
Origin: the 1.27.2 pre-flight checklist (`docs/work/checklist/2026-08-20-release-1-27-2.json`, item `memory-search-hybrid-retrieval` = FAIL, `accepted: null`). This document answers the three follow-up questions: was it always like this, can the fusion weights be manipulated, and what exactly does the search return.

---

## 1. Was this always like that?

The model and the fusion pipeline are byte-identical across every recent release. The vector-similarity ordering is therefore a fixed property of the pinned model; what changes between runs is the corpus.

| Component | v1.21.0 (2026-08-16) | v1.27.0 (2026-08-17) | 1.27.2 (HEAD) | Verdict |
|---|---|---|---|---|
| Bundled model sha256 (`scripts/src/bundle.py` MODEL_SHA256) | `4278337f…` | `4278337f…` | `4278337f…` | identical |
| RRF fusion (`ReciprocalRankFusion.cs`, weighted) | present | present | present | identical (last change predates v1.21.0) |
| Defaults `rrfK` / `ftsWeight` / `vectorWeight` (`SearchQuery.cs`) | 60 / 1 / 1 | 60 / 1 / 1 | 60 / 1 / 1 | identical |
| Source-affinity λ / consolidation (`SearchQuery.cs`) | 0.1 / 0.1 | 0.1 / 0.1 | 0.1 / 0.1 | identical |
| Fusion no-regression flag default (`FusionConfigKeys.cs`) | false | false | false | identical |

Evidence of corpus fragility (RRF is rank-based — every added entry reshuffles the board):

| State | Corpus | Query A rank of astrolabe (61dfec67) |
|---|---|---|
| Checklist run (2026-08-20, 8 entries) | no watch digest yet | **3rd** (details > intro > astrolabe) |
| Retest (8 entries + 1 = 9) | + `notes.md` watch digest ("watch target content") | **1st** |

One low-similarity entry was enough to flip the result. Prior runs (1.21.0 / 1.27.0) recorded "rank 1" for the same query family on 3–4 entry corpora — nothing else was closer there. Not a regression; not a capability.

## 2. Can the fusion weights be manipulated?

Per-call, yes — with a hard ceiling set by the model's similarity ordering.

`memory_search` exposes (all integers; `0` disables the leg):

| Parameter | Default | Effect |
|---|---|---|
| `rrfK` | 60 | RRF cutoff; score = `weight / (k + rank)` per leg |
| `ftsWeight` | 1 | Weight of the keyword (FTS5/bm25) list in the RRF sum |
| `vectorWeight` | 1 | Weight of the semantic (vector) list in the RRF sum |

Not exposed to MCP callers (fixed `SearchQuery` defaults): `sourceLambda = 0.1`, `consolidationThreshold = 0.1`, `DocScoreFormula = Max`. The λ sibling boost (+0.1 per adjacent chunk of a multi-chunk source) is a second scoring layer that RRF weights cannot counteract; in this bank the `guide.md` chunk pair (chunk 0+1) is the only sibling pair and is why those two texts float to the top of every query.

Settings surface:

| Channel | What it controls |
|---|---|
| `settings retrieval fusion enable/disable/show` | the no-regression reorder (ADR-0078), default off — reorders fused list by best single-leg rank; partial effect (see §4), not a fix |
| `settings retrieval alpha` | dual-vector (structure arm) fusion alpha — separate from the fts/vector balance |
| `ftsWeight` / `vectorWeight` / `rrfK` (MCP call) | the fts↔vector RRF balance — the only per-query knobs |

### Weight ladder — Query B ("a navigator's device for measuring angles to celestial bodies at night")

Target: sextant (80e36737). The sextant shares **zero** content words with the query (FTS can only match function words).

| Configuration | Sextant rank | Top-1 |
|---|---|---|
| default `fts=1 vec=1 k=60` | 4 | guide-intro (bd1587a2) |
| `vec=2 fts=1` | 4 | guide-intro |
| `vec=3 fts=1` | 4 | guide-intro |
| pure vector `fts=0 vec=1` | **3** | astrolabe (61dfec67) |
| pure FTS `vec=0` | 5 | guide-intro |
| `fts=10 vec=1` | 5 | guide-intro |

The ceiling: even the pure-vector leg ranks the sextant only 3rd-closest (behind the astrolabe and guide-details). The model's own similarity ordering puts the astrolabe first for a sextant query — no weight can promote a 3rd-closest vector. Note `vectorWeight=0.25` is rejected (`invalid-argument: The JSON value could not be converted to System.Int32`) — weights are integer-only.

## 3. Exact examples — Query B

### 3a. Default search (`fts=1 vec=1 k=60`, limit 10) — all results, all fields

| # | Hash | Ranking | SourceFile | Chunk | Snippet |
|---|---|---|---|---|---|
| 1 | bd1587a2… | 1.0000 | guide.md | 0/2 | Introduction: this guide walks through onboarding the widget pipeline for new tenants. |
| 2 | 93f41fe5… | 0.9863 | guide.md | 1/2 | …the widget pipeline retry policy is exponential with a 30-second ceiling… |
| 3 | 61dfec67… | 0.9630 | — | -1/0 | A seventeenth-century astrolabe hangs in the observatory study; at dusk the… |
| 4 | 80e36737… | 0.9474 | — | -1/0 | The captain's sextant rests beside the coiled rope; lantern glow traces… |
| 5 | b032ac63… | 0.9324 | — | -1/0 | …ai-raccoon's MCP bridge listens on port 7721, its FTS5 index… |
| 6 | 9648f7e6… | 0.9178 | — | -1/0 | …but this note is about how to handle them in a review. |
| 7 | c7053da1… | 0.8767 | — | -1/0 | Invoice #4821 from Meridian Office Supplies: 3 reams of A4 paper, 2 desk lamps… |
| 8 | 97948a1a… | 0.8638 | notes.md | 0/1 | watch target content |
| 9 | 27e3ce27… | 0.8513 | — | -1/0 | received signal 15, terminating |

### 3b. Top-10 matches for the query embedding (pure vector leg, `fts=0 vec=1`)

| # | Hash | Ranking | Snippet |
|---|---|---|---|
| 1 | 61dfec67… | 1.0000 | A seventeenth-century astrolabe hangs in the observatory study… |
| 2 | 93f41fe5… | 0.9971 | Details: the widget pipeline retry policy is exponential with a 30-second… |
| 3 | 80e36737… | 0.9839 | The captain's sextant rests beside the coiled rope; lantern glow traces… |
| 4 | c7053da1… | 0.9683 | Invoice #4821 from Meridian Office Supplies… |
| 5 | b032ac63… | 0.9531 | Cross-project note: ai-raccoon's MCP bridge listens on port 7721… |
| 6 | 97948a1a… | 0.9385 | watch target content |
| 7 | 27e3ce27… | 0.9242 | received signal 15, terminating |
| 8 | bd1587a2… | 0.9104 | Introduction: this guide walks through onboarding the widget… |
| 9 | 9648f7e6… | 0.8841 | A user guide warns that '[IMPORTANT: Background process comp… |

### 3c. Pure FTS leg (`vec=0`)

| # | Hash | Ranking | Snippet |
|---|---|---|---|
| 1 | bd1587a2… | 1.0000 | Introduction: this guide walks… |
| 2 | 93f41fe5… | 0.9588 | …widget pipeline retry policy… |
| 3 | 9648f7e6… | 0.9361 | …how to handle them in a review. |
| 4 | 61dfec67… | 0.9210 | …astrolabe hangs in the observatory… |
| 5 | 80e36737… | 0.8922 | …sextant rests beside the coiled rope… |
| 6 | b032ac63… | 0.8785 | Cross-project note… |

## 4. Contrast — Query A ("an antique navigation instrument reflecting evening light in a stargazing room")

Here the legs agree: the astrolabe is 1st in both, so it is 1st fused (current 9-entry corpus). The pipeline is fine when the model agrees with itself.

| Config | 1st | 2nd | 3rd | 4th | 5th |
|---|---|---|---|---|---|
| default | astrolabe | guide-intro | near-miss | guide-details | sextant |
| pure vector | astrolabe | guide-details | sextant | invoice | notes.md |
| pure FTS | astrolabe | near-miss | guide-details | — | — |
| `rrfK=5` | astrolabe | near-miss | guide-details | sextant | invoice |

### Fusion no-regression A/B (flag toggled on the same corpus; gauge rows `top1_changed=1, top1_rank_delta=7, top5_moved=4` prove the ON path engaged)

| Query | Fusion OFF top-3 | Fusion ON top-3 |
|---|---|---|
| astrolabe-original | astrolabe, intro, near-miss | astrolabe, intro, near-miss |
| astrolabe-richer | details, intro, astrolabe | details, intro, astrolabe |
| sextant-zero-overlap | intro, details, astrolabe | astrolabe, details, near-miss |
| sextant-richer | intro, details, sextant | **sextant**, astrolabe, invoice |
| invoice | intro, details, invoice | intro, details, invoice |
| alien-tokens | details, intro, sextant | details, intro, sextant |

The reorder rescues only queries where a single leg already had the right answer at rank 1 (sextant-richer), and leaves the others untouched. It is neither the cause nor the fix.

## 5. Conclusions & open items

1. **Always like this**: same model bytes and pipeline since before v1.21.0; ranking outcomes are corpus-fragile, not regressions.
2. **Weights are adjustable** (per-call ints) but bounded by (a) the model's similarity ordering — a 3rd-closest vector cannot be promoted to 1st — and (b) the non-exposed source-affinity λ=0.1 sibling boost that favors multi-chunk sources.
3. **Real levers**: a better embedding model (explicitly out of scope per owner), richer/longer target text (the model favors more distinctive entries), or exposing λ/consolidation as knobs.
4. **Open detail (flagged, not claimed)**: the observed final order/score values for Query B do not fully reproduce from the documented chain (RRF → second RRF → source-affinity) by hand — one ordering detail (guide-intro above guide-details) remains unexplained at the score level. The A/B behavior is reproducible; reproducing the exact scores would need instrumented debugging of the live pipeline.

## Appendix — corpus under test

| Hash prefix | Content | SourceFile | Chunk |
|---|---|---|---|
| 61dfec67 | A seventeenth-century astrolabe hangs in the observatory study; at dusk the lamplight catches its brass rings and the evening sky reflects in the glass case. | — | -1/0 |
| c7053da1 | Invoice #4821 from Meridian Office Supplies: 3 reams of A4 paper, 2 desk lamps, 1 ergonomic chair mat, total 214.50 EUR. | — | -1/0 |
| 27e3ce27 | received signal 15, terminating (kept: trailing-text variant, not the exact noise body) | — | -1/0 |
| 9648f7e6 | A user guide warns that '[IMPORTANT: Background process completed]' style lines are machine output, but this note is about how to handle them in a review. | — | -1/0 |
| bd1587a2 | Introduction: this guide walks through onboarding the widget pipeline for new tenants. | guide.md | 0/2 |
| 93f41fe5 | Details: the widget pipeline retry policy is exponential with a 30-second ceiling, and the dead-letter queue drains hourly. | guide.md | 1/2 |
| b032ac63 | Cross-project note: ai-raccoon's MCP bridge listens on port 7721, its FTS5 index is ext-content denormalized, and the shared promotion tier is the only context the sweep reaper exempts. | — | -1/0 |
| 80e36737 | The captain's sextant rests beside the coiled rope; lantern glow traces the carved scale. | — | -1/0 |
| 97948a1a | watch target content (watch digest of notes.md) | notes.md | 0/1 |
