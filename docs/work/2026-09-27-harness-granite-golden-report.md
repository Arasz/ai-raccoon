# LlamaIndex fusion harness — eval report

Date: 2026-09-27. Corpus: project-corpus-100.json (evaluated 99 of 100).
Bank copy entries: 70763. Ingested rows: 70626 (content=70626, structure=13919, fts=70626, headed=13919).
Model: ibm-granite/granite-embedding-small-english-r2 (99.0 MB HF cache). Store: 838M.
Provenance: weights revision 2ab6fa8ea2d6... (98950970 bytes); bank copy /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-harness-granite-golden-regen-726/.harness-run/memory-copy.db (sha256 cb99fe6ecbe0...).
Stale anchors (1 — unhittable by either leg: absent anchors still score into d, null-anchored rows are filtered pre-eval and unscored): C048.
Stale-anchor invariant (asserted): staleAnchors ∩ scored = ∅.

## Scope and routing

M1: each query ran at its own corpus targetProjectId/targetScope on BOTH systems (no 75-row file-targeted restriction). The harness store ingests every targeted bucket (project buckets incl. custom scopes, plus the global shared tier).
Resolved project buckets (11): 01a09156-d614-7656-b186-f0b6cca0a727, 01a0dad2-a98f-76bc-bed5-906e95b4627f, ai-badger, ai-raccoon, ai-sheepdog, arasz-home-page, dotnet-ignore, hermes-default, jsaa, pi-badger-integration, vue-kanban.
Additional shared-tier spellings present in the store (raw project_id folds; those rows are global and are served by shared/all queries): aib/shared (1), job-search-ai-assistant/shared (8).
Both systems ran at a uniform limit 8 (the corpus records searchLimit=8 for all 100 queries; candidate window max(limit*3,100)=100 either way).

## Method

