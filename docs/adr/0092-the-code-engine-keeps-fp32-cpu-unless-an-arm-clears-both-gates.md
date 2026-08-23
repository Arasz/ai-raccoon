# 0092. The code engine keeps fp32 CPU unless an arm clears both gates

Date: 2026-08-23

Status: **Draft** — the decision shape only. The throughput numbers this record turns on do not
exist yet: post-delta-4 **WP7**'s measured arms run in wave 3, alone on a quiet machine, and G3
approved research only — *"no production edit and no engine swap until the owner rules on what it
finds"*. Every `<TBD>` below is filled from that run. Not yet listed in `docs/adr/README.md`; it is
added there when this leaves Draft.

**Evidence:** `docs/work/2026-08-23-code-engine-inference-research.md` (desk half — CoreML
availability, graph inventory, quantization recipe, parity smoke test, and the gate thresholds
named before the run).

## Context

The code corpus's embed drain is **99.66 %** of a code ingest, and 99.5 % of the drain is one native
ONNX call (`docs/work/2026-08-22-code-ingestion-profile.md` Finding 1). The code engine
(`faxenoff/code-daemon-embed-v1`, 768-dim) runs an **fp32** graph — 187,286,767 B, all 70
initializers `FLOAT`, zero `MatMulInteger` — while the bundled *memory* model has been u8s8-quantized
since ADR-0049. Two arms were proposed to close that gap:

- **int8** — dynamic quantization of the code model's ONNX with `onnxruntime`'s `quantize_dynamic`.
- **CoreML** — the CoreML execution provider via `SessionOptions.AppendExecutionProvider_CoreML`,
  available on osx-arm64 in the already-pinned `Microsoft.ML.OnnxRuntime` 1.29.0.

The desk half established four things that constrain any decision taken here:

1. The CoreML EP **is** in the pinned package on osx-arm64 and **is not** in the linux-x64 or
   win-x64 natives, so the call must be guarded by an OS check or the engine fails to start.
2. Under the `COREML_FLAG_CREATE_MLPROGRAM` backend the EP agrees with the CPU EP to
   `cos = 1.0000000`, max component delta **2.1e-7** — the CoreML arm changes no vectors. Under the
   *default* NeuralNetwork backend it rejects every `MatMul` and is not worth measuring.
3. Apple's runtime rejects the graph's dynamic `['batch','seq']` shapes, which
   `OnnxEmbeddingGenerator.cs:130` makes concrete per batch. So the CoreML arm is **not** a
   `SessionOptions` call: it needs a fixed or bucketed padding policy, which pulls against WP12-A's
   length-sorted batches.
4. `quantize_dynamic` as specified quantizes **0 of 29** weight MatMuls, because every weight sits
   behind a `Transpose`. A folding step first yields a real 47 MB / 25 `MatMulInteger` artifact —
   whose vectors sit at **cos ≈ 0.964** against fp32 on a smoke corpus, against a 0.9999 control.

The decision this ADR exists to take is therefore **not** "which arm is fastest". It is: under what
conditions is the code engine's arithmetic path allowed to change at all.

## Decision

**The code engine keeps fp32 on the CPU EP. An arm replaces it only by clearing both a throughput
gate and, where it moves vectors, an accuracy gate — each named before the measurement, each able to
go red.**

`<TBD — which arm, if any, cleared its gates. If none did, this ADR ships as the record of a
deliberate no-change, which is the expected outcome for int8.>`

The gates, from the research record §6.3:

| Gate | Applies to | Threshold |
|---|---|---|
| **0 — protocol validity** | run first | re-baselined fp32 within **±15 %** of S4's 2.347 rows/s (1.995–2.699). Outside it, stop; nothing is comparable |
| **1 — throughput** | int8 | **≥ 1.5×** the re-baselined fp32 |
| **1 — throughput** | CoreML | **≥ 1.25×** the re-baselined fp32 (lower bar: costs no accuracy, no re-embed) |
| **2 — accuracy** | int8 only | mean **top-5 overlap ≥ 0.80** over ≥ 30 code queries; mean per-chunk **cosine ≥ 0.99**, p1 **≥ 0.97**. Failing this rejects the arm **regardless of Gate 1** |
| **3 — off-CPU** | CoreML | CPU band must separate from fp32's 124.3–140.3 %. Same band ⇒ the EP is falling back and the arm is dead whatever rows/s says |
| **4 — startup** | all | cold `InferenceSession` construction ≤ fp32 + 10 s, or reported as a startup regression |

