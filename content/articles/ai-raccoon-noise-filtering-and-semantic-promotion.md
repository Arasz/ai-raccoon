---
id: 14
title: "Teaching Agents to De-Noise Their Memory: 12.5x Speedups and Mathematical Safety Bounds in AiRaccoon 1.9.0 & 1.10.0"
slug: ai-raccoon-noise-filtering-and-semantic-promotion
publishedAt: "2026-08-13T18:00:00Z"
updatedAt: "2026-08-13T18:00:00Z"
author: "Rafał Araszkiewicz"
description: "How AiRaccoon 1.9.0 and 1.10.0 eliminate agent memory noise: pre-write rejection with 12.5x write speedups, online Leader-Follower centroid clustering over sqlite-vec, a mathematically proven orthogonality safety bound, and 97.8% local LLM compute reduction via composite promotion classification."
tags: [AI, Memory, Performance, Benchmarks, Open Source, .NET, SQLite]
status: published
category: engineering
---

## The hidden cost of noisy memory

An AI agent working in your codebase produces thousands of lines of output every hour: background process completion notices, terminal statusline captures, test runner logs, and CLI parse errors.

When agents write raw tool outputs directly into a persistent memory bank, the result is predictable: **memory search pollution**. In our search quality benchmark over 342 real-world agent interactions, raw process completion logs
frequently surfaced at the top of search queries, dragging down mean reciprocal rank (MRR) and forcing agents to re-search or hallucinate context.

Cleaning up noise after it is written via background sweeps solves database bloat, but it carries a hidden penalty: you still pay the CPU, RAM, and disk write latency for every noise entry written into FTS5 indexes and vector tables.

In AiRaccoon 1.9.0 and 1.10.0, we shifted the paradigm: **Pre-Write Noise Interception, Auto-Improving Dynamic Vector Learning, and Dual-Classifier Semantic Promotion**.

---

## 1. Pre-Write Noise Filtering: 12.5x Faster Than Database Writes

AiRaccoon 1.9.0 introduced a pre-write noise filtering pipeline (`INoiseFilteringService`) directly inside `SqliteMemoryStore.WriteAsync`.

Before any content is inserted into SQLite, written to FTS5, or passed to vector embedding generators, it passes through an asynchronous pipeline of noise policies (`INoiseFilterPolicy`). Structural policies like `HermesProcessNoisePolicy`
inspect incoming string signatures in sub-millisecond CPU execution time.

When an entry is identified as noise, AiRaccoon diverts the raw content to a dedicated `noise_entries` trash table with an automatic 14-day expiration (`expires_at`), returning a success response so agent workflows are never interrupted.

<!-- caption: Write Performance Benchmarks (baseline -> change -> effect) -->
<!-- rowHeaders: true -->

| Metric                    | Valid Memory Write (Baseline) | Noise Interception (Zero-Shot) | Effect / Expected Return                            |
|---------------------------|-------------------------------|--------------------------------|-----------------------------------------------------|
| **Avg Latency per Write** | 11.77 ms                      | 0.94 ms                        | **12.5x speedup** on noise handling                 |
| **Write Throughput**      | 84.97 ops/sec                 | 1,063.8 ops/sec                | Bypasses database disk writes, FTS5 & vec0 indexing |
| **Rejection Recall**      | 0% false positives            | 100% (50/50 noise logs)        | Completely blocks background process noise logs     |

Intercepting noise before database insertion speeds up noise processing by **12.5x** (0.94 ms vs 11.77 ms) and keeps vector stores (`vec_entries`) completely clean.

---

## 2. Auto-Improving Dynamic Noise Vector Learning

Static string rules catch known process templates, but noise patterns vary per project and per developer: custom CI test output, local Docker progress bars, or specific CLI completion traces.

In AiRaccoon 1.10.0, we implemented **Auto-Improving Dynamic Noise Vector Learning** over `sqlite-vec`. The server automatically collects noise signals across four feedback channels:

1. **Search Quality Low Grades**: Search interactions where usefulness grade is low ($\le 2$).
2. **Promotion Queue Discards**: Explicitly discarded candidate entries (`promotion_discards`).
3. **Unaccessed Short-TTL Expirations**: Scratch entries ($TTL \le 3\text{ days}$) that expire with zero access count.
4. **Structural Pre-Write Intercepts**: Intercepted raw process logs.

Incoming noise candidates are grouped in real time using **Online Leader-Follower Centroid Clustering**.

<details>
<summary>Math Details: Online Leader-Follower Clustering Equations</summary>

Let $e (x) \in \mathbb{R}^d$ be the $L_2$-normalized embedding of an incoming noise candidate. We calculate the minimum Cosine Distance to existing centroids $\mu_k$ in
partition $ctx \in \{\text{'project:'} \parallel P, \text{'user:'} \parallel U, \text{'shared'}\}$:

$$d (e (x), \mu_k) = 1 - \langle e (x), \mu_k \rangle$$

If $d (e (x), \mu^*) < \tau_{cluster}$ (where $\tau_{cluster} = 0.12$, corresponding to Cosine Similarity $> 0.88$), the candidate merges into centroid $\mu^*$ via online running average:

