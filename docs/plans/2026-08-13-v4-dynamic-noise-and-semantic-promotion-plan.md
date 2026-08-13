# V4 Dynamic Noise Vector Learning & Semantic Promotion Classifier Plan

Date: 2026-08-13
Status: Draft (Pending MoE Review - Revised per User Feedback)
Task: v4-dynamic-noise-and-semantic-promotion

## 1. Executive Summary & Goals

This plan implements V4 Phase 2 (Auto-Improving Dynamic Noise Vector Learning) and V4 Phase 3 (Semantic Promotion Classifier with Comparative Measurement & Opt-In ONNX) for AiRaccoon (.NET 10 MCP Memory Server over sqlite-memory):

1. **Auto-Improving Dynamic Noise Vector Learning (Phase 2)**:
   - Schema migration v5 -> v6 (`noise_clusters` table + `vec_noise` partition-keyed virtual vector table).
   - Online Leader-Follower Centroid Clustering service ($\tau_{cluster} = 0.12$, similarity $> 0.88$).
   - 4-Channel Feedback Collection Loop (`search_quality` low grades, `promotion_discards`, unread expired scratch entries, structural intercepts).
   - 4 MCP Tools: `memory_noise_report`, `memory_noise_list`, `memory_noise_override`, `memory_noise_settings`.
   - 5 Mandatory Safety Boundaries: Dual-gated candidate promotion ($n \ge 3$), scope white-list immunity (`shared`, watch docs, ADRs), core knowledge orthogonality check ($\cos(\mu_{noise}, \mu_{core}) \le 0.75$), 14d reversible trash bin, threshold floor $\ge 0.85$ + circuit breaker, operator overrides with negative reinforcement list.

2. **Dual-Classifier Semantic Promotion Engine & Comparative Evaluation (Phase 3)**:
   - **Approach A (Zero-Shot Vector Distance)**: Computes cosine distance against core domain knowledge centroids ($\mu_{core}$). Zero extra disk/package cost, sub-millisecond execution.
   - **Approach B (Local ONNX Instruct Model)**: Quantized local ONNX model (`Qwen2.5-0.5B-Instruct`) for background evaluation. Opt-in via configuration (`promotion.model.enabled = true|false`, default: `false`).
   - **Approach C (Combined Pipeline)**: Fast zero-shot vector distance pre-screen, followed by ONNX instruct evaluation for borderline candidates.
   - **Comparative Evaluation Suite**: Benchmark and compare Approach A vs B vs C on:
     * **Performance**: Latency (ms), CPU/Memory allocations.
     * **Cost**: Package size, disk space, RAM footprint.
     * **Quality**: Correlation against `search_quality_eval.json` ($r$ correlation, MRR, nDCG).

---

## 2. Architecture & Design

### A. Schema Migration v5 -> v6
```sql
CREATE TABLE IF NOT EXISTS noise_clusters (
    id                 INTEGER PRIMARY KEY,
    project_id         TEXT NOT NULL,
    user_id            TEXT NULL,
    cluster_label      TEXT NOT NULL,
    sample_content     TEXT NOT NULL,
    frequency          INTEGER NOT NULL DEFAULT 1,
    status             TEXT NOT NULL CHECK(status IN ('candidate','active','suppressed')),
    centroid_embedding BLOB NOT NULL,
    created_at         INTEGER NOT NULL,
    last_seen_at       INTEGER NOT NULL,
    UNIQUE(project_id, cluster_label)
);

CREATE VIRTUAL TABLE IF NOT EXISTS vec_noise USING vec0(
    ctx TEXT partition key,
    embedding float[384] distance_metric=cosine
);
```

### B. Online Leader-Follower Centroid Clustering (`OnlineNoiseClusteringService`)
- For candidate noise $e(x)$, compute distance to existing centroids in scope `ctx IN ('project:' || P, 'user:' || U, 'shared')`.
- If distance $< 0.12$, update running centroid $\mu^*$.
- If distance $\ge 0.12$, spawn new candidate cluster $C_{new}$.
- Check Safety Bound 2 (Orthogonality): Promote to `active` only if $n_k \ge 3$ AND $\max_{core} \cos(\mu_k, \mu_{core}) \le 0.75$.

### C. 4 MCP Tools for Noise Management
- `mcp__ai_raccoon__memory_noise_report` — report text/entry as noise.
- `mcp__ai_raccoon__memory_noise_list` — list active/candidate noise centroids.
- `mcp__ai_raccoon__memory_noise_override` — restore falsely intercepted entries or suppress clusters.
- `mcp__ai_raccoon__memory_noise_settings` — configure learning thresholds and toggles.

### D. Dual-Classifier Promotion Architecture & Opt-In Config
```csharp
public interface IPromotionClassifier
{
    ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request, 
        CancellationToken cancellationToken = default);
}

public sealed class ZeroShotVectorPromotionClassifier : IPromotionClassifier { ... }
public sealed class OnnxInstructPromotionClassifier : IPromotionClassifier { ... }
public sealed class CompositePromotionClassifier : IPromotionClassifier { ... }
```
- Config setting: `promotion.model.enabled = true|false` (default `false`).

---

## 3. Work Packages (Parallel Implementation Streams)

- **Work Package 1 (WP1)**: Schema Migration v5->v6 & Dynamic Noise Cluster Store
  - Add v5->v6 migration step in `MemorySchema.cs`.
  - Implement `SqliteNoiseClusterStore` for `noise_clusters` and `vec_noise`.

- **Work Package 2 (WP2)**: Online Leader-Follower Clustering & 4-Channel Feedback Collector
  - Implement `OnlineNoiseClusteringService` and `NoiseFeedbackCollector`.
  - Implement the 5 Safety Boundaries (Orthogonality check, dual-gated state, white-list immunity, circuit breaker).

- **Work Package 3 (WP3)**: 4 MCP Tools for Noise Management
  - Implement `memory_noise_report`, `memory_noise_list`, `memory_noise_override`, and `memory_noise_settings` tool endpoints.
  - Wire tools in `AppRegistrations.cs` and `McpServerSetupHost`.

- **Work Package 4 (WP4)**: Dual-Classifier Implementation (Zero-Shot vs ONNX) & Opt-In Configuration
  - Implement `ZeroShotVectorPromotionClassifier` (Approach A).
  - Implement `OnnxInstructPromotionClassifier` (Approach B) behind `promotion.model.enabled`.
  - Implement `CompositePromotionClassifier` (Approach C).

- **Work Package 5 (WP5)**: Comparative Measurement & Benchmark Report
  - Measure Approach A vs B vs C on latency, memory allocations, package/model disk cost, and quality correlation ($r$) on `search_quality_eval.json`.
  - Document findings in `docs/work/2026-08-13-v4-promotion-classifier-benchmark-report.md`.

---

## 4. Verification & Acceptance Criteria
- [ ] Schema v5->v6 migration tests pass (`EnsureAsync_FromV5_CreatesNoiseClustersAndVecNoise`).
- [ ] TDD unit tests for Leader-Follower clustering and safety boundaries pass (`OnlineNoiseClusteringServiceTests`).
- [ ] 4 MCP noise tools tested and verified (`McpNoiseToolsTests`).
- [ ] Both Approach A (Zero-Shot Vector) and Approach B (ONNX Instruct) implemented; ONNX model is opt-in via config (`promotion.model.enabled = false` by default).
- [ ] Comparative benchmark report produced (`docs/work/2026-08-13-v4-promotion-classifier-benchmark-report.md`) evaluating latency, memory/disk cost, and correlation $r$.
- [ ] Full build and test suite pass cleanly (`dotnet test`).
