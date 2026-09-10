# LlamaIndex fusion harness — eval report

Date: 2026-09-10. Corpus: project-corpus-100.json (evaluated 99 of 100).
Bank copy entries: 56457. Ingested rows: 56321 (content=56321, structure=15898, fts=56321, headed=15898).
Model: Salesforce/SFR-Embedding-Code-400M_R (829M HF cache). Store: 910M.
Provenance: weights revision cb950dc80d67... (869254400 bytes); bank copy /private/tmp/p1-live-copy.db (sha256 e0434a7214ac...).
Stale anchors (1 — unhittable by either leg: absent anchors still score into d, null-anchored rows are filtered pre-eval and unscored): C035.
Stale-anchor invariant (asserted): staleAnchors ∩ scored = ∅.

## Scope and routing

M1: each query ran at its own corpus targetProjectId/targetScope on BOTH systems (no 75-row file-targeted restriction). The harness store ingests every targeted bucket (project buckets incl. custom scopes, plus the global shared tier).
Resolved project buckets (11): ai-badger, ai-raccoon, ai-sheepdog, arasz-home-page, deepseek-harness, dotnet-ignore, hermes-default, interview-tasks, jsaa, pi-badger-integration, vue-kanban.
Additional shared-tier spellings present in the store (raw project_id folds; those rows are global and are served by shared/all queries): aib/shared (1), job-search-ai-assistant/shared (8).
Both systems ran at a uniform limit 8 (the corpus records searchLimit=8 for all 100 queries; candidate window max(limit*3,100)=100 either way).

## Method