- ai-raccoon leg: scratch server over a bank copy via scripts/src/retrieval_tuning/server.py + mcp.py (M2 — BumpAccessAsync mutates the live bank, so no live-bank searches); settings-driven call shape, per-query routing, kind=memory.
- Harness leg: FusionRetriever over Chroma content + structure collections, dual-vector fused at structureAlpha=0.5 (M3 — content-only fork NOT taken).
- Metrics (M6 forks from scoring.py, justified): hit = EXACT served hash equality (scoring.resolve_gain allows prefix + source fallback — would credit near-misses as parity); per-query F1 vs the singleton expected set (hit-rate PRIMARY, mean F1 secondary — a singleton collapses nDCG/MRR to rank-discounted hits, while F1's precision term prices the served-set size the 0.6 floor + Take(8) shape); agreement MCC over the paired hit/miss table (single-system MCC is degenerate — actual=1 every trial). Transport failures are recorded per query and excluded pairwise.

## Per-query results

| id | target | exp | harness hit/F1 | ai-raccoon hit/F1 | agree | gap | fts/vec | error |
|---|---|---|---|---|---|---|---|---|
| C001 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 48fac3133fc7 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C002 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 3a4490a8a30f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C003 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 599c5a8371f6 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C004 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 77023fd6a707 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C005 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 25e5756c2e44 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C006 | 01a09156-d614-7656-b186-f0b6cca0a727/project | ae6e09221a5c | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C007 | 01a09156-d614-7656-b186-f0b6cca0a727/project | 3eacfba8931a | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C008 | 01a09156-d614-7656-b186-f0b6cca0a727/project | d3f2d2169549 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C009 | 01a0dad2-a98f-76bc-bed5-906e95b4627f/project | 2ece0585a555 | 1 0.500 | 1 0.667 | yes | none | 1/1 |  |
| C010 | ai-badger/project | 7c4f6b400f5d | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C011 | ai-badger/custom | 0dcc1fc63d2d | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C012 | ai-badger/custom | df5974aff72d | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C013 | ai-badger/custom | 1c5c1ccaf89c | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C014 | ai-badger/custom | ab58f6ec744d | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C015 | ai-badger/custom | 49cb58160fbc | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C016 | ai-badger/custom | 2e73e13779a9 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C017 | ai-badger/project | 26d6489c6a94 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C018 | ai-badger/project | 17c5cacbf111 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C019 | ai-badger/project | 0d3d437fbe67 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C020 | ai-badger/project | 0cb4d4ddf5be | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C021 | ai-badger/project | fccaaeb0353a | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C022 | ai-badger/project | a91abd2b9b9c | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C023 | ai-badger/project | 13f95b60c4e6 | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C024 | ai-badger/project | 4162a66c4da8 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C025 | ai-badger/project | 1f421ce04d99 | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C026 | ai-badger/project | 0acd1186eeee | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C027 | ai-badger/project | 04dbcb06a76e | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C028 | ai-badger/project | 23de79dde94f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C029 | ai-raccoon/project | 85c101758538 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C030 | ai-raccoon/project | d5d95c9f9d1a | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C031 | ai-raccoon/project | 00afb3f82f32 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C032 | ai-raccoon/project | e200f554de3f | 1 0.222 | 1 0.286 | yes | none | 1/1 |  |
| C033 | ai-raccoon/project | 78a5fb010bba | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C034 | ai-raccoon/project | a2d3879fad7f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C035 | ai-raccoon/shared | 0d0a610420e4 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C036 | ai-raccoon/project | 55b9d1ac0fbd | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C037 | ai-raccoon/project | c7524ceb7a10 | 0 0.000 | 0 0.000 | yes | none | 0/1 |  |
| C038 | ai-raccoon/project | c055b5d30808 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C039 | ai-raccoon/project | 1ff737b19a7f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C040 | ai-raccoon/project | 011efa03c3e3 | 0 0.000 | 1 0.222 | NO | fusion | 1/1 |  |
| C041 | ai-raccoon/project | e209aaafccf3 | 1 0.222 | 1 0.286 | yes | none | 1/1 |  |
| C042 | ai-raccoon/project | ac91488af92f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C043 | ai-raccoon/project | f4503d8a44de | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C044 | ai-raccoon/project | 8eab433bfce5 | 1 0.222 | 1 0.286 | yes | none | 1/1 |  |
| C045 | ai-raccoon/project | 8eede041b5b2 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C046 | ai-raccoon/project | 59815d48da65 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C047 | ai-raccoon/project | 3f3a0376d543 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C049 | arasz-home-page/project | 50c2ea84d821 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C050 | arasz-home-page/project | 0497aef36697 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C051 | arasz-home-page/project | 2c957dc9c2f6 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C052 | arasz-home-page/project | 0de8251cc91a | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C053 | arasz-home-page/project | 0af4aeb69d78 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C054 | arasz-home-page/project | 55af47a5d4f2 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C055 | arasz-home-page/project | 6449a78548f2 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C056 | arasz-home-page/project | 01ac2bef58f5 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C057 | arasz-home-page/project | 25aa4e275ce2 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C058 | dotnet-ignore/project | 5cf7568210ec | 1 0.222 | 1 0.250 | yes | none | 1/1 |  |
| C059 | hermes-default/project | 63345f6d0836 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C060 | hermes-default/project | 80e56abbdb90 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C061 | hermes-default/shared | 05b04fbb5cf9 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C062 | hermes-default/project | f38875e87985 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C063 | hermes-default/custom | 3494d80b6a27 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C064 | hermes-default/project | 7e8d4cbc9c2c | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C065 | hermes-default/custom | 19ad9a374152 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C066 | hermes-default/project | 970554b90178 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C067 | hermes-default/project | 07e8e2c642aa | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C068 | hermes-default/project | 6f79a3eed975 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C069 | hermes-default/custom | 3545f913f5f6 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C070 | jsaa/custom | 664b9ed26e95 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C071 | jsaa/project | 6884122d21d4 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C072 | jsaa/project | ded4552d1090 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C073 | jsaa/project | 4eaad4ad146f | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C074 | jsaa/custom | a6fa1836ae0b | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C075 | jsaa/custom | 588934db9e81 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C076 | jsaa/project | dfa361827db0 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C077 | jsaa/project | 37aabeae4c6b | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C078 | jsaa/project | 616d2ffb345e | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C079 | jsaa/project | c99a5865b836 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C080 | jsaa/project | e682da40d0a9 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C081 | jsaa/project | 7b4d438d296e | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C082 | jsaa/project | 4a8ed3805364 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C083 | jsaa/custom | 1b34c781b807 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C084 | jsaa/project | 5ad97d48515a | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C085 | jsaa/project | 36753f7277d9 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C086 | jsaa/project | 78748605e358 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C087 | jsaa/project | 25443ff11389 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C088 | jsaa/shared | 4293525e9323 | 1 0.222 | 1 0.400 | yes | none | 1/1 |  |
| C089 | pi-badger-integration/custom | 48d3c3006a4b | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C090 | pi-badger-integration/project | b1248d2283ca | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C091 | pi-badger-integration/project | 3381b30488a6 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C092 | pi-badger-integration/project | a6192fdcf777 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C093 | pi-badger-integration/project | f832953d029a | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C094 | vue-kanban/project | 09d99638900d | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C095 | vue-kanban/project | 14bdd05072eb | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C096 | vue-kanban/project | 01403e0d71a0 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C097 | vue-kanban/project | 077a5790544b | 1 0.222 | 1 0.667 | yes | none | 1/1 |  |
| C098 | vue-kanban/project | 8521a5f3877e | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C099 | vue-kanban/project | 000c8046f2db | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C100 | vue-kanban/project | 006f5c1ef2b1 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |

## Aggregates

Queries: n=99, paired (both legs clean)=99.
Harness: hit-rate=0.909 (n=99), mean F1=0.205.
ai-raccoon: hit-rate=0.919 (n=99), mean F1=0.217.
Agreement table: both-hit a=90, harness-only b=0, ai-raccoon-only c=1, neither d=8 (cells sum to 99).
Agreement MCC: 0.9376.
Recompute path: results.json preserves per-query served hashes plus the harness fts_hit/vector_hit legs, so post-hoc relevance metrics recompute from the hash-preserving golden without new retrieval (rerun report.py on results.json). Singleton-F1 here is a parity verdict, not a relevance verdict.

### Repeat-run spread (1 independent runs, fresh bank scratch per repeat; min/max are across runs)

| metric | mean [min–max] |
|---|---|
| harness hit-rate | 0.9091 [0.9091–0.9091] |
| ai-raccoon hit-rate | 0.9192 [0.9192–0.9192] |
| harness mean F1 | 0.2048 [0.2048–0.2048] |
| ai-raccoon mean F1 | 0.2172 [0.2172–0.2172] |
| agreement MCC | 0.9376 [0.9376–0.9376] |

Unstable served-set query ids across repeats: harness=none, ai-raccoon=none. A non-empty list is jitter a golden must not tolerate — the run exits nonzero and never tolerance-blesses a moved anchor.
Variance guarded: weight drift (weights revision 2ab6fa8ea2d6... / 98950970 bytes pinned), server nondeterminism (fresh scratch copy + fresh server per repeat), and bank drift (row-stability snapshot per repeat, C11). Process-level harness determinism remains the P1 C7 double-run gate; repeats here share one read-only harness store.

### Query-composition stratification (recomputed from the scored corpus rows)

| stratum | n | harness hit-rate | ai-raccoon hit-rate | harness mean F1 | ai-raccoon mean F1 |
|---|---|---|---|---|---|
| debris (query text carries markup/JSON debris) | 0 | — | — | — | — |
| clean (natural-language query) | 99 | 0.909 | 0.919 | 0.205 | 0.217 |

Disclosure: relevance-flavoured readings ("bank finds what harness misses", any embedding-gap narrative) are **composition-sensitive** — the debris stratum is a tool-call-artifact subset whose stratified rates differ from the clean stratum, so aggregate gaps partly reflect corpus composition. The parity/pipeline reading (identical fusion pipeline on both legs, exact-hash hits) stands. The split is recomputed here from the scored rows' query text by signature (regex adapted from docs/work/mmr_transfer_checks.py), never a hardcoded id list.

## Parity-gap discussion

- Embedding seam (granite re-baseline): both systems embed with ibm-granite/granite-embedding-small-english-r2 — the harness loads it through sentence-transformers, which reads the checkpoint's own `1_Pooling/config.json` (`pooling_mode_cls_token=true`) and CLS-pools automatically, matching the product's ONNX graph (`pooling.mode: model-output` — the graph pools CLS internally) plus the L2 normalization `OnnxEmbeddingGenerator.PoolAlreadyPooledOutput` applies afterward (src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs). The remaining runtime seam, if any, is the harness's HF/PyTorch path vs the product's fp16 ONNX export, not a pooling or normalization difference. The taxonomy below labels rows against MEASURED leg positions and never claims the harness embedding equals the bank's: a row whose FTS window held the anchor is `fusion` even when the vector leg missed (C10), and only rows where BOTH windows missed are `embedding`/`unrecoverable` (composition-aware, C9).
- Structure gap: 13919 of 70626 rows carry heading_path structure texts (structureAlpha=0.5 fuse; missing structure scores 0). Section-targeted misses on unheaded rows are structure-gap, not fusion-gap.
- No harness knob was tuned to close either gap (plan: measure and report, never tune silently).

### Classified gap taxonomy (the single table; P1 provisional counts consumed, never redefined)

Labels are computed per row from the existing `fts_hit`/`vector_hit` columns, which are CANDIDATE-WINDOW hits (max(limit*3,100)=100), never each leg's top-8: a window-rank 9–100 hit counts as held and therefore labels `fusion`, not `embedding`. Harness-relative and restricted to the c-cell (bank-hit/harness-miss); agreement is never misattributed as deficit.

| label | n | reading |
|---|---|---|
| none | 98 | no harness deficit under test (harness hit, or the bank missed too — agreement is never a deficit) |
| fusion | 1 | a leg's candidate window held the anchor; the fused pipeline did not serve it (dedupe/RRF/floor/Take(8)) |
| embedding | 0 | no leg window held it on clean prose — representation side (the bank-ONNX vs harness-HF runtime seam qualifies this, C13/C14) |
| unrecoverable | 0 | no leg window held it on a debris/tool-call artifact — no representation recovers it |
| unknown | 0 | leg diagnostics absent (data gap) |
| c_cell (bank-hit/harness-miss of 99 paired) | 1 | deficit labels sum; unknown share 0.0% (cap 5%) |

### Fusion-drop attribution (package D: where in the pipeline the anchor was lost)

| label | n | reading |
|---|---|---|
| fusion_take | 1 | cleared the relative floor but ranked beyond Take(limit) |
| fusion_floor | 0 | ranked in the fused pipeline but below the relative floor |
| unattributed | 0 | fusion-labelled but the per-row rank columns are absent (older artifact, or carried under a different served hash by content-dedupe) |
| fusion (n=1) | — | must equal the `fusion` row of the classified gap taxonomy above |

C10 trace note: shared-scope rows (C035, C061, C088) were traced stage-by-stage — the anchor survived per-leg content-dedupe, RRF, affinity and the relative floor, and was dropped by Take(8) after dual-leg candidates outranked it (the harness/bank vector scores diverge at the embedding seam, measured). These rows are fusion-drop; never mapped to "embedding-gap evidence".

Excluded projects (2, seed-equal with the corpus header excludedProjects — documented there, never ad hoc):
- aib → ai-badger (committed=0, shared=1, embedded=1): raw entries.project_id 'aib' folds to canonical 'ai-badger' under the search gate; it has no committed project/custom rows, and its 1 scope='shared' row is global, remains ingested and is served by shared/all queries
- job-search-ai-assistant → jsaa (committed=106, shared=8, embedded=114): raw entries.project_id 'job-search-ai-assistant' folds to canonical 'jsaa' under the search gate, so its 106 committed project/custom rows can never be served; its 8 scope='shared' rows are global, remain ingested and are served by shared/all queries
