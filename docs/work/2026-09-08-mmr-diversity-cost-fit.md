# Research: MMR diversity re-ranking for ai-raccoon memory search

**Date:** 2026-09-08
**Question:** Would adding maximal marginal relevance to ai-raccoon's memory search improve result diversity enough to justify its cost, and where would it fit in the current pipeline?

```chart:bars
title: distinct topics in top-5 by lambda (synthetic refund corpus)
lambda 1.0 (pure relevance): 1
lambda 0.7: 1
lambda 0.5: 4
lambda 0.3: 5
```

```chart:range
title: MMR rerank ms by candidate count (k=10, pure-Python, Apple M4)
N=100: 0.35..0.35..0.50
N=300: 1.12..1.12..1.59
```

```chart:range
title: pairwise similarity-matrix build ms (pure-Python cosine, dim=32, Apple M4)
N=100: 4.44..4.46..4.80
N=300: 40.34..40.41..59.88
```

## Findings

### F1 — MMR selects greedily on relevance minus a redundancy penalty, with λ as the dial [READ]

MMR builds the result list one document at a time: the first pick is the most
relevant candidate, and each later pick maximizes
`λ · sim(d, query) − (1−λ) · max similarity(d, anything already picked)`.
At λ=1.0 it is identical to plain top-k; the 0.5–0.7 band is the usual working
range; below ~0.3 diversity dominates and marginal, barely-relevant documents
start appearing. This matters because it bounds what MMR can promise here: it
re-orders a fixed candidate pool, it never recalls anything the legs missed.

**Evidence:** https://www.elastic.co/search-labs/blog/maximum-marginal-relevance-diversify-results and https://vizlearn.in/gen_ai/maximal_marginal_relevance.html (formula, greedy procedure, and the λ table as summarized in the task brief).

### F2 — ai-raccoon's pipeline has no cross-source diversity step; its dedup only sees same-file adjacency [READ]

