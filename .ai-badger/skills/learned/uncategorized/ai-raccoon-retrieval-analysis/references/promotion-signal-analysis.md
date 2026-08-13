# Promotion-quality signal analysis — embeddings do not encode usefulness

Measured 2026-08-13 against the 55-row promotion-labeled fixture
(`docs/work/promotion-scoring-eval/reference-labels.json` joined to live-bank content by hash via
`rebuild_fixture.py` → `/tmp/promotion-fixture.json`). Usefulness label 0–4 from `RUBRIC.md`
(4 = durable portable gotcha, 0 = noise for a shared tier). Record of record:
`docs/work/2026-08-13-fixing-zero-shot-promotion-classifier.md`.

## The verdict

**Promotion usefulness is not a direction the content embedding captures.** Any embedding-based
promotion gate is a dead end — do not tune its threshold, re-represent it with more prototypes, or
"calibrate" it further. Promotion quality is the mechanical `PromotionScorer`'s job (archetype
prior + zero-centred content evidence + organic refinement), which measures Spearman +0.69 on a
79-row holdout (ADR-0018 round-3 lane-A). Embeddings stay for retrieval and for noise *rejection*
of stereotyped text (turn-mirrors, transcripts, status dumps), never portability judgement.

## Measured numbers (leave-one-out, 55 rows, 16 signal / 39 noise)

| method | F1 | Spearman |
|---|---|---|
| single-reference zero-shot (shipped) | 0.46 | — |
| supervised positive centroid | 0.48 | +0.04 |
| multi-prototype (max-pos − max-neg) | 0.59 | +0.36 |
| k-NN k=1 / k=3 / k=5 | 0.52 / 0.47 / 0.56 | +0.26 / +0.18 / +0.30 |
| mechanical `PromotionScorer` (holdout) | — | +0.69 |

Cluster-structure diagnostic: mean cosine signal–signal 0.393, noise–noise 0.382, signal–noise
0.382 — a promotable fact is as close to noise as to another promotable fact. Fully-supervised
methods are an UPPER BOUND on any zero-shot variant, and the best of them (0.59 F1 / +0.36
Spearman) is far below usable and would still overfit a committed labeled set that can't live in a
public repo.

## The "is label X embeddable?" ceiling probe (run this BEFORE any representation work)

Decide whether an embedding encodes a label axis in ~30 lines of stdlib Python, no numpy, no
re-embedding — pull the vectors already stored in the live bank:

1. Get real embeddings from `~/.ai-raccoon/memory.db`: `entries.embedding` is a `float32[384]`
   BLOB (1536 bytes). Join to a labeled fixture by hash, exactly as `rebuild_fixture.py` does:
   ```sql
   SELECT embedding FROM entries WHERE hash = ? ORDER BY id LIMIT 1
   ```
   (a hash repeats across scopes; lowest id is the original project-scope row).
2. Unpack: `struct.unpack("<384f", blob)`. Cosine in pure `math` (no numpy on this machine).
3. Print the cluster diagnostic first — `mean cos(pos,pos)` vs `mean cos(neg,neg)` vs
   `mean cos(pos,neg)`. If the three are within ~0.01 of each other, the label has no cluster
   structure and NO embedding classifier will work; stop there.
4. Compute the supervised ceiling: leave-one-out nearest-positive-minus-nearest-negative (and/or
   positive centroid, k-NN). Report best F1 (sweep threshold) + Spearman.
5. Verdict rule: if the supervised ceiling is below the incumbent signal's correlation by a wide
   margin (here +0.36 vs +0.69), the label axis is not embeddable — change representation, do not
   tune.

`struct`/`sqlite3`/`math` are stdlib; the whole probe ran in 0.4 s in `execute_code`.

## Why the "OnnxInstruct" name lies

`OnnxInstructPromotionClassifier.ClassifyCandidateAsync` (model-enabled branch) embeds the candidate
and a canonical reference string through the bundled *embedding* model and cosine-compares them at
threshold 0.07 — it never invokes an instruct/LLM. The only ONNX runtime in the repo is the
embedding generator (`OnnxEmbeddingGenerator.cs`, `Microsoft.ML.OnnxRuntime`). The "dual-classifier
semantic promotion engine" is two cosine similarities against two different reference sentences.
The zero-shot pre-screen is pass-through by default (`SharedExtractionRunner.FilterByClassifierAsync`
short-circuits when `!IsModelEnabled`), but the live bank has `promotion.model.enabled = true` in
`settings`, so it runs ahead of the scorer on every propose pass at threshold 0.07 — passing ~everything
and adding nothing.
