# Code-engine inference research — the quantized and CoreML arms, before they are measured

**Date:** 2026-08-23 · **Branch:** `docs/pd4-wp7-inference-research` · **Base:** `origin/main` `05625928`
· **Lane:** architect · **WP:** post-delta-4 **WP7** (WP12-E), **desk half** ·
**Scope:** research only — no production file is edited in this branch, and no engine is swapped.

Every figure below carries the command that produced it and a tag:

- **[measured]** — I ran it on this machine, in this session, and the command is quoted.
- **[read]** — read out of the tree or a package at the stated version, with `file:line`.
- **[inferred]** — derived from a measured number plus a read fact; the derivation is shown.
- **[unverified]** — stated so it can be checked later; I did not settle it.

**The headline.** Both arms are real, and neither is the shape the plan assumed.

- **The CoreML arm loads and runs today** — the pinned 1.29.0 package takes 177 of 197 nodes on the
  CoreML EP under MLProgram. But Apple's runtime rejects the graph's *dynamic* input shapes at every
  partition boundary, so the arm is **not** "a `SessionOptions` call": it needs a fixed-or-bucketed
  padding policy, which is a change to `OnnxEmbeddingGenerator.RunBatch`. **And the parity number
  this record originally leaned on does not cover the arm being proposed** — it was measured on a
  run that fell back to CPU. Vector drift for CoreML is **unmeasured**; §6.3's Gate 2 now covers
  every arm, not just int8.
- **The int8 arm does not exist as specified.** `quantize_dynamic` run on this model the way the
  card describes it quantizes **nothing that costs time** — 0 of 29 weight MatMuls — because every
  weight sits behind a `Transpose`. A three-step recipe does produce a genuine 47 MB / 25
  `MatMulInteger` artifact, and that artifact's vectors sit at **cos ≈ 0.964** against fp32 on a
  smoke corpus. That is not a drop-in.
- **Five dated work documents state the opposite of the graph.** They call the shipped code model
  "INT8 QAT". It is fp32 — all 70 initializers are `FLOAT`. The **published** docs are clean, so the
  blast radius is bounded to `docs/work/`. Corrected in place, dated, in §2 below.

Nothing here is a throughput number. Throughput is wave 3's job, on a quiet machine, and §6 names
its protocol and its pass/fail lines **before** it runs.

---

## 1. What is pinned, and what the package actually contains

| Fact | Value | How |
|---|---|---|
| Package | `Microsoft.ML.OnnxRuntime`, no `.Gpu`/`.DirectML`/EP package | `src/AiRaccoon.Infrastructure/AiRaccoon.Infrastructure.csproj:19` **[read]** |
| Version | **1.29.0** | `Directory.Packages.props:34` **[read]** |
| osx-arm64 native | `runtimes/osx-arm64/native/libonnxruntime.dylib`, 43,184,400 B | `ls -l` **[measured]** |
| Session construction | `new SessionOptions { IntraOpNumThreads = … }` → `new InferenceSession(modelPath, sessionOptions)` | `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:60-61` **[read]** |
| Per-batch padding | `maxLen = Math.Min(_window, items.Max(i => i.Ids.Length))` — every batch gets its **own** sequence length | `OnnxEmbeddingGenerator.cs:130` **[read]** |

### 1.1 Is the CoreML EP in the pinned package? Yes, on osx-arm64 only

```sh
nm -gU ~/.nuget/packages/microsoft.ml.onnxruntime/1.29.0/runtimes/osx-arm64/native/libonnxruntime.dylib \
  | grep -i AppendExecutionProvider
```

```
0000000000584054 T _OrtSessionOptionsAppendExecutionProvider_CPU
0000000000130ae0 T _OrtSessionOptionsAppendExecutionProvider_CoreML
```

**[measured]** — this confirms §Review (c) of the plan.

The managed side was checked against metadata rather than `strings`, because the .NET string heap
suffix-folds `AppendExecutionProvider_CoreML` into the tail of the P/Invoke name
`OrtSessionOptionsAppendExecutionProvider_CoreML` and a `strings` grep cannot tell a real method from
that shared suffix. Reading `System.Reflection.Metadata` over
`microsoft.ml.onnxruntime.managed/1.29.0/lib/net8.0/Microsoft.ML.OnnxRuntime.dll` (the asset a
`net10.0` project resolves) **[measured]**:

- `Microsoft.ML.OnnxRuntime.SessionOptions.AppendExecutionProvider_CoreML` — **`Public, HideBySig`**.
- `Microsoft.ML.OnnxRuntime.CoreMLFlags` — `COREML_FLAG_USE_NONE`, `_USE_CPU_ONLY`,
  `_ENABLE_ON_SUBGRAPH`, `_ONLY_ENABLE_DEVICE_WITH_ANE`, `_ONLY_ALLOW_STATIC_INPUT_SHAPES`,
  `_CREATE_MLPROGRAM`, `_USE_CPU_AND_GPU`, `_LAST`.

So the managed API exposes it at this version. **No dependency change, no pin update, no new package
is needed for the CoreML arm.**

### 1.2 It is genuinely absent elsewhere — the guard is mandatory, not defensive

```sh
nm -gDU …/runtimes/linux-x64/native/libonnxruntime.so | grep AppendExecutionProvider
# 000000000060a940 T OrtSessionOptionsAppendExecutionProvider_CPU@@VERS_1.29.0
strings …/runtimes/win-x64/native/onnxruntime.dll | grep -c AppendExecutionProvider_CoreML
# 0
```