- ai-raccoon leg: scratch server over a bank copy via scripts/src/retrieval_tuning/server.py + mcp.py (M2 — BumpAccessAsync mutates the live bank, so no live-bank searches); settings-driven call shape, per-query routing, kind=memory.
- Harness leg: FusionRetriever over Chroma content + structure collections, dual-vector fused at structureAlpha=0.5 (M3 — content-only fork NOT taken).
- Metrics (M6 forks from scoring.py, justified): hit = EXACT served hash equality (scoring.resolve_gain allows prefix + source fallback — would credit near-misses as parity); per-query F1 vs the singleton expected set (hit-rate PRIMARY, mean F1 secondary — a singleton collapses nDCG/MRR to rank-discounted hits, while F1's precision term prices the served-set size the 0.6 floor + Take(8) shape); agreement MCC over the paired hit/miss table (single-system MCC is degenerate — actual=1 every trial). Transport failures are recorded per query and excluded pairwise.

## Per-query results

| id | target | exp | harness hit/F1 | ai-raccoon hit/F1 | agree | gap | fts/vec | error |
|---|---|---|---|---|---|---|---|---|
| C001 | ai-badger/project | 7c4f6b400f5d | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C002 | ai-badger/custom | 0dcc1fc63d2d | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C003 | ai-badger/project | 226acd885233 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C004 | ai-badger/custom | df5974aff72d | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C005 | ai-badger/custom | 1c5c1ccaf89c | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C006 | ai-badger/custom | ab58f6ec744d | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C007 | ai-badger/custom | 49cb58160fbc | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C008 | ai-badger/custom | 2e73e13779a9 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C009 | ai-badger/project | aa6b11b8cc74 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C010 | ai-badger/project | 0cb4d4ddf5be | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C011 | ai-badger/project | 407ab320871a | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C012 | ai-badger/project | 13f95b60c4e6 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C013 | ai-badger/project | 25771b8fed90 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C014 | ai-badger/project | 0c38eb907426 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C015 | ai-badger/project | 1f421ce04d99 | 1 0.222 | 0 0.000 | NO | none | 1/0 |  |
| C016 | ai-raccoon/project | 85c101758538 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C017 | ai-raccoon/project | 00c8f78b4b1f | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C018 | ai-raccoon/project | a2d3879fad7f | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C019 | ai-raccoon/shared | 0d0a610420e4 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C020 | ai-raccoon/project | 55b9d1ac0fbd | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C021 | ai-raccoon/project | b4b35e38b62a | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C022 | ai-raccoon/project | 1c8e2de0e645 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C023 | ai-raccoon/project | 7919c74edd68 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C024 | ai-raccoon/project | 231996d09049 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C025 | ai-raccoon/project | 1ff737b19a7f | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C026 | ai-raccoon/project | 011efa03c3e3 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C027 | ai-raccoon/project | 3f3a0376d543 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C028 | ai-raccoon/project | 09f29e284e74 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C029 | ai-raccoon/project | 0e0f09f763c9 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C030 | ai-raccoon/project | 12b898aaac85 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C031 | ai-raccoon/project | 029a3d777a2d | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C032 | ai-raccoon/project | 03a70bc0d15d | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C033 | ai-raccoon/project | 09fa011f4ccf | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C034 | ai-raccoon/project | 072dcee62bc1 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C036 | arasz-home-page/project | 044bb60f7ee9 | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C037 | arasz-home-page/project | 206f391939d8 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C038 | arasz-home-page/project | 0497aef36697 | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C039 | arasz-home-page/project | 2c957dc9c2f6 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C040 | arasz-home-page/project | d420480a49c2 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C041 | arasz-home-page/project | abb36b6292f3 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C042 | arasz-home-page/project | 0de8251cc91a | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C043 | deepseek-harness/project | 01179beb095a | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C044 | deepseek-harness/project | 9177222d342e | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C045 | deepseek-harness/project | 1cb3e4aee8be | 1 0.222 | 0 0.000 | NO | none | 1/0 |  |
| C046 | deepseek-harness/project | e665dc1a7f7d | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C047 | deepseek-harness/project | dc69781ba9ae | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C048 | deepseek-harness/project | 2202ecb67f5e | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C049 | deepseek-harness/project | 15d80b3c66b5 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C050 | deepseek-harness/project | 00c952c347ab | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C051 | deepseek-harness/project | 0984f40c469c | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C052 | deepseek-harness/project | 241d3d95b244 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C053 | deepseek-harness/project | 001307d09219 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C054 | deepseek-harness/project | 06f577ffa58f | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C055 | deepseek-harness/project | 336da8e2b65b | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C056 | deepseek-harness/project | 22d662ca66ae | 0 0.000 | 0 0.000 | yes | none | 1/0 |  |
| C057 | deepseek-harness/project | 074544e0dc1f | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C058 | deepseek-harness/project | 023319821d82 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C059 | deepseek-harness/project | 0e87dc0d3116 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C060 | deepseek-harness/project | 13f25c20e279 | 0 0.000 | 0 0.000 | yes | none | 0/0 |  |
| C061 | deepseek-harness/project | 4c35692a0aac | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C062 | dotnet-ignore/project | 5cf7568210ec | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C063 | hermes-default/project | 63345f6d0836 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C064 | hermes-default/project | 80e56abbdb90 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C065 | hermes-default/shared | 05b04fbb5cf9 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C066 | hermes-default/project | f38875e87985 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C067 | hermes-default/custom | 3494d80b6a27 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C068 | hermes-default/project | 7e8d4cbc9c2c | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C069 | hermes-default/custom | 19ad9a374152 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C070 | hermes-default/project | 970554b90178 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C071 | hermes-default/custom | 38a7f4847fde | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C072 | interview-tasks/project | 04a2a225b075 | 1 1.000 | 1 1.000 | yes | none | 1/1 |  |
| C073 | jsaa/custom | 664b9ed26e95 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C074 | jsaa/custom | a6fa1836ae0b | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C075 | jsaa/project | 9251d81a82f9 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C076 | jsaa/project | dfa361827db0 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C077 | jsaa/custom | 1b34c781b807 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C078 | jsaa/project | b5e18b918e36 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C079 | jsaa/project | 78748605e358 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C080 | jsaa/project | 25443ff11389 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C081 | jsaa/shared | 4293525e9323 | 0 0.000 | 1 0.222 | NO | fusion | 1/1 |  |
| C082 | jsaa/project | 12b6addfa66e | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C083 | jsaa/project | 5a36782cefc3 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C084 | jsaa/project | 449eb53976ad | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C085 | jsaa/project | 30b78301f9f5 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C086 | jsaa/project | 35ace7ef6857 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C087 | jsaa/project | 07cc2b39cef3 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C088 | jsaa/project | 47f8e12012a9 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C089 | jsaa/project | 861344ce469b | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C090 | jsaa/project | 3ca5d76d07c2 | 1 0.222 | 1 0.222 | yes | none | 1/0 |  |
| C091 | jsaa/project | 9f69eda07b9d | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C092 | pi-badger-integration/custom | 6143ec318847 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C093 | pi-badger-integration/custom | 48d3c3006a4b | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C094 | pi-badger-integration/project | 3381b30488a6 | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C095 | vue-kanban/project | 09d99638900d | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C096 | vue-kanban/project | 14bdd05072eb | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C097 | vue-kanban/project | 01403e0d71a0 | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C098 | vue-kanban/project | 077a5790544b | 1 0.222 | 1 0.222 | yes | none | 1/1 |  |
| C099 | vue-kanban/project | 8521a5f3877e | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |
| C100 | vue-kanban/project | 000c8046f2db | 0 0.000 | 1 0.222 | NO | fusion | 1/0 |  |

## Aggregates

Queries: n=99, paired (both legs clean)=99.
Harness: hit-rate=0.677 (n=99), mean F1=0.158.
ai-raccoon: hit-rate=0.808 (n=99), mean F1=0.187.
Agreement table: both-hit a=65, harness-only b=2, ai-raccoon-only c=15, neither d=17 (cells sum to 99).
Agreement MCC: 0.5955.
Recompute path: results.json preserves per-query served hashes plus the harness fts_hit/vector_hit legs, so post-hoc relevance metrics recompute from the hash-preserving golden without new retrieval (rerun report.py on results.json). Singleton-F1 here is a parity verdict, not a relevance verdict.

### Repeat-run spread (3 independent runs, fresh bank scratch per repeat; min/max are across runs)

| metric | mean [min–max] |
|---|---|
| harness hit-rate | 0.6768 [0.6768–0.6768] |
| ai-raccoon hit-rate | 0.8081 [0.8081–0.8081] |
| harness mean F1 | 0.1582 [0.1582–0.1582] |
| ai-raccoon mean F1 | 0.1874 [0.1874–0.1874] |
| agreement MCC | 0.5955 [0.5955–0.5955] |

Unstable served-set query ids across repeats: harness=none, ai-raccoon=none. A non-empty list is jitter a golden must not tolerate — the run exits nonzero and never tolerance-blesses a moved anchor.
Variance guarded: weight drift (weights revision cb950dc80d67... / 869254400 bytes pinned), server nondeterminism (fresh scratch copy + fresh server per repeat), and bank drift (row-stability snapshot per repeat, C11). Process-level harness determinism remains the P1 C7 double-run gate; repeats here share one read-only harness store.

### Query-composition stratification (recomputed from the scored corpus rows)

| stratum | n | harness hit-rate | ai-raccoon hit-rate | harness mean F1 | ai-raccoon mean F1 |
|---|---|---|---|---|---|
| debris (query text carries markup/JSON debris) | 23 | 0.609 | 0.696 | 0.169 | 0.188 |
| clean (natural-language query) | 76 | 0.697 | 0.842 | 0.155 | 0.187 |

Disclosure: relevance-flavoured readings ("bank finds what harness misses", any embedding-gap narrative) are **composition-sensitive** — the debris stratum is a tool-call-artifact subset whose stratified rates differ from the clean stratum, so aggregate gaps partly reflect corpus composition. The parity/pipeline reading (identical fusion pipeline on both legs, exact-hash hits) stands. The split is recomputed here from the scored rows' query text by signature (regex adapted from docs/work/mmr_transfer_checks.py), never a hardcoded id list.

## Parity-gap discussion

- Embedding seam (C13/C14, corrected mechanism): both systems use the same SFR-Embedding-Code-400M_R weights with CLS pooling — the harness was verified CLS (llama-index defaults `get_pooling_mode` to 'cls'; the live Pooling module reports pooling_mode='cls'; harness output equals the model-card recipe at cos 1.00000), so this is NOT a pooling difference (that explanation is retracted). The bank runs its own ONNX export while the harness runs the HF safetensors path: bank-ONNX self-consistency 0.9826, ONNX-vs-HF same-text cos 0.5934, store == current HFE path 1.00000; harness vector_hit=1 on only 8/99 rows (78/99 are fts=1/vec=0; 14/15 c-cell rows are fts=1/vec=0). The taxonomy below labels rows against MEASURED leg positions and never claims the harness embedding equals the bank's: a row whose FTS window held the anchor is `fusion` even when the vector leg missed (C10), and only rows where BOTH windows missed are `embedding`/`unrecoverable` (composition-aware, C9).
- Structure gap: 15898 of 56321 rows carry heading_path structure texts (structureAlpha=0.5 fuse; missing structure scores 0). Section-targeted misses on unheaded rows are structure-gap, not fusion-gap.
- No harness knob was tuned to close either gap (plan: measure and report, never tune silently).

### Classified gap taxonomy (the single table; P1 provisional counts consumed, never redefined)

Labels are computed per row from the existing `fts_hit`/`vector_hit` columns, which are CANDIDATE-WINDOW hits (max(limit*3,100)=100), never each leg's top-8: a window-rank 9–100 hit counts as held and therefore labels `fusion`, not `embedding`. Harness-relative and restricted to the c-cell (bank-hit/harness-miss); agreement is never misattributed as deficit.

| label | n | reading |
|---|---|---|
| none | 84 | no harness deficit under test (harness hit, or the bank missed too — agreement is never a deficit) |
| fusion | 15 | a leg's candidate window held the anchor; the fused pipeline did not serve it (dedupe/RRF/floor/Take(8)) |
| embedding | 0 | no leg window held it on clean prose — representation side (the bank-ONNX vs harness-HF runtime seam qualifies this, C13/C14) |
| unrecoverable | 0 | no leg window held it on a debris/tool-call artifact — no representation recovers it |
| unknown | 0 | leg diagnostics absent (data gap) |
| c_cell (bank-hit/harness-miss of 99 paired) | 15 | deficit labels sum; unknown share 0.0% (cap 5%) |

C10 trace note: shared-scope rows (C019, C065, C081) were traced stage-by-stage — the anchor survived per-leg content-dedupe, RRF, affinity and the relative floor, and was dropped by Take(8) after dual-leg candidates outranked it (the harness/bank vector scores diverge at the embedding seam, measured). These rows are fusion-drop; never mapped to "embedding-gap evidence".

Excluded projects (2, seed-equal with the corpus header excludedProjects — documented there, never ad hoc):
- aib → ai-badger (committed=0, shared=1, embedded=1): raw entries.project_id 'aib' folds to canonical 'ai-badger' under the search gate; it has no committed project/custom rows, and its 1 scope='shared' row is global, remains ingested and is served by shared/all queries
- job-search-ai-assistant → jsaa (committed=106, shared=8, embedded=114): raw entries.project_id 'job-search-ai-assistant' folds to canonical 'jsaa' under the search gate, so its 106 committed project/custom rows can never be served; its 8 scope='shared' rows are global, remain ingested and are served by shared/all queries