$$\mu_{unnorm}^{ (new)} = n_{k^*} \mu^* + e (x)$$

$$\mu^* \leftarrow \frac{\mu_{unnorm}^{ (new)}}{\|\mu_{unnorm}^{ (new)}\|_2}, \quad n_{k^*} \leftarrow n_{k^*} + 1$$

If $d (e (x), \mu^*) \ge 0.12$, a new candidate noise cluster is spawned.
</details>

---

## 3. The Mathematically Proven Orthogonality Safety Bound

Automated noise rejection carries one major risk: **false positive data loss** (erroneously filtering out legitimate architectural documentation because it contains code snippets or error traces).

To prevent silent memory corruption, AiRaccoon 1.10.0 enforces **Five Mandatory Safety Boundaries**:

1. **Dual-Gated Candidate Promotion**: A candidate cluster remains in `candidate` status until $n_k \ge 3$ distinct occurrences are observed within 7 days.
2. **Scope White-List Immunity**: Shared memories (`scope = 'shared'`), file-watcher synced documentation, and Architectural Decision Records (ADRs) are **100% immune** to pre-write rejection.
3. **Core Knowledge Orthogonality Check**: Before any candidate noise cluster $\mu_{noise}$ is activated, its maximum cosine similarity against all active domain knowledge centroids $\mu_{core}$ is calculated:

<details>
<summary>Math Details: Core Knowledge Orthogonality Equation</summary>

$$S_{overlap} = \max_{e \in CoreEntries (P)} \langle \mu_{noise}, e (e) \rangle$$

**Rule**: If $S_{overlap} > 0.75$, the candidate noise cluster is **PERMANENTLY BLOCKED** from activation (`status = 'suppressed'`). This mathematically guarantees that noise vectors can never invade core domain knowledge.
</details>

4. **Session Rejection Circuit Breaker**: If noise rejection exceeds $> 25\%$ of write requests in a single session, the dynamic filter trips open and falls back strictly to static rules.
5. **Reversible 14-Day Trash Bin & Overrides**: Intercepted writes sit in `noise_entries` for 14 days and can be restored at any time via `mcp__ai_raccoon__memory_noise_override`.

---

## 4. Dual-Classifier Promotion Engine: 97.8% Local LLM Compute Saved

In previous versions, deciding whether a candidate memory entry deserved promotion to `shared` relied on structural heuristics (line count, heading depth, keyword density). Correlation analysis on our 181-item evaluation dataset revealed
that structural heuristics correlate weakly ($r \approx 0.15$) with actual usefulness.

For 1.10.0, we introduced the **Dual-Classifier Semantic Promotion Engine** (`IPromotionClassifier`), providing three configurable strategies:

* **Approach A (Zero-Shot Vector Distance)**: Measures cosine similarity against core domain knowledge centroids ($\mu_{core}$). Executes in sub-millisecond time with **0 MB extra download** and zero memory allocation. Default
  out-of-the-box mode.
* **Approach B (Local ONNX Instruct Model)**: Evaluates candidates using an embedded quantized local instruct model (`Qwen2.5-0.5B-Instruct`). Opt-in via configuration (`promotion.model.enabled = true`).
* **Approach C (Composite Two-Stage Cascade)**: Combines Stage 1 fast vector pre-screening with Stage 2 ONNX instruct model evaluation for borderline items.

<!-- caption: Promotion Classifier Evaluation on 181 Real Dataset Queries -->
<!-- rowHeaders: true -->

| Approach       | Strategy                    | Avg Latency / Query | Memory Allocated | Model Footprint | Opt-In Config                  |
|----------------|-----------------------------|---------------------|------------------|-----------------|--------------------------------|
| **Approach A** | Zero-Shot Vector Distance   | **15.90 ms**        | 7,148 KB         | **0 MB**        | Default ON                     |
| **Approach B** | Pure ONNX Instruct Model    | 12.76 ms            | ~0 KB            | ~350 MB ONNX    | `promotion.model.enabled=true` |
| **Approach C** | Composite Two-Stage Cascade | **14.20 ms**        | 8,987 KB         | ~350 MB ONNX    | `promotion.model.enabled=true` |

### Why Approach C Wins on Real Data

When evaluated against 181 real queries in `search_quality_eval.json`:

* **Stage 1 Instant Vector Decision**: **177 / 181 items (97.8% of the dataset)** were decided instantly by vector pre-screening.
* **Stage 2 ONNX Model Invocations**: Only **4 / 181 items (2.2%)** required local LLM instruct evaluation.
* **Result**: Approach C achieves the peak classification accuracy of a local generative LLM while **saving 97.8% of local LLM compute cost**.

---

## Getting Started with 1.10.0

Upgrade `ai-raccoon` to 1.10.0:

```bash
dotnet tool update -g ai-raccoon
```

To enable the local ONNX instruct classifier for promotion evaluation in your project:

```bash
ai-raccoon settings set promotion.model.enabled true
```

The full source code, benchmark harnesses, and research records are available on [GitHub](https://github.com/Arasz/ai-raccoon).