The read path is per-context FTS + dual-vector candidates, fused once by
reciprocal-rank fusion, then passed through `SearchResultMerger.Merge`, which
re-fuses and applies `SourceAffinityRanker` before the relative-score floor and
limit. The affinity pass boosts adjacent chunks of the same source (λ=0.1
default), merges a weak adjacent sibling into its file's best chunk
(threshold 0.1), and tie-breaks document-first — all keyed on `SourceFile` +
`ChunkIndex`. Near-duplicate content arriving from *different* sources (the
handbook/FAQ/macro/terms-page case in the brief, or the same text re-ingested
under another path or scope) shares no `SourceFile`, so none of the three
mechanisms fires on it, and the top-k can fill with restatements of one thing.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:595` (`ExecuteSearchPipeline`), `:655` (`SearchResultMerge`), `:669` (`FuseWithEvidence`); `src/AiRaccoon.Infrastructure/Sqlite/Memory/SearchResultMerger.cs:12-31`; `src/AiRaccoon.Infrastructure/Sqlite/Memory/SourceAffinityRanker.cs:11` plus the `SiblingCount`/`Consolidate` guards on same-`SourceFile` adjacency; defaults `src/AiRaccoon.Core/Memory/SearchParameterSettingsKeys.cs:31-32`; `docs/adr/0005-source-affinity-ranking.md:32-45`.

### F3 — On a redundant synthetic corpus, plain top-5 wastes four slots and MMR recovers the missing facets, but λ=0.7 does not suffice [MEASURED]

A 15-document corpus (5 near-duplicate policy statements at cosine ~0.999 to
each other, one doc each for exceptions/timescale/process, 5 distractors) with
a policy-leaning query ranks the five duplicates at relevance 0.958–0.969
against 0.457 for each facet. Greedy MMR at λ=1.0 and λ=0.7 both return five
copies of topic A (1 distinct topic); λ=0.5 returns A/B/C/D (4 distinct
topics); λ=0.3 returns 5 distinct topics but spends one slot on a distractor —
the diversity-heavy marginal pick the λ table warns about. Deterministic
computation, identical selection on all three runs. Two corrections to the
brief's telling: the "usual default 0.7" fails when near-dupes outscore facets
by a wide relevance gap (penalty 0.3·0.999 < edge 0.7·0.512), and the
"first pick is the most relevant" holds only for λ>0 — at λ=0 the first
iteration is an all-zero tie and the pick is arbitrary (observed: A1 picked
over higher-relevance A2).

**Evidence:** `python3 /tmp/mmr_experiment.py`, three consecutive runs on Apple M4 (Darwin 25.6.0 arm64, Python 3.14.7), pure-Python cosine MMR over the hand-built vectors in the script; selections byte-identical across runs as quoted above (near-dupe sim 0.9986 vs cross-topic sim 0.3304).

### F4 — Given pairwise similarities, the rerank itself is sub-millisecond to ~1.6 ms; building the similarity matrix dominates by 10–40× [MEASURED]

With similarities precomputed, selecting k=10 costs 0.35–0.50 ms at N=100 and
1.12–1.59 ms at N=300 candidates (3 process runs each, min..typical..max).
Computing the N² cosine matrix in pure Python costs 4.44–4.80 ms (N=100) and
40.34–59.88 ms (N=300, dim=32). The shape is the finding, not the constants: a
native (sqlite-vec) dot product shrinks both, but the O(k·N) selection stays
trivial while the O(N²·d) matrix stays the bill — and ai-raccoon's candidate
window is `max(limit·3, 100)`, so the default limit-10 search already pays the
N≈100 end of that bill. Capping the MMR input (e.g. fused top-50 → pick 10)
bounds the quadratic term without touching recall from the legs.

**Evidence:** same command, machine, and runs as F3 (microbenchmark section of `/tmp/mmr_experiment.py`); candidate-window sizing from `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Search.cs:25`.

### F5 — The fitting insertion point is post-consolidation, pre-limit, gated behind a new opt-in parameter, using fetched vectors or a cheap proxy [INFERRED]

Reasoning from F2 (no cross-source diversity step; consolidation is the last
dedup before the floor/limit), F3 (MMR needs a relevance score per candidate —
the fused `Ranking` — plus inter-candidate similarity), and F4 (the matrix is
the cost). Concretely: run MMR inside `SearchResultMerger.Merge` after
`SourceAffinityRanker.Rank` and before the `minRelativeScore`/`Take(limit)`
truncation (`SearchResultMerger.cs:31-36`), so same-file adjacency is already
settled and the floor still applies to diversified picks. Two costs have no
home yet: `MemorySearchResult` carries no vector (`MemorySearchResult.cs:3`),
so inter-candidate similarity needs either fetching candidate vectors
(extra reads on the search connection) or a proxy that reuses resident data
(same-`SourceFile` bonus is already spent; token-overlap or query-leg
co-occurrence would be new code), and λ/threshold need the standard
`SearchParameters` treatment (query override → `retrieval.*` setting →
validated default) rather than a hardcoded constant, or the 0.7-default trap
in F3 ships as a no-op. Keep it default-off like the no-regression flag until
a live-bank A/B (F6) shows a win.

### F6 — Whether real banks are redundant enough for any of this to matter is unmeasured [UNVERIFIED]

No live-bank A/B ran: nothing here shows what share of top-10s on the actual
bank are cross-source near-dupes versus usefully adjacent chunks the
consolidation pass already handles. A one-query probe (e.g. a question whose
answer is restated across several ingested docs, comparing served sets at
λ=1.0 vs λ=0.5) would settle existence; a small relevant-set graded for
facet coverage would settle value. Not run for lack of a graded bank-side
fixture, not for lack of mechanism.

## Still open

- What fraction of real served top-10s are cross-source near-dupes? Settles F6's existence half; needs a bank scan for high-cosine pairs with different `SourceFile`, not a new feature.
- Which similarity signal is cheapest with acceptable quality — fetched content vectors via sqlite-vec, structure vectors, or a token-overlap proxy? Settles the F5 design fork; needs a like-for-like prototype on the real store.
- Where should λ default if shipped, given F3 shows 0.7 can be a no-op under large relevance gaps? Needs the same graded fixture as F6, swept over λ ∈ {0.3, 0.5, 0.7}.
- Does MMR belong before or after the `minRelativeScore` floor — should a diverse-but-weak facet survive, or stay floored? A product call disguised as a pipeline detail; the record takes no position.
