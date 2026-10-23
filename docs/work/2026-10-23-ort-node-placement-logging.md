# Research: logging ORT's per-provider node distribution instead of the generic warning

**Date:** 2026-10-23
**Question:** Can ai-raccoon log the exact distribution of graph nodes across execution providers in place of ORT's generic "Some nodes were not assigned to the preferred execution providers" warning?

## Findings

### F1 — ORT computes the per-provider distribution only when the session logger is exactly VERBOSE [READ]

`VerifyEachNodeIsAssignedToAnEp` fills `node_placements` only when `is_verbose_mode`, which is
`logger.GetSeverity() == kVERBOSE`. In that mode it logs `Node placements`, then one
` Node(s) placed on [<EP>]. Number of nodes: N` line per provider followed by one
`  <OpType> (<name>)` line per node. At any other severity it logs only the generic warning plus the
"Rerunning with verbose output" hint — the two lines in the report. Release NuGet builds are
non-minimal, so the verbose branch is compiled in.

**Evidence:** `onnxruntime/core/framework/session_state.cc:1296-1400` at tag v1.30.0 (the version
pinned in `Directory.Packages.props:35`), fetched with `gh api repos/microsoft/onnxruntime/contents/...?ref=v1.30.0`.

### F2 — The C# API can route all ORT log lines into a managed callback [READ]

`EnvironmentCreationOptions.loggingFunction` (`DOrtLoggingFunction`) passed to
`OrtEnv.CreateInstanceWithOptions(ref options)` replaces the default stderr sink; every line arrives
with severity, category, code location and message. It must be set before the first
`OrtEnv.Instance()` call — ai-raccoon currently touches `OrtEnv.Instance()` in the static ctor of
`OnnxEmbeddingGenerator`.

**Evidence:** `csharp/src/Microsoft.ML.OnnxRuntime/OrtEnv.shared.cs:57-201` at v1.30.0;
`src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:116`.

### F3 — With a callback plus session severity VERBOSE, the placement summary is captured and parseable [MEASURED]

On granite-embedding-small-english-r2 with the CoreML EP (MLProgram), the callback received
`CoreMLExecutionProvider=37, CPUExecutionProvider=95`, and the per-node lines grouped to a CPU op
histogram: Reshape×48, Neg×24, MultiHeadAttention×12, Slice×4, Expand×2, Range, Abs, Greater,
Unsqueeze, Gather. At session severity WARNING the same callback got no placement lines, only the
generic warning. Session creation took 3194 ms (WARNING) vs 3126 ms (VERBOSE); verbose added
1036 VERBOSE + ~230 INFO lines to the callback.

**Evidence:** scratch probe `probe.cs` (file-based `dotnet run`, `Microsoft.ML.OnnxRuntime@1.30.0`,
env log level VERBOSE, `AppendExecutionProvider_CoreML(COREML_FLAG_CREATE_MLPROGRAM)`), model
`~/.ai-raccoon/models/onnx-community__granite-embedding-small-english-r2-ONNX/model.onnx`, Apple M4,
.NET SDK 10.0.402, 2026-10-23, one run per mode with a warm CoreML cache.

### F4 — The CoreML count in the placement summary is fused partitions, not original nodes [MEASURED]

The 37 "nodes" on CoreML are the 37 fused `..._CoreML_..._N` partition nodes. The original-node
count comes from CoreML's own WARNING-level line, already emitted today without verbose:
`number of partitions supported by CoreML: 37 number of nodes in the graph: 400 number of nodes
supported by CoreML: 305`. A useful summary therefore combines both: 305/400 original nodes on
CoreML in 37 partitions, 95 on CPU with the op histogram.

**Evidence:** same probe run as F3; `coreml_execution_provider.cc:137 GetCapability` line in its output.

### F5 — ORT 1.30 has a structured API for this, but the managed wrapper does not expose it [READ]

Session config key `session.record_ep_graph_assignment_info` = "1" enables
`Session_GetEpGraphAssignmentInfo` → `OrtEpAssignedSubgraph` (EP name, nodes) →
`OrtEpAssignedNode` (name, domain, op type). The C# `NativeMethods` function table has the slots,
but `InferenceSession` has no public method and its `Handle` is `internal`, so using it means
reflection on the handle plus hand-written delegates over the API table.

**Evidence:** `include/onnxruntime/core/session/onnxruntime_session_options_config_keys.h:608-614`,
`include/onnxruntime/core/session/onnxruntime_c_api.h:7270-7350`,
`csharp/src/Microsoft.ML.OnnxRuntime/NativeMethods.shared.cs:474-476`,
`csharp/src/Microsoft.ML.OnnxRuntime/InferenceSession.shared.cs:28,1655` — all at v1.30.0.

### F6 — Verbose session logging has negligible creation-time cost for this model [INFERRED]

From F3: 3126 vs 3194 ms, a difference inside run-to-run noise for CoreML session creation, and
~1.3k callback invocations. One run per mode, so this rules out a large cost, not a small one.

### F7 — The same approach works for the WebGPU, MLX-plugin and CPU-only sessions [UNVERIFIED]

`VerifyEachNodeIsAssignedToAnEp` is EP-agnostic and the 2026-09-24 research saw the same verbose
section for WebGPU and MLX, but only CoreML was probed through the managed callback today. Plugin
EPs may log through their own sinks.

## Recommendation

Install one ORT logging callback at env creation that forwards ORT lines into `ILogger`
(mapped severities, own `[LoggerMessage]` EventIds), and create each session at
`LogSeverityLevel = VERBOSE` while the callback drops VERBOSE/INFO lines except the
`Node(s) placed on` / `All nodes placed on` block and the per-node lines that follow, which it
aggregates into one summary per session:
`Node placement: CoreML 305/400 nodes in 37 partitions; CPU 95 (Reshape×48, Neg×24, MultiHeadAttention×12, …)`.
The callback then suppresses the two generic lines. Simpler alternative if verbose is unwanted:
keep the CoreML GetCapability line (already logged) and only suppress the generic warning — gives
counts but not the CPU op histogram.

## Still open

- Whether verbose session logging slows WebGPU/MLX session creation (F6 measured CoreML only).
- Whether plugin EPs (MLX, WebGPU node core) log through the env callback at all (F7).
- Callback thread-safety: ORT invokes it from session-creation threads; aggregation state must be per session, keyed by logid.
