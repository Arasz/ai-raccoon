# Research & Algorithmic Roadmap (V4 Scoring & Local Filtering)

Date: 2026-08-13

## 1. Evaluating Promotion Scoring (V3)
We have successfully extracted a rich dataset of 164 graded memory searches from the `search_quality` table, enriched with 10 simulated RAG-expert grades and 93 local LLM (Prometheus) grades. 

**Next Steps for Evaluation:**
- Export this graded dataset to a JSON fixture.
- Point `AIRACCOON_SCORING_EVAL_FIXTURE` to this new file.
- Run `PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness` to mathematically measure how well our current `PromotionScorer` heuristics correlate with the actual usefulness grades. This establishes our V3 baseline.

## 2. Algorithmic Improvements for Scoring (V4)
The correlation analysis showed that structural heuristics (length, specific keywords) are only weakly correlated with actual content value (r ~ 0.15). For V4, we must move from structural analysis to semantic analysis.

**Proposed V4 Architecture (Asynchronous Mini-LLM):**
Given the project already utilizes `Microsoft.ML.OnnxRuntime`, we can embed a highly quantized (int4), ultra-small instruct model (e.g., `Qwen2.5-0.5B-Instruct` ONNX) directly within the AiRaccoon `.NET` process.
- We implement an `IHostedService` that runs a background loop.
- The loop sweeps the `entries` table for items with `ttl_days = 3` (assigned by our V3 heuristic policy).
- It feeds the content to the local ONNX model with a prompt: "Is this text a transient system log or valuable architectural knowledge?"
- If the model classifies it as valuable, the service strips the TTL, making it permanent.

## 3. Advanced Local Noise Filtering (Zero-Shot Embedding)
If the V4 background Mini-LLM proves too resource-intensive, we can implement an alternative local ML filter for pre-write rejection.
- We maintain a static SQLite table of "Noise Signatures" (pre-computed embeddings of 50 classic noise logs).
- During `SqliteMemoryStore.WriteAsync`, after calculating the embedding for the incoming request, we execute a fast Cosine Distance check against the noise signatures.
- If the similarity is > 0.90, the entry is rejected as noise. This operates at the speed of the embedding model and requires no separate generative LLM.
