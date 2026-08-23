# Probe scripts — code-engine inference research (2026-08-23)

The scripts behind every measured figure in
[`docs/work/2026-08-23-code-engine-inference-research.md`](../../2026-08-23-code-engine-inference-research.md).
They are research probes, not product tooling: nothing in `src/` or `tests/` references them, and
they are committed only so the record's numbers can be re-derived rather than taken on trust.

## Setup

```sh
python -m venv onnxenv && ./onnxenv/bin/pip install onnx onnxruntime sympy
# measured with: onnx 1.22.0, onnxruntime 1.29.0 (the same version
# Directory.Packages.props:34 pins for the .NET package), sympy for quant_pre_process
```

`coreml.cs` and `probe.cs` are .NET 10 file-based apps and need no project — `dotnet run <file>.cs`
resolves `#:package` on its own.

**Two things bite a file-based app placed inside this repo**, both found by running these from here
rather than from a scratch directory:

- Central Package Management applies, so `#:package Foo@1.29.0` fails with
  `NU1008: … cannot define a value for Version`. Both files carry
  `#:property ManagePackageVersionsCentrally=false` to opt out; the version stays pinned in the file,
  which is the point — these probes must run against **1.29.0** specifically, not whatever
  `Directory.Packages.props` moves to next.
- The repo's analyzer settings apply too, and IDE0011 (`Add braces`) is an error here. A probe
  written loosely elsewhere will not build once committed.

## What produces what

| Script | Record section | Produces |
|---|---|---|
| `inspect_onnx.py <model>` | §2, §4.1, §4.2 | opset, producer, node inventory, initializer counts by dtype — the evidence that the shipped model is fp32 and that the quantized one is 25 `MatMulInteger` |
| `matmul_inputs.py <model>` | §4.1, §4.2 | classifies each `MatMul`'s two inputs by producer — the seven-row table showing every weight sits behind a `Transpose` |
| `ort_optimize.py <in> <out> [basic\|all]` | §4.2 | step 1 of the int8 recipe; `all` reproduces the rejection |
| `quantize.py <in> <out> int8\|uint8` | §4.1, §4.2 | `quantize_dynamic` |
| `parity.py <a> <b>` | §4.3 | per-sequence-length cosine between two models over 24 seeded samples — the 0.964 figure **and** its 0.9999 negative control |
| `coreml.cs <model> [mlprogram\|neuralnetwork]` | §3.1, §3.2 | CoreML partition/placement counts and CPU-vs-CoreML parity; run with ORT's verbose log visible to see the `GetCapability` lines and Apple's E5RT shape rejections |
| `probe.cs <assembly.dll>` | §1.1 | reads `SessionOptions` / `CoreMLFlags` from assembly **metadata** — the check that `strings` cannot do, because the .NET string heap suffix-folds `AppendExecutionProvider_CoreML` into the P/Invoke name |

## Not committed

The derived 47 MB int8 artifact. §4.2 is the recipe; wave 3 re-derives it rather than trusting a file
it did not build. Reproducing it from `model.onnx` takes about five seconds.
