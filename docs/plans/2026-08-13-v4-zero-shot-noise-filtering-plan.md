# V4 Zero-Shot Semantic Noise Filtering & Performance Benchmark Plan

Date: 2026-08-13
Status: Draft (Pending MoE Review)
Task: v4-zero-shot-noise-filter

## 1. Goal & Objectives
Complete the V4 Zero-Shot Semantic Noise Filtering & Auto-TTL pipeline in `AiRaccoon.Core` and `AiRaccoon.Infrastructure`.
- **Async Pipeline Refactor**: Transition `INoiseFilterPolicy` and `INoiseFilteringService` to async (`ValueTask<NoiseFilterResult>`).
- **Zero-Shot Semantic Noise Filter**: Complete `ZeroShotEmbeddingNoisePolicy` to fetch embeddings and run cosine distance checks against a seeded Noise Vector Corpus.
- **Noise Corpus & Trash Table**: Store rejected noise entries in a dedicated 'trash' table with a 14-day TTL for auditability and training data without polluting the primary memory bank.
- **Performance Benchmarks**: Benchmark write latency, ops/sec, and allocations (`baseline -> change -> effect`) using BenchmarkDotNet / xUnit speed benchmarks.
- **TDD Discipline**: Every work package must follow RED -> GREEN -> REFACTOR discipline.

## 2. Architecture & Design

### A. Async Noise Filter Interface Refactor
```csharp
public interface INoiseFilterPolicy
{
    string Name { get; }
    ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}

public interface INoiseFilteringService
{
    ValueTask<bool> EvaluatePreWriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}
```

### B. ZeroShotEmbeddingNoisePolicy Implementation
1. Inject `IEntryEmbedder` and `INoiseVectorProvider` / `INoiseStore`.
2. Generate embedding vector for `request.Content` via `IEntryEmbedder`.
3. Perform vector comparison using `ZeroShotEmbeddingFilter.IsNoise(docVector, noiseVector, threshold)`.
4. If classified as noise, return `NoiseFilterResult.Noise("ZeroShotSemanticNoiseFilter")`.

### C. Seeded Noise Vector Corpus & Trash Table
1. Define a seed asset `noise_vectors_v1.json` containing 50 canonical noise embeddings (background process completion logs, CLI completion notices, build outputs, low usefulness grade samples).
2. Store rejected noise items in `INoiseStore` / `trash_entries` table with `ttl_days = 14`.

### D. Benchmark & Measurement Suite
1. Measure baseline `SqliteMemoryStore.WriteAsync` throughput (ops/sec), latency (p50/p99), and memory allocation.
2. Measure post-implementation performance with `ZeroShotEmbeddingNoisePolicy` active.
3. Quantify return on investment: Noise rejection efficiency (% of noise blocked) vs write latency overhead (< 2ms).

## 3. Work Packages (Parallel Worktree Streams)

- **Work Package 1 (WP1)**: Async Noise Filter Pipeline Refactor
  - Refactor `INoiseFilterPolicy`, `INoiseFilteringService`, `HermesProcessNoisePolicy`, `NoiseFilteringService`, and `SqliteMemoryStore.WriteAsync`.
  - Add tests in `NoiseFilteringServiceTests`.

- **Work Package 2 (WP2)**: Zero-Shot Noise Policy, Noise Corpus & Trash Storage
  - Implement `ZeroShotEmbeddingNoisePolicy` and `INoiseVectorProvider`.
  - Seed canonical noise vectors in `noise_vectors_v1.json`.
  - Wire trash table persistence (`INoiseStore` / 14-day TTL).
  - Add unit and integration tests in `ZeroShotEmbeddingNoisePolicyTests`.

- **Work Package 3 (WP3)**: Write Performance Benchmarks & Quality Evaluation
  - Benchmark write performance (`baseline -> change -> effect`).
  - Run search quality benchmark against `search_quality_eval.json` to verify zero precision loss for valid memories.

## 4. Verification & Acceptance Criteria
- [ ] All unit, integration, and BDD tests pass (`dotnet test`).
- [ ] Write performance benchmark results documented (`baseline -> change -> effect`).
- [ ] Rejection rate > 90% for background process noise logs without false positives on valid architectural knowledge.