Measured on the S3–S5 protocol verbatim (same 469-file / 1,762-chunk corpus, cap 5, restart and
re-activate, fixed 150 s window, `top -l 6 -s 5 -stats cpu,th`), **alone on the machine** — the
condition whose absence made #511's numbers unusable.

Three things follow from the shape of the decision rather than from any number:

- **An arm below its bar is rejected in the record, not re-run with a lower bar.** ADR-0049 records
  that this project has widened a bound twice before and that widening is what let a real property
  stay hidden.
- **A faster engine that returns different results is not a faster engine.** Gate 2 is not
  subordinate to Gate 1.
- **CoreML must never be reached off Apple.** The guard is a correctness requirement, not defence in
  depth.

## Consequences

### Positive

- The cheap arm is separated from the expensive one on evidence rather than intuition: CoreML costs
  no vector churn and no re-embed, so it can be judged on complexity alone.
- The gates are falsifiable before the run, so a green result means something. Gate 3 in particular
  can reject a CoreML arm that *wins* on throughput for the wrong reason.
- Naming Gate 0 catches the #511 failure mode during the measurement instead of after it.

### Negative

- `<TBD>` If int8 is adopted, it **extends ADR-0049's accepted defect to a second corpus**: u8s8
  matmul takes three arithmetic paths (arm64 NEON / x64 `VPMADDUBSW` / VNNI `VPDPBUSD`), and that
  spread was worth 0.070 nDCG@5 — fourteen times the gate tolerance — on the memory corpus. Bounded
  by ADR-0085 (the code corpus is a re-derivable cache, so losing it costs a re-ingest), but real.
- `<TBD>` If int8 is adopted, the artifact **has no provenance story**. `ModelDownloadPlanner`
  downloads from a HF repo and pins per-file SHA-256; a file produced locally by `quantize_dynamic`
  has no upstream to pin against. That is a distribution decision, and arguably a larger objection
  than any timing.
- `<TBD>` If CoreML is adopted, the fixed/bucketed padding it requires **pulls against WP12-A's
  length-sorted batches**, which exist to reduce padding. Bucketing may satisfy both; unmeasured.
- Either arm changes the arithmetic path, so `MiniLmGoldenVectorTests` and the parity golden are
  downstream (ADR-0049).

### Neutral

- No `Directory.Packages.props` change either way: the CoreML EP is compiled into
  `Microsoft.ML.OnnxRuntime` **1.29.0**, which `AiRaccoon.Infrastructure.csproj:19` already
  references.
- The int8 arm's fingerprint change triggers a full code re-embed by design (ADR-0084 fingerprint,
  ADR-0087 one-transaction invalidation) — ≈ 1,061 s at today's rate. That is the mechanism working,
  not a cost the ADR introduces.
- `docs/work/2026-08-21-code-search-exploration.md:14,48,49,81` describes the shipped code model as
  "INT8 QAT". It is fp32; the research record corrects it. That correction stands whatever this ADR
  decides.

## Alternatives considered

- **Adopt int8 unconditionally, matching the memory model.** Rejected as the framing: ADR-0049
  accepted u8s8 for the memory model on a specific bounded-risk argument, and inheriting that
  argument for a second corpus without re-measuring is exactly the reasoning ADR-0049 warns against.
  The desk parity (0.964) and the model card's own PTQ figure (hit@1 .200 → .133) both point away.
- **Adopt CoreML unconditionally because it is free.** Rejected: it is not free (§3 of the record —
  a padding-policy change, an OS guard, and a WP12-A conflict), and Gate 3 exists because "the EP
  loaded" and "the EP ran off-CPU" are different claims.
- **Ship fp32 → fp16 instead.** Not evaluated. Named so a later reader knows it was not measured
  rather than measured and rejected.
- **Change nothing about inference and take the poll-timer win instead.** Partly adopted already:
  the profile record's Finding 4 is that **207 s of a 1,061 s drain (19.5 %) is the 15-second poll
  timer, not inference**, which is WP1 — approved, in wave 1, and worth more than a 1.25×
  inference arm. Any WP7 result must be read against it.
- **Measure on the live bank or beside other lanes.** Rejected outright: #511 produced 2.8×
  same-binary variance under 5–7 concurrent lanes on an unchanged branch.