**[measured].** Linux-x64 exports **only** `_CPU`; win-x64 does not carry the string at all. The
package ships `android`, `ios`, `linux-arm64`, `linux-x64`, `osx-arm64`, `win-arm64`, `win-x64`
**[measured]**. Calling `AppendExecutionProvider_CoreML` off Apple is therefore not a silent no-op —
it is an `EntryPointNotFoundException` at session construction, i.e. **an embedding engine that
fails to start**. Any production form of this arm must be gated on `OperatingSystem.IsMacOS()`
before the call, not after it.

---

## 2. Correction: the shipped code model is fp32, not INT8 QAT

The plan's §Review (c) says the code engine is "the fp32 outlier". **Five** dated work documents say
it is INT8 — the list derived by `grep -rn 'INT8 QAT\|int8 QAT' docs/` plus a broader `INT8` sweep of
the same five files, not assembled by hand:

| File | Lines | What it says |
|---|---|---|
| `docs/work/2026-08-21-code-search-exploration.md` | `:14`, `:48`, `:49`, `:81` | *"187 MB INT8 ONNX"*; *"INT8 from quantization-aware training (Q/DQ nodes carry trained scales); **do not run PTQ/calibration over it** — card measured hit@1 .200 → .133"*; *"`model_int8qdt.onnx` 187,490,530 B"*; *"yes (INT8 QAT 187 MB)"* |
| `docs/work/2026-08-21-code-search-implementation-plan.md` | `:34`, `:349` | *"INT8 QAT ONNX 187 MB"*; *"never PTQ the INT8 QAT artifact"* — carried as a **live constraint** in the condensed risk list |
| `docs/work/2026-08-21-code-search-moe-ops.md` | `:234` | the same PTQ prohibition, in the §4 risk table |
| `docs/work/2026-08-21-code-search-moe-architecture.md` | `:21` | *"768-dim INT8 QAT ONNX 187 MB"*, quoting the exploration record |
| `docs/work/2026-08-21-code-search-moe-engineer.md` | `:32` | *"768-dim INT8 QAT ONNX 187 MB"*, in a row graded **"Code model (verified)"** |

**The published docs are clean, and that bounds the blast radius.** `docs/reference/agent-memory-server.md:159,874,881`,
`docs/how-to/configure-embedding-engines.md:14,31`, `docs/explanation/agent-memory-architecture.md:42`
and `README.md:140` mention int8 **only** in connection with the bundled *MiniLM memory* model
(`model_qint8_arm64.onnx`, ~23 MB), which is correct. No user-facing document has ever described the
code model as quantized. **[measured]** — `grep -rn 'int8\|INT8' docs/reference/ docs/explanation/ docs/how-to/ README.md`.

