# C10 — shared-scope fusion-loss trace (P1 close-out round 2)

Date: 2026-09-10. Branch `task/air-full-hundred-query-parity-eval-lane-close2`,
base `44546818`. Golden: `docs/work/results-f1.json` (run C) /
`results-f1-run2.json` (run D) — **unchanged by this item** (verdict: per-spec).
Repro script: `docs/work/c10_shared_leg_trace.py` (sibling session's exact
`_retrieve_with_limit` replica with per-stage prints; defaults now resolve to
this lane).

Inputs: corpus `scripts/retrieval_tuning/corpora/project-corpus-100.json`
(shared rows **C019 / C065 / C081**), frozen store `/tmp/p1-full-store`, pinned
copy `/tmp/p1-live-copy.db`, quiesced base `/tmp/p1-eval-scratch-quiet-base.db`
(C11 recipe). All read-only. One heavy process at a time; port 7721 never
dialed.

## 1. Per-stage trace (harness pipeline)

```
cd scripts/retrieval_tuning && python3 memwatch.py --cap-mb 12288 \
  --log /tmp/p1-closeout2/rss/c10-trace-mem.log -- \
  python3 ../../docs/work/c10_shared_leg_trace.py --harness-dir . \
  --corpus corpora/project-corpus-100.json --store-dir /tmp/p1-full-store --offline
memwatch: peak=2493MB cap=12288MB result=exit(0)
```

| query | fts leg | vector leg | dedupe | pre-merge RRF | post-affinity | floor 0.6 | Take(8) |
|---|---|---|---|---|---|---|---|
| C019 | rank **1** (bm25 −34.5807) | absent | kept (leg n=100) | rank 28 (0.5755) | rank 28 (0.6932) | passes | **dropped** |
| C065 | rank **1** (bm25 −28.1021) | absent | kept (leg n=99) | rank 32 (0.6093) | rank 32 (0.6630) | passes | **dropped** |
| C081 | rank **1** (bm25 −42.4456) | rank 95 (0.3707) | kept (both legs) | rank 10 (0.7821) | rank 10 (0.8714) | passes | **dropped** |

Verdict lines (pasted):

```
  VERDICT: outside top-8 after floor (rank 28)      # C019
  VERDICT: outside top-8 after floor (rank 32)      # C065
  VERDICT: outside top-8 after floor (rank 10)      # C081
```

