# 0030. Real-time Heuristic TTL Assignment

Date: 2026-08-13

## Status
Accepted

## Context
Our existing background sweep service (ADR-0025) relies on entries having a `ttl_days` value to determine when to degrade them. By default, `memory_write` operations assign a `NULL` TTL, making the entries permanent unless explicitly aged by an agent.
We explored using an LLM to score content at ingestion and assign short TTLs to transient or low-value data. However, running a generative LLM synchronously during the `memory_write` tool call would cause the agent to hang for several seconds on every write, degrading the user experience.

## Decision
We will implement real-time heuristic TTL assignment using an `IAutoTtlPolicy` pipeline injected into `SqliteMemoryStore`.

1. **Reusing the PromotionScorer**: We will leverage the existing fast, heuristic text analysis engine (`PromotionScorer`), normally used for shared tier promotion, to evaluate incoming writes synchronously. 
2. **Thresholding**: The `PromotionScorerTtlPolicy` will feed the incoming content into the scorer. If the heuristic score is below `0.6` (indicating the content is structurally thin, lacks portability, or matches known transient archetypes like turn mirrors), the policy will assign a short TTL of 3 days.
3. **Background Reaping**: The assigned TTL allows the existing `SweepService` to eventually hard-delete the entry if it proves un-useful, without requiring synchronous LLM evaluation.

## Consequences
- **Positive:** We automate the degradation of low-value, transient context without blocking the agent's write operation.
- **Positive:** We reuse heavily tested heuristic scoring logic, maintaining a lean architecture without adding synchronous ML dependencies.
- **Negative:** Heuristics are not semantically perfect; some edge-case valuable short notes might receive a 3-day TTL and be prematurely degraded if not accessed or explicitly saved by the agent.