**The plan is right and all five work documents are wrong about the artifact we run.** Loading the file
`model download faxenoff/code-daemon-embed-v1` actually places on disk
(`<scratch>/codemodel/model.onnx`, 187,286,767 B, sha256
`57bcfc6aed11ea239d01f2b124f2f948456f2284ad6e2c4744452509c9c25ca9` — the value pinned in that
directory's `ai-raccoon.manifest.json`) **[measured]**:

```
ir_version: 9 · producer: pytorch 2.12.1 · opset: '' 19
inputs:  input_ids ['batch','seq'], attention_mask ['batch','seq']
outputs: last_hidden_state ['batch', <dynamic>]
node count: 373
  Constant 99 · Add 43 · Transpose 41 · Unsqueeze 35 · MatMul 33 · Mul 19 · Cast 18 ·
  Concat 16 · Reshape 16 · Gather 11 · Shape 10 · LayerNormalization 9 · Div 6 · Erf 4 ·
  Softmax 4 · Clip 2 · ReduceSum 2 · ConstantOfShape 1 · CumSum 1 · Expand 1 · ReduceL2 1 · Sub 1
initializers: 70
  FLOAT: tensors=70 elements=46,801,920 raw_bytes=187,207,680
```

**Zero** `QuantizeLinear`, `DequantizeLinear`, `MatMulInteger` or `QGemm` nodes; **zero** int8 or
uint8 tensors; 46.8M parameters × 4 bytes = the whole 187 MB. It is a plain fp32 graph.

Two consequences:

1. **The card's PTQ warning does not forbid this arm.** *"Never PTQ the INT8 QAT artifact"* is about
   a different file (`model_int8qdt.onnx`, per `:49`) than the one AiRaccoon downloads and runs. It
   remains a **warning about what quantization does to this model family's retrieval** — hit@1
   .200 → .133 is a 33 % relative loss — and §4.3 measures something consistent with it.
2. **All five documents are corrected in place, dated**, per *fix-what-you-find* — not left for a
   follow-up and not rewritten silently. The repo already has the pattern for exactly this, in
   exactly this file: `docs/work/2026-08-21-code-search-exploration.md:44` reads
   `~~hard 128-token cap~~ **512** … **CORRECTED 2026-08-22**`, backed by an `## Amendments` section
   at `:434` and mirrored by an amendment in `docs/adr/0088-…md:88-97`. *"Dated records are not
   edited"* is **not** this project's convention, so this record follows the one that is: each
   drifted line gets a struck-through in-place marker reading **CORRECTED 2026-08-23**, and each of
   the five files gets one dated `## Amendments` entry (three of them needed the section created).
   The two documents carrying *"never PTQ the INT8 QAT artifact"* as a **live ops constraint**
   (`implementation-plan.md:349`, `moe-ops.md:234`) are the ones that most needed it: that
   prohibition was gating a decision on a file we do not run. **[unverified]** whether the HF repo
   still hosts a separate `model_int8qdt.onnx` and whether `ModelDownloadPlanner` could ever select
   it — worth one check before wave 3, because if the repo already ships a QAT int8 artifact then
   the int8 arm is *"download the other file"* and §4's recipe is unnecessary.

Also read from the manifest **[read]**: `dimensions` 768, `contextWindowTokens` 512,
`normalization` l2, `pooling.mode` `model-output` with `embedding` = `last_hidden_state`,
`requiresTokenTypeIds` false, tokenizer family sentencepiece. The graph carries `ReduceL2` + `Div`,
so **pooling and L2 normalization happen inside the ONNX graph** and `last_hidden_state` comes back
rank-2 `[batch, 768]` — consistent with #466's "pool by the output's rank" fix.

---

## 3. The CoreML arm, measured at the desk

A file-based .NET 10 probe (`dotnet run coreml.cs`) with `#:package Microsoft.ML.OnnxRuntime@1.29.0`
builds two sessions over the same `model.onnx` — one plain CPU, one with
`so.AppendExecutionProvider_CoreML(<flag>)`, both `IntraOpNumThreads = 5` — feeds identical
synthetic `input_ids`/`attention_mask`, and compares the 768-float outputs. Session logging at
`ORT_LOGGING_LEVEL_VERBOSE` yields ORT's own partition report. All **[measured]**.

### 3.1 Backend choice decides the arm

| Backend | Nodes ORT offered | Supported by CoreML | **Partitions** | Fused CoreML nodes in final graph | Cosine vs CPU | max abs delta |
|---|---|---|---|---|---|---|
| `COREML_FLAG_USE_NONE` (NeuralNetwork, the default) | 197 | 104 | **32** | 32 | 1.0000000 | **1.9e-5** |
| `COREML_FLAG_CREATE_MLPROGRAM` | 197 | **177** | **12** | 12 | 1.0000000 | **2.1e-7** |

Both source lines, verbatim **[measured]**:

```
CoreMLExecutionProvider::GetCapability, number of partitions supported by CoreML: 32
  number of nodes in the graph: 197 number of nodes supported by CoreML: 104
CoreMLExecutionProvider::GetCapability, number of partitions supported by CoreML: 12
  number of nodes in the graph: 197 number of nodes supported by CoreML: 177
```

**Reading the last two columns** (they look contradictory and are not). "Supported by CoreML" counts
nodes *before* partitioning; "Fused CoreML nodes in final graph" counts the fused subgraph nodes ORT
reports afterwards — `Node(s) placed on [CoreMLExecutionProvider]. Number of nodes: 12`, each one a
compiled partition standing in for many original nodes. 177 original nodes become 12 fused nodes
across 12 partitions; the remaining 20 sit on `[CPUExecutionProvider]`. So one partition boundary per
fused node, which is the cost that matters.

**On "197" versus §2's 373** — the same file, two counters. §2 inventories `model.onnx` **as it sits
on disk**: 373 nodes. ORT applies its own default graph optimizations *before* offering the graph to
an EP for partitioning, so `GetCapability` sees an already-folded 197-node graph. It is the same
optimizer §4.2's recipe leans on. **"177/197" is therefore not "177 of the model's 373 nodes."**

The default NeuralNetwork backend **rejects every op that matters**: the verbose log rejects
`MatMul` at every layer (`Operator [MatMul] is not supported by the impl`), plus `Reshape`, `Erf`
and `Cast`. It splits the graph into 32 interleaved subgraphs and leaves all the arithmetic on CPU.
**If the measured arm is run with default flags it will measure nothing and look like a
regression.** `COREML_FLAG_CREATE_MLPROGRAM` is the only backend worth measuring.

**The two parity figures above are CPU-fallback measurements, and they do not cover the arm this
record proposes.** §3.2 shows that on these same runs Apple's E5RT rejected every dynamic shape and
CoreML fell back; so the arithmetic that produced `2.1e-7` largely did **not** execute on the CoreML
backend. Worse, the figure and the arm are mutually exclusive by this record's own reasoning: A3
exists to bind shapes so the graph *can* schedule on the ANE, the ANE computes in fp16, and a graph
that genuinely lands there will not agree with fp32 CPU to 2.1e-7. **What these numbers do prove is
narrower and still worth having: ORT's CoreML EP wires up correctly, partitions this graph, and
returns right answers.** They prove nothing about drift under a bound-shape CoreML arm — that is
unmeasured, and §6.3's Gate 2 now covers A3 for exactly this reason.

### 3.2 The blocker is dynamic shapes, and it is Apple's, not ORT's

Under MLProgram the results are correct, but Apple's E5RT runtime emits, repeatedly and per layer:

```
E5RT encountered an STL exception. msg = Input: attention_mask has unbounded dimension which is not
supported. Please consult MIL Framework or milPython on adding a bound for this dimension.
E5RT … Failed to PropagateInputTensorShapes: std::invalid_argument during type inference for
ios18.matmul: shapes of x and y are not broadcastable.
```

Named tensors in the wall: `attention_mask`, `_encoder_encoder_layer_{0,1}_attention_self_Reshape_1_output_0`,
`_encoder_Mul_output_0`, `_ReduceL2_output_0`, `_Expand_output_0`.

The graph declares `['batch','seq']` — fully dynamic — and `OnnxEmbeddingGenerator.cs:130` makes
that concrete *per batch*: `maxLen` is the longest row **in that batch**, so the shape changes from
one `Run` to the next. Correctness survives (the parity numbers above were taken through this same
path), so CoreML is silently falling back rather than failing; but a CoreML program that cannot bind
its shapes cannot be scheduled on the ANE, which is the entire premise of the arm.

**This is the finding that changes the WP's cost.** §Review (c) of the plan says *"the arm costs a
`SessionOptions` call, not a dependency change"*. The dependency claim is right; the
`SessionOptions`-call claim is not. A CoreML arm that can win needs, in addition:

- a **fixed or bucketed** padding length at `OnnxEmbeddingGenerator.cs:130` (pad to 512, or to a
  small ladder such as {128, 256, 512}, instead of the batch's own max), and
- `COREML_FLAG_ONLY_ALLOW_STATIC_INPUT_SHAPES` to make the constraint explicit rather than
  discovered, and
- an `OperatingSystem.IsMacOS()` guard (§1.2).

Fixed padding is not free: it is the exact opposite of WP12-A's length-sorted-batch fix
(`task/pd3-wp12a-length-sorted-batches`), which exists to *reduce* padding. **These two changes
fight each other**, and that conflict should be on the record before either ships. Bucketing is the
shape that could satisfy both. **[inferred]** from `OnnxEmbeddingGenerator.cs:130` **[read]** plus
the E5RT diagnostics **[measured]**.

### 3.3 What the desk cannot answer

Whether MLProgram + static shapes is *faster*. Nothing here is a throughput measurement; the probe
ran two sequence lengths once each. Session construction under CoreML did take visibly longer than
CPU (≈2 s vs ≈1 s from adjacent log timestamps) because CoreML compiles the program at session
create — **[unverified]** as a number, and §6 measures it, because the drain restarts on every
`serve` restart and a slow compile is a real cost.

---

## 4. The int8 arm, measured at the desk

Tooling used, all in a scratch venv: `onnx` 1.22.0, `onnxruntime` **1.29.0** (python) — the same
version as the pinned .NET package — and `sympy`. **[measured]**

### 4.1 The obvious recipe quantizes nothing that costs time

`quantize_dynamic(model.onnx, out.onnx, weight_type=QuantType.QInt8)` — 1.6 s, output 133,741,771 B.
Inspecting it **[measured]**:

```
MatMul: 33 · DequantizeLinear: 3 · MatMulInteger: 0
initializers: FLOAT 70 tensors / 28,942,851 elements · UINT8 6 tensors / 17,859,075 elements
```

It quantized the **embedding lookup table** (22,739 × 768 = 17.5M elements) and left all 33 MatMuls
in fp32. The embedding table is a `Gather`; it costs no arithmetic. Expected speedup: none.

Running ORT's own recommended preprocessing first does not help. `quant_pre_process(...,
skip_symbolic_shape=False)` **crashes** on this graph —
`TypeError: object of type 'NoneType' has no len()` at `symbolic_shape_infer.py:374 _broadcast_shapes`,
reached from `_infer_Expand` **[measured]** — and with `skip_symbolic_shape=True` it completes but
produces a byte-identical-sized result (133,741,816 B) with the same 0 `MatMulInteger`.

**Why**, measured rather than guessed — classifying each MatMul's two inputs by producer:

| MatMul A | MatMul B | count |
|---|---|---|
| LayerNormalization | **Transpose** | 8 |
| Cast | **Transpose** | 8 |
| Mul | Mul | 4 |
| Softmax | **Transpose** | 4 |
| Reshape | **Transpose** | 4 |
| Mul | **Transpose** | 4 |
| Div | **Transpose** | 1 |

ORT's dynamic quantizer only quantizes a `MatMul` whose **B input is an initializer**. This
PyTorch 2.12 / opset-19 export puts a `Transpose` in front of every weight (41 `Transpose` nodes in
a 4-layer model), so the matcher sees zero eligible MatMuls. The 4 `Mul`×`Mul` pairs are the
attention score/context products — genuinely activation×activation, and correctly never quantizable.

### 4.2 A three-step recipe that does work

Let ORT's own graph optimizer fold the transposes first, then quantize:

```python
# 1. fold Transpose(weight) into an initializer — ORT_ENABLE_BASIC is enough
so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_BASIC
so.optimized_model_filepath = "basic.onnx"
ort.InferenceSession("model.onnx", so, providers=["CPUExecutionProvider"])

# 2. quantize the folded graph
quantize_dynamic("basic.onnx", "model.int8.onnx", weight_type=QuantType.QInt8)
```

After step 1, 25 of the 29 weight MatMuls have `B = initializer` **[measured]**. After step 2
(3.7 s) **[measured]**:

| | fp32 | int8 |
|---|---|---|
| Bytes | 187,286,767 | **47,034,766** (**4.0×** smaller) |
| sha256 | `57bcfc6a…c25ca9` | `e00cf954…e7d660` |
| `MatMulInteger` | 0 | **25** |
| `DynamicQuantizeLinear` | 0 | **17** |
| `MatMul` (left fp32) | 33 | 8 |
| INT8 initializers | 0 | 50 tensors / 28,901,401 elements |
| UINT8 initializers | 0 | 6 tensors / 17,859,075 elements (the embedding tables, from §4.1) |
| FLOAT initializers | 70 tensors / 46,801,920 elements | 77 tensors / 41,507 elements (scales, zero-points, LayerNorm) |

The three rows close against the file size, which is why they are all listed: 28,901,401 + 17,859,075
= **46,760,476** bytes of quantized tensors, plus 41,507 floats (166,028 B) = 46,926,504 B, leaving
**108,262 B** of graph structure — node definitions, names, shapes — against the artifact's actual
47,034,766 B. **[measured]** A table carrying only the INT8 row leaves 17.9M elements unaccounted
for, which at fp32 would be 71.6 MB inside a 47 MB file, and reads as impossible.

That is the same **u8s8 arithmetic** the bundled memory model uses: `DynamicQuantizeLinear` emits
uint8 activations against int8 weights, so every quantized matmul is u8s8, exactly as
`model_qint8_arm64.onnx` does (*"48 `MatMulInteger`"*, ADR-0049 `:55` **[read]**). It is **not** the
same coverage: the memory model is 48/48 with **zero** fp32 matmuls left — fully quantized — while
this artifact is 25 of 33 with **8** still fp32, about 76 %. The u8s8 claim, and with it §5b's
inheritance of ADR-0049's cross-ISA risk, rests on the arithmetic and holds; "the same shape" would
have overstated it.

**`ORT_ENABLE_ALL` was tried and rejected.** It also folds the transposes, but it introduces
`com.microsoft`-domain fusions (`BiasGelu`), after which `quantize_dynamic` fails outright —
`RuntimeError: Unable to find data type for weight_name='…/attention/output/dense/MatMul_output_0'.
shape_inference failed to return a type probably this node is from a different domain` **[measured]**
— and ORT warns that an `ORT_ENABLE_ALL` artifact *"may contain hardware specific optimizations, and
should only be used in the same environment the model was optimized in"* **[measured]**, which is
disqualifying for a file we would ship or pin.

### 4.3 The vectors move — a lot

fp32 vs int8, same inputs, CPU EP both sides, 24 samples per length, seeded RNG **[measured]**:

| seq | n | cosine min | mean | max |
|---|---|---|---|---|
| 64 | 24 | 0.957656 | **0.964204** | 0.969362 |
| 128 | 24 | 0.959442 | **0.964744** | 0.970409 |
| 256 | 24 | 0.965698 | **0.968929** | 0.971077 |
| 510 | 24 | 0.968037 | **0.970816** | 0.972871 |

**Negative control**, same harness, fp32 vs the §4.1 embedding-table-only model (which has no
`MatMulInteger` and should be nearly identical) **[measured]**:

| seq | cosine min | mean | max |
|---|---|---|---|
| 64 | 0.999856 | **0.999884** | 0.999901 |
| 510 | 0.999948 | **0.999957** | 0.999963 |

The control lands at 0.9999 and the real arm at 0.964, so the four-nines gap is the quantization,
not the harness — the measurement can distinguish a no-op from a change, which is the only reason
the 0.964 is worth quoting.

**Caveat, stated so nobody over-reads it.** These inputs are uniformly-random token ids, which are
out of distribution for the model, and quantization error is typically worse off-distribution. The
real number must come from the **1,762 actual chunks** (§6). But 0.964 is a poor prior, it points
the same way as the model card's PTQ warning (hit@1 .200 → .133, §2), and it is why §6's accuracy
gate is written to be failable rather than as a formality.

---

## 5. What each arm costs, beyond the measurement

| | **int8** | **CoreML (MLProgram)** |
|---|---|---|
| Tooling | python `onnx` 1.22 + `onnxruntime` 1.29.0, two-step recipe §4.2 | none |
| New model file | yes — 47 MB derived artifact | no |
| Manifest change | `onnx.files[0].sha256` re-pinned | none |
| NuGet pin change | none | none |
| Production code | none *if* the artifact is just activated | `OnnxEmbeddingGenerator` ctor **and** `:130` padding policy **and** an OS guard |
| Fingerprint / re-embed | **yes** — a manifest content change is the engine fingerprint (ADR-0084), so `model set code local` invalidates all 1,762 code rows in one transaction (ADR-0087). ~1,061 s at today's rate **[read]** | **unknown** — no manifest changes, so nothing triggers a re-embed *automatically*; whether one is *needed* depends on Gate 2 |
| Stored-vector compatibility | **breaks** — banks embedded fp32 must re-embed | **unmeasured** — the 2.1e-7 was taken on a CPU-fallback run (§3.1); a bound-shape CoreML arm may compute in fp16 on the ANE. Gate 2 settles it |
| Non-Apple platforms | unchanged | must be a guarded no-op, or the engine throws (§1.2) |

Three consequences worth their own lines:

**(a) The int8 artifact has no provenance story.** `ModelDownloadPlanner` downloads from a HF repo
and pins per-file SHA-256; a file *we* produced by running `quantize_dynamic` locally has no
upstream to pin against, and the MoE ops doc's TOFU-pin design
(`docs/work/2026-08-21-code-search-moe-ops.md:234` **[read]**) has no slot for it. Adopting int8
means either hosting a derived artifact somewhere, or the recipe becomes a build step, or the arm is
only ever a local `model set code local <dir>`. **This is a distribution decision, not a performance
one**, and it is arguably a larger objection than the throughput result.

**(b) int8 extends ADR-0049's accepted defect to a second corpus.** ADR-0049 records that u8s8
matmul takes three different arithmetic paths (arm64 NEON / x64 `VPMADDUBSW` / VNNI `VPDPBUSD`) and
that the resulting embeddings differ in the third decimal — a spread of 0.070 nDCG@5, fourteen times
the gate tolerance. The owner **accepted** that for the memory model on 2026-08-14, on the reasoning
that *"a bank embedded and queried on one machine is self-consistent"*. Quantizing the code model
makes that true of the code corpus too. That is bounded by ADR-0085's "the code corpus is an
explicit, re-derivable cache" — losing it costs a re-ingest — but it is a real extension of an
accepted risk, and it should be decided deliberately rather than inherited.

**(c) CoreML collides with WP12-A.** §3.2. Length-sorted batches minimize padding; static shapes
maximize it. Bucketing may satisfy both; nobody has measured that.

**(d) CoreML's "no accuracy cost" is a hypothesis, not a finding.** It is the one claim in this
record that was over-stated in the first draft, and it was load-bearing in four places — Gate 1's
lower bar, the §5 row above, the ADR's Context, and Gate 2's scope. All four are now conditional.
Both plans always scoped WP7 as three arms *"plus a vector-drift check of the same 1,762 chunks
against fp32"* (`docs/work/2026-08-23-post-delta-4-plan.md:496` and
`docs/work/2026-08-23-post-delta-5-plan.md:135` **[read]**) — not int8-only. Narrowing it was an
unflagged departure from an approved scope; it is withdrawn.

---

## 6. The measurement plan for wave 3 — protocol and thresholds, named before the run

### 6.1 Protocol — S3–S5 verbatim, nothing new

From `docs/work/2026-08-22-code-ingestion-profile.md` §3 and §9 **[read]**, unchanged:

- Scratch bank only, **port 7931**, `--data-root <scratch>`, `--idle-timeout 0`. Never 7721, never
  `~/.ai-raccoon`.
- Corpus: the same **469-file / 2,045,873 B** C# tree (`git ls-files src | grep '\.cs$'`), producing
  **1,762** `code_entries` rows.
- Thread cap **5** for every arm (`settings model threads 5`) — the merged default, and the fastest
  of the three caps measured.
- For each arm: set the cap, **kill and restart `serve`** (sessions cache per engine fingerprint, so
  a restart is mandatory), **re-activate the code engine** (which invalidates all 1,762 rows to
  `pending` in one transaction), then count `embed_state='pending'` at the start and end of a fixed
  **150-second** window.
- CPU alongside, identically: `top -l 6 -s 5 -stats cpu,th`.
- **Alone on the machine.** No other lane running, no other `serve`, no build. This is the condition
  whose absence made #511's numbers unusable, and it is the reason WP7's measured half is wave 3.

Baselines to compare against, all from that document **[read]**: **S4 = 2.347 rows/s** (cap 5),
**S5 = 1.902 rows/s** (cap 0), **S2 = 1,061.3 s** end-to-end, S4 CPU 124.3–140.3 % at ~41 threads.

### 6.2 Arms

| id | Arm | Build |
|---|---|---|
| **A0** | fp32 CPU, cap 5 — **re-baseline** | today's binary, unchanged |
| **A1** | int8, cap 5 | the §4.2 artifact (47 MB, 25 `MatMulInteger`), activated via `model set code local` |
| **A2** | CoreML MLProgram, fp32, cap 5 | scratch branch: `AppendExecutionProvider_CoreML(COREML_FLAG_CREATE_MLPROGRAM)` at `OnnxEmbeddingGenerator.cs:60` |
| **A3** | CoreML MLProgram + **static shapes**, fp32, cap 5 | A2 plus fixed `maxLen = 512` at `:130` and `COREML_FLAG_ONLY_ALLOW_STATIC_INPUT_SHAPES` |
| **A4** | **fp32 CPU + fixed `maxLen = 512`**, cap 5 — *padding control* | A3's `:130` change **without** the CoreML EP |

A3 is not optional garnish: §3.2 says A2 is expected to fall back, so **A2 alone cannot falsify the
CoreML arm** — only A3 can. A2 exists to size the fallback penalty.

**A4 is what makes A3 readable, and it is the cheapest arm in the set.** A0 pads each batch to that
batch's longest row; A3 pads every batch to 512. That is not a free change: the profile document
sizes the same axis in the opposite direction — length-sorted batching (*less* padding) is worth
"a conservative 10–20 % of the 1,061 s drain (105–210 s) … attention is superlinear in sequence
length" (`docs/work/2026-08-22-code-ingestion-profile.md:251-253` **[read]** — and tagged there
*"[inferred, and the derivation is explicitly weaker than the others]"*, so treat it as an order of
magnitude, not a number), against a mean chunk of 1,159 chars and 18.5 % of rows under 500. So A3 must clear its bar **while doing materially more arithmetic than A0**, and without A4
a miss cannot be attributed: CoreML losing and the padding eating the win look identical. A4 also
makes the bucketing question of §3.2/§8 decidable now instead of deferred — it is the same one-line
`:130` change A3 needs anyway.

**Gate 1 for A3 is therefore read against A4, not A0** — same padding on both sides, so the
comparison isolates the execution provider. A3-vs-A0 is still reported, as the end-to-end number a
user would actually feel.

A0 through A4 are **scratch-branch** builds — A2/A3/A4 each need a one- or two-line source change that
is never intended to merge. Per G3, nothing merges from this measurement; the record ends in a
recommendation.

### 6.3 Thresholds — every one of these can go red

**Gate 0 — protocol validity (run first; if it fails, stop and report).**
A0 must land within **±15 %** of S4's 2.347 rows/s, i.e. **1.995–2.699 rows/s**. Outside that band
the machine or the tree has moved since 2026-08-22 and no cross-arm comparison from this session is
comparable to the published baselines. This is the check that catches the #511 failure mode
*during* the run instead of after it.

**Gate 1 — throughput, per arm.** Measured against **A0**, not against S4.

- **A1 (int8)** must reach **≥ 1.5× A0** to be recommendable. Rationale: it costs a full re-embed,
  a re-pinned manifest, an unsolved provenance story (§5a) and an extension of ADR-0049 (§5b).
  1.5× removes ≈ 354 s from the 1,061 s drain; below that the price is not paid for.
- **A2/A3 (CoreML)** must reach **≥ 1.25×**, A3 measured against **A4** (§6.2). **This bar is
  provisional.** It was set on the premise that the arm costs no accuracy, and §3.1 now says that
  premise is unmeasured — the 2.1e-7 came from a CPU-fallback run. **If A3 fails Gate 2, its bar
  rises to A1's 1.5×**, because an arm that moves vectors is buying its speed with the same currency
  int8 is, and must pay the same price. The bar is confirmed at 1.25× only once A3 clears Gate 2.
- **A4 (padding control)** carries no bar. It is a measurement, not a candidate.
- Below its bar, an arm is **rejected in the record**, not re-run with a lower bar.

**Gate 2 — vector drift. Every arm that could move a vector: A1 *and* A3** (and A2 if it is run
with any shape binding at all). Re-embed all **1,762** real chunks under A0 and under the arm, and
compare, per chunk:

- primary: **mean top-5 overlap ≥ 0.80** across **≥ 30** code queries run against both banks —
  i.e. the arm returns at least 4 of A0's top 5, on average. Retrieval agreement is what the corpus
  is for; cosine is a proxy for it.
- secondary: **mean per-chunk cosine ≥ 0.99** and **1st-percentile cosine ≥ 0.97**.

**Failing Gate 2 rejects an arm regardless of Gate 1** — a faster engine that returns different
results is not a faster engine. For A3 the consequence is graded rather than fatal: failing it moves
A3 to the 1.5× bar and puts a full code re-embed into its cost column (§5), which is exactly the
price int8 pays.

**For A1 this gate is predicted to fail.** §4.3 measures mean 0.964 / min 0.958 on a smoke corpus,
well under both secondary numbers. Naming the threshold and the prediction together is the point: if
A1 comes back at 0.995 on real chunks, that is a genuine surprise worth acting on, and if it comes
back at 0.96 the gate has done its job.

**For A3 there is no prediction, and that is the finding.** §3.1's 2.1e-7 does not apply to a
bound-shape run, and the ANE's fp16 arithmetic could plausibly land anywhere between 0.999 and
int8's 0.964. This gate exists because nobody knows.

**Gate 3 — CoreML is actually executing off-CPU (A2/A3).** The primary criterion is **binary and
on-mechanism**, not a CPU proxy, because §3 already harvested the direct signal:

- **primary:** A3's `ORT_LOGGING_LEVEL_VERBOSE` session log contains **zero** E5RT shape-rejection
  messages — none of `attention_mask`, `_encoder_encoder_layer_*_attention_self_Reshape_1_output_0`,
  `_encoder_Mul_output_0`, `_ReduceL2_output_0`, `_Expand_output_0` may appear with *"has unbounded
  dimension which is not supported"*. One occurrence means the shapes did not bind and A3 is just A2.
- **secondary:** partition and placement counts from the same log, which must still report a CoreML
  placement (§3.1's shape), and mean/max CPU % from the identical `top` invocation — A3's band
  separating from A0's 124.3–140.3 % is corroboration, never the finding on its own. CPU % cannot
  tell "ANE running" from "GPU running, driven from CPU" from "slower and idle".
- **tie-breaker if the two disagree:** `powermetrics --samplers ane_power,gpu_power` alongside the
  window settles it outright.

Failing the primary criterion means the E5RT fallback of §3.2 is still in force and **the arm is
dead whatever rows/s says** — a CoreML arm that wins on throughput while still logging shape
rejections is measuring something else and must be investigated, not banked.

**Gate 4 — session-create cost.** Time `InferenceSession` construction cold, 3 repeats per arm,
timed the same way S3–S5 time their windows (wall clock around the construction call, server freshly
started). Two lines, both able to go red:

- **≥ A0 + 10 s: rejected**, on the same footing as a Gate 1 miss. `serve` restarts are routine —
  §6.1 makes one mandatory per measurement — so a minute of CoreML compilation on every start is a
  user-visible regression that a throughput win does not buy back.
- **A0 + 3 s to A0 + 10 s: escalated to an owner ruling**, reported with the arm's throughput so the
  trade is visible. Below A0 + 3 s: passes silently.

This gate was written as "reported as" in the first draft, which made it an annotation rather than a
gate and contradicted this section's own opening.

### 6.4 What gets recorded per arm

rows/s (pending before → after, window length to 0.1 s), CPU mean/max and thread count, cold
session-create seconds, the Gate 2 distribution for **A1 and A3**, and for A2/A3 the E5RT
rejection count and the partition/placement lines Gate 3 reads. Every one with its command and its
tag, in the same table shape as the profile document's §3, so the two records read against each
other. A3 is reported twice — against **A4** (isolating the execution provider) and against **A0**
(the end-to-end number a user would feel).

---

## 7. Recommendation to the owner, ahead of the numbers

**Order the arms A0 → A3 → A2 → A1**, and be prepared to stop early.

A3 is the arm with the best cost-to-chance ratio, but it is not the free win the first draft of this
record called it. It has no provenance problem and needs no new artifact — but it has **two** open
unknowns, not one: whether static shapes let CoreML schedule the graph (Gate 3 answers it directly,
on-mechanism), and whether a bound-shape CoreML run moves the vectors (Gate 2 — unmeasured, because
§3.1's 2.1e-7 came from a CPU-fallback run). Run **A4** immediately after A3 regardless of the
result: it is a one-line change, it is the only thing that makes A3's number attributable, and it
answers the bucketing question §3.2 leaves open.

A1 should be measured **last and possibly not at all.** Three independent things point away from it
before any timing exists: the desk parity at 0.964 (§4.3), the model card's own PTQ figure of
hit@1 .200 → .133 (§2), and the fact that it extends ADR-0049's accepted cross-ISA defect to the
code corpus (§5b) while having nowhere to publish the artifact (§5a). If A3 clears its bar, A1 is
answering a question that no longer needs asking.

And **the cheapest win in this whole area is still not an inference change.** The profile document's
Finding 4 is that **207 s of a 1,061 s drain (19.5 %) is the 15-second poll timer, not inference**
(`docs/work/2026-08-22-code-ingestion-profile.md` §5 **[read]**) — which is WP1, already in wave 1,
already approved, and worth more than a 1.25× inference arm. That belongs in the recommendation so
the owner reads any WP7 result against it.

---

## 8. What is unverified

- Whether `faxenoff/code-daemon-embed-v1` still hosts `model_int8qdt.onnx`, and whether it is a
  genuine QAT artifact. If it is, the int8 arm is a download, not a recipe, and §4 is moot (§2).
- **Vector drift under a bound-shape CoreML arm.** §3.1's 2.1e-7 is a CPU-fallback measurement and
  does not cover A3. Gate 2 settles it; there is no prediction.
- Whether A3's static padding can coexist with WP12-A's length-sorted batches via bucketing (§3.2).
  **A4 is the arm that answers this**, and it is cheap.
- CoreML cold session-create as a number (§3.3) — Gate 4 settles it.
- Whether `COREML_FLAG_ONLY_ENABLE_DEVICE_WITH_ANE` changes A3's partitioning. Not tried.
- Everything about throughput. **No newly measured rows/s figure appears anywhere in this
  document** — the only rows/s here are the profile record's published baselines (2.347, 1.902) and
  Gate 0's band derived from one of them.

## 9. Reproducing the desk half

```sh
# CoreML availability, pinned package
nm -gU ~/.nuget/packages/microsoft.ml.onnxruntime/1.29.0/runtimes/osx-arm64/native/libonnxruntime.dylib \
  | grep -i AppendExecutionProvider
nm -gDU ~/.nuget/packages/microsoft.ml.onnxruntime/1.29.0/runtimes/linux-x64/native/libonnxruntime.so \
  | grep AppendExecutionProvider

# managed API — metadata, not strings (the string heap suffix-folds the name)
#   System.Reflection.Metadata over
#   ~/.nuget/packages/microsoft.ml.onnxruntime.managed/1.29.0/lib/net8.0/Microsoft.ML.OnnxRuntime.dll

# graph inventory + quantization, scratch venv
python -m venv onnxenv && ./onnxenv/bin/pip install onnx onnxruntime sympy
#   onnx 1.22.0, onnxruntime 1.29.0

# CoreML parity + partition report: dotnet run coreml.cs <model.onnx>
#   file-based .NET 10 app, "#:package Microsoft.ML.OnnxRuntime@1.29.0",
#   SessionOptions.LogSeverityLevel = ORT_LOGGING_LEVEL_VERBOSE
```

**The probe scripts are committed**, at
[`docs/work/scripts/2026-08-23-code-engine-inference/`](scripts/2026-08-23-code-engine-inference/)
with a README mapping each one to the section it produces: `probe.cs` (§1.1), `coreml.cs` (§3.1/§3.2,
backend as an argument so both rows reproduce), `inspect_onnx.py` (§2/§4), `matmul_inputs.py` (§4.1),
`ort_optimize.py` and `quantize.py` (§4.2), `parity.py` (§4.3). "Every figure carries its command" is
not met by a command naming a file nobody else has.

Both were re-run from the committed location and reproduce exactly: `coreml.cs … neuralnetwork`
returns `partitions: 32 … nodes: 197 … supported: 104` with `maxAbsDelta 1.919E-005`, and `parity.py`
returns `0.957656 / 0.964204 / 0.969362` at seq 64 — the §3.1 and §4.3 figures to six decimals.
**[measured]**

The derived int8 artifact (`e00cf954…e7d660`, 47,034,766 B) is deliberately **not** committed; wave 3
re-derives it from §4.2 in about five seconds rather than trusting a file it did not build.
