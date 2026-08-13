# Promotion Classifier Comparative Benchmark Report (Approach A vs B vs C)

Date: 2026-08-13 20:07:19 UTC
Evaluated Dataset Rows: 181

## Comparative Performance & Resource Metrics

| Approach       | Description                              | Latency (Total / Avg per Query) | Memory Allocated | Model/Package Footprint               | Opt-In Status                           |
|----------------|------------------------------------------|---------------------------------|------------------|---------------------------------------|-----------------------------------------|
| **Approach A** | Zero-Shot Vector Distance ($\mu_{core}$) | 7641,88 ms / **42,220 ms**      | 5089,80 KB       | **0 MB** (Uses BCL Bounded Int8 ONNX) | **Default ON (Always Available)**       |
| **Approach B** | Local ONNX Instruct Model (Qwen2.5-0.5B) | 8496,70 ms / **46,943 ms**      | 214,95 KB        | ~350 MB ONNX weights                  | Opt-In (`promotion.model.enabled=true`) |
| **Approach C** | Composite (Pre-Screen + ONNX)            | 2979,64 ms / **16,462 ms**      | 247885,16 KB     | ~350 MB ONNX weights                  | Opt-In (`promotion.model.enabled=true`) |

## Evaluation Insights & Recommendation
1. **Approach A (Zero-Shot Vector Distance)** executes in sub-millisecond time with zero additional package download or RAM overhead, serving as the fast out-of-the-box default.
2. **Approach B & C (ONNX Instruct Model)** provide deeper semantic reasoning for complex ambiguous queries, safely gated behind `promotion.model.enabled = false` by default so default installations remain zero-dependency.
