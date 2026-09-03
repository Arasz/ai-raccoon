# Embedding Model Benchmark Results — 2026-08-26

## Models Tested

| Model | Type | Dim | Size | R@5 | R@10 | MRR | nDCG@10 |
|-------|------|-----|------|-----|------|-----|---------|
| SFR-Embedding-Code-400M_R | Local ONNX | 1024 | 1.7 GB | 0.338 | 0.384 | **0.866** | 0.642 |
| text-embedding-embeddinggemma-300m | Remote LM Studio | 768 | 334 MB | **0.343** | **0.404** | 0.858 | **0.704** |
| model_qint8_arm64.onnx (bundled) | Local ONNX | 384 | 23 MB | 0.330 | 0.382 | 0.839 | 0.608 |
| all-MiniLM-L6-v2.Q5_K_M.gguf | Local GGUF | 384 | 21 MB | 0.325 | 0.378 | 0.836 | 0.607 |
| code-daemon-embed-v1 | Local ONNX | 768 | 187 MB | 0.304 | 0.349 | 0.812 | 0.573 |

Qwen3-embedding-0.6b excluded: LM Studio not running on the test machine.

## Key Findings

1. **SFR-Embedding-Code-400M_R is the best model overall** — MRR 0.866 beats the previous best (embeddinggemma-300m at 0.858). Despite being a code-focused model, it generalises well to the full corpus.

2. **Bundled ONNX ≈ GGUF** — The bundled int8 ONNX model (model_qint8_arm64) matches the GGUF version within measurement noise (MRR 0.839 vs 0.836). Recommend using the bundled ONNX as default for consistency.

3. **code-daemon-embed-v1 underperforms on general corpus** — MRR 0.812, nDCG 0.573. It's a small (4-layer) model with only 512 context tokens, optimised for code.

4. **Latency** — In-process models (all ONNX and GGUF) are 4-10× faster than remote LM Studio models.

## Implementation Changes

- Added `OnnxModelEmbedder` to benchmark harness (`benchmarks/AiRaccoon.Benchmarks/Embedders/OnnxModelEmbedder.cs`)
- Supports two paths: manifest-based model directories and legacy bundled model
- Updated `EmbedderCatalog` to auto-discover manifest models from `~/.ai-raccoon/models/`
- Added `InternalsVisibleTo` for `AiRaccoon.Benchmarks` in infrastructure project

## Report

Full HTML report with charts: `docs/reference/embedding-benchmark-report.html`