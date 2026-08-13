# QA & Benchmark Guidelines for .NET AI Classifiers & Model Pipelines

## 1. Evaluation Target Alignment (Classifier vs. Search Ranking)

When auditing or designing evaluation suites for candidate write/promotion classifiers (`IPromotionClassifier`):
- **Avoid Metric Target Mismatches**: Do NOT evaluate a candidate write classifier using a search query retrieval dataset (such as `search_quality_eval.json`) with search ranking metrics (MRR, nDCG, or Pearson correlation $r$).
- **Use Dedicated Candidate Datasets**: Candidate promotion requires a ground-truth dataset of candidate write payloads labeled with target classification outcomes (e.g. `promote_shared`, `keep_local`, `reject_noise`).
- **Required Classification Metrics**:
  - **Precision, Recall, F1-Score** (per class and macro-averaged).
  - **False Positive Rate (FPR)**: Crucial for promotion classifiers to prevent shared/core store pollution (target FPR $< 1.0\%$).
  - **ROC-AUC & Precision-Recall AUC**.

## 2. C# Async Memory Allocation Measurement Traps

- **Threadpool Allocation Discontinuity**: `GC.GetAllocatedBytesForCurrentThread()` across `await` boundary jumps across threadpool threads returns inaccurate or negative allocation counts (e.g., `-805.76 KB`).
- **Managed Allocation Remedy**: Use `GC.GetTotalAllocatedBytes(precise: true)` on synchronous warm iterations or `BenchmarkDotNet` `[MemoryDiagnoser]`.
- **Unmanaged/C++ Native Heap Memory**: Native ONNX Runtime (`Microsoft.ML.OnnxRuntime`) allocates C++ unmanaged heap memory for model weights and tensor buffers, which is invisible to .NET GC counters.
- **Process Working Set Profiling**: Measure total RAM footprint using `Process.GetCurrentProcess().WorkingSet64` and `PrivateMemorySize64` across three states:
  1. *Baseline* (Host process running, no ONNX session).
  2. *Model Loaded* (`InferenceSession` allocated).
  3. *Peak Batch Inference*.

## 3. ONNX Model Latency & Package Cost Profiling

- **Cold-Start vs. Warm Inference**: Always isolate ONNX model loading / session initialization (cold start, 100ms–1500ms) from warm inference latency (p50, p95, p99 across N $\ge$ 100 iterations).
- **Package Cost vs. Disk Storage**: Separate application binary / NuGet package distribution size from local model storage footprints (e.g., lazy downloads to `~/.airaccoon/models/`).
- **Composite Classifier Bypass Rate**: For multi-stage classifiers (e.g., Fast Zero-Shot pre-screen + Heavy ONNX fallback), measure **Bypass Rate**:
  $$\text{Bypass Rate} = \frac{\text{Entries resolved by Zero-Shot}}{\text{Total Candidate Entries}} \times 100\%$$
