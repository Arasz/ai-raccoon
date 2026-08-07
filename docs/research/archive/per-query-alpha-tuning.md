# Per-Query α Tuning for Dual-Vector Structure-Aware Retrieval

**Date**: 2026-08-04
**Problem**: In dual-vector retrieval (`score = α × sim(query, content) + (1-α) × sim(query, structure)`), α must vary per query — "what was the decision?" wants high structure weight, "tell me about performance" wants low. How to determine α without user input?

## Obvious Ideas Refused

| Idea | Why Rejected |
|---|---|
| User specifies α | They won't. This is a retrieval system, not a search UI. |
| Train a classifier on labeled query-section pairs | Needs training data you don't have, fragile across collections. |
| Fixed α per collection | The whole point is per-query variance. |

## Approaches

### 1. Top-K Agreement (Self-Calibrating)

Run retrieval twice: α=1 (content-only) and α=0 (structure-only). Compare top-K overlap.

`α = 1 - |content_top_k ∩ structure_top_k| / k`

High overlap → structure doesn't matter (α ≈ 0.2). Low overlap → structure signal is important (α ≈ 0.9).

**Failure mode**: 2× retrieval latency (parallelizable). Low overlap doesn't tell you *which side is right*.

---

### 2. Embedding Geometry: Structure-Space Dispersion

Compute query's similarity to top-M structure embeddings. Peaked distribution = clear structural target → high α. Flat distribution = no structural alignment → low α.

`α = (max_sim - mean_sim) / std_sim`

**Failure mode**: Degenerate structure embedding space — if all heading paths embed similarly, distribution is always flat.

---

### 3. Query Token Overlap with Heading Vocabulary

Maintain per-section-type token vocabularies. `{"decision", "decided", "chose"}` → Decision, etc. Count overlap.

`α = clamp(max_overlap, 0.1, 0.9)`

**Failure mode**: Vocabulary coverage — "why did we go with Postgres" has no decision tokens but is a decision query. Low recall.

---

### 4. Two-Pass Confidence Voting (GROUNDED)

Pass 1: compute structure similarities (cheap — heading paths are ~10-50 tokens). Pass 2: fuse with dynamically computed α.

```
structure_scores = sim(query, all_structure_embeddings)
confidence = max(structure_scores) - mean(structure_scores)  // "peakiness"
α = sigmoid(confidence * temperature)  // map to (0,1)

content_scores = sim(query, all_content_embeddings)
final_scores = α * content_scores + (1-α) * structure_scores
return top_k(final_scores)
```

One hyperparameter: temperature. Controls how aggressively confidence maps to α.
- Low temperature (0.1): α jumps to extremes — almost always 0 or 1
- Medium temperature (0.5): smooth transition
- High temperature (0.8): α stays near 0.5 unless confidence is very high
- Temperature 1.0: nearly linear mapping

**Failure mode**: Temperature is collection-specific. Spurious peaks from word overlap between query and heading paths. The sigmoid mapping is a heuristic with no theoretical guarantee.

**Effort**: ~afternoon to prototype
**Stack**: Dual-vector index + temperature parameter + confidence computation

## Open Question

Does linear fusion (continuous α) actually outperform hard gating (structure match as binary pre-filter)? The α-tuning complexity may be solving a self-created problem — if you can reliably detect whether a query has structural intent, you might be better off routing to structure-first retrieval than blending signals.
