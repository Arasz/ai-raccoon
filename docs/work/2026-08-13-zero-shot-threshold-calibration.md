# Zero-shot promotion classifier threshold calibration

Date: 2026-08-13

## Question

Can the zero-shot promotion classifier (cosine similarity of a candidate embedding against a
canonical core-domain reference) be calibrated to separate promotion-worthy memory from noise?

## Method

Rebuilt the promotion-labeled fixture (`docs/work/promotion-scoring-eval/reference-labels.json`,
0–4 usefulness labels, joined to live-bank content by hash via `rebuild_fixture.py` → 55 rows),
embedded every value with the bundled model, and measured max cosine similarity against two
canonical references:

- core: "Architecture decision record documenting a durable technical decision …"
- reusable-fact: "A cross-project reusable fact worth sharing …"

Partitioned by usefulness and swept the threshold 0.0–0.5 for the best F1 (signal = usefulness ≥ 2).

## Result

| usefulness | n | median similarity | p25 | p75 | max |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 (noise) | 21 | 0.190 | 0.150 | 0.262 | 0.378 |
| 1 (local) | 18 | 0.165 | 0.132 | 0.195 | 0.362 |
| 2 (weakly portable) | 10 | 0.193 | 0.159 | 0.223 | 0.387 |
| 3 (core semantics) | 3 | 0.269 | 0.176 | 0.295 | 0.322 |
| 4 (durable gotcha) | 3 | 0.171 | 0.134 | 0.176 | 0.182 |

- signal (≥2): median **0.187** (n=16)
- noise (≤1): median **0.175** (n=39)
- best threshold t=0.07, **F1 = 0.46** (near chance for this 29% base rate)

## Conclusion

The zero-shot reference approach **does not separate promotion-worthy content from noise**. The
distributions overlap almost completely — the most valuable class (u=4 durable gotchas) scores
*below* noise. No threshold makes the classifier a useful gate; at any threshold it randomly
rejects roughly half of good content.

Promotion quality is carried by the mechanical `PromotionScorer` (provenance archetype + content
evidence), which is already calibrated against `reference-labels.json` (`eval.py`, Spearman/nDCG).
The zero-shot classifier is wired but should be treated as non-discriminating: its threshold is set
to the measured best (0.07, least harmful) and must not be relied on as a quality gate. Full
calibration of a *semantic* promotion signal needs a different representation than a single
reference centroid (e.g. a labeled embedding classifier), which is out of scope here.