The stage that drops the anchor is **Take(8)** — the final limit — after the
anchor survived per-leg content-dedupe, RRF, affinity and the relative floor.
No knob changed (rrfK=60, weights 1/1, limit 8, floor 0.6, λ=0.1,
threshold 0.1, Max, Max3X100, structureAlpha=0.5 verified unchanged in
`/tmp/p1-full-store/params.json` and the collector's frozen-knobs gate).

## 2. Bank leg (ground truth on the same copy)

```
python3 memwatch.py --cap-mb 12288 --log /tmp/p1-closeout2/rss/bank-probe-mem.log \
  -- python3 /tmp/p1-closeout2/bank_probe.py      # start_server over the quiesced base
memwatch: peak=3482MB cap=12288MB result=exit(0)
```

`memory_search` (scope=shared, limit=8, minRelativeScore=0.6) returns
`evidenceByHash` with each served hash's `legs` (fts/vector ranks) and fused
`cosine`:

| query | anchor served at | anchor legs | anchor cosine | harness anchor pre-affinity |
|---|---|---|---|---|
| C019 | bank rank **1** | fts 1, **vector 25** | 0.354085 | rank 28 |
| C065 | bank rank **2** | fts 1, **vector 19** | 0.349145 | rank 32 |
| C081 | bank rank **1** | fts 1, **vector 23** | 0.325343 | rank 10 |

The bank's fused ranking puts the anchor top-8 not because its pipeline differs
(`fusion.noRegression.enabled.global=false`; no-regression reorder is off), but
because its **vector leg ranks the anchor 19–25** while the harness's vector
leg does not rank it at all (C019/C065) or ranks it 95 (C081).

## 3. Where the harness vector leg loses it

Exact-recall probe (recompute the dual-vector leg with an unbounded window;
peak 2212 MB):

| query | content filtered | anchor content sim | content rank-100 cut | anchor exact fused rank | fused rank-100 cut | anchor fused score |
|---|---|---|---|---|---|---|
| C019 | 237 | 0.739506 | 0.739156 | 119 (union) | 0.370264 | 0.369753 |
| C065 | 237 | 0.783934 | 0.785733 | 219 (union) | 0.393765 | 0.391967 |
| C081 | 237 | 0.741466 | 0.740887 | 110 (union) | 0.370992 | 0.370733 |

All three anchors sit just under the harness's own rank-100 dual-vector cut
(Δ 0.00026–0.0018). So the harness's vector leg drops the anchor *before RRF* —
not at dedupe/floor/affinity — and the RRF/affinity/floor stages then faithfully
keep it outside the top-8.

## 4. Why the vector leg ranks differ: the embedding seam

Stored vectors for the **same hash/text** compared bank-vs-harness
(1024-dim each, cosine):

| hash | content cos(bank, harness) | structure cos(bank, harness) |
|---|---|---|
| fa97c8d6cc… | 0.628 | 0.602 |
| 6e5389d93e… | 0.504 | 0.361 |
| anchor 0d0a610420… | 0.556 | (no structure vector in either) |

Running the **bank's own vec0 KNN with the harness's query embedding** likewise
does not reproduce the harness's Chroma similarities (bank top content sims
0.55–0.63 vs harness 0.74–0.91) — the two systems embed the same text
differently. Mechanism, read from the shipped artifacts and installed code:

- Bank: `~/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R/ai-raccoon.manifest.json`
  pins `"pooling": {"mode": "cls"}`, `"normalization": "none"`, `"queryInstruction": null`.
- Harness: the HF snapshot
  (`~/.cache/huggingface/hub/models--Salesforce--SFR-Embedding-Code-400M_R/snapshots/<rev>/`)
  ships **no `modules.json`** / sentence-transformers pooling config, so
  `HuggingFaceEmbedding` → `SentenceTransformer` takes
  `_load_default_modules` — whose own docstring (sentence-transformers 5.6.1)
  reads: *"Creates a simple Transformer + **Mean Pooling** model …, except for
  CausalLM-based models which use Last Token pooling instead. This is used as a
  fallback when no pre-trained SentenceTransformer model is found."*

CLS vs mean pooling over the same weights is the divergence; it is a
leg-input difference, not a fusion-path difference.

## 5. Verdict (C10)

- **Stage**: `Take(8)`. The anchor passes every fusion stage and the relative
  floor; the limit drops it because 27/31/9 dual-leg candidates outrank it in
  the harness's fused list.
- **Judgment: per-spec.** The harness's dedupe/RRF/affinity/floor are faithful
  to the frozen contract (all knobs untouched); the same harness pipeline,
  given the bank's vector ranks (19–25), serves the anchor. The divergence
  upstream of fusion is the known bank-ONNX-vs-harness-HF embedding seam, now
  measured (CLS vs mean pooling, stored-vector cos 0.50–0.63). No code fix is
  taken: changing pooling would require re-embedding the 56,321-row store and
  would change every vector-leg number — a P2/P3-scale decision, not a C10
  knob fix. No regression test is added for the same reason (nothing to
  regress; the trace is the evidence).
- **P2 AC2 marking**: C019/C065/C081 are **fusion-drop** (the FTS leg held the
  anchor at rank 1 in the candidate window; the final output dropped it),
  **never "embedding-gap evidence"**. Note for P2: "fusion-drop" here does not
  assert a fusion bug — it is the row-level label the AC2 amendment defines
  ("a leg had it and fusion dropped it"); the trace above is the evidence that
  the drop is the limit exercised by embedding-seam score differences.
- **Golden**: unchanged (`results-f1.json` / `results-f1-run2.json` stay run
  C/D).
