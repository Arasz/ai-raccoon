# 0115 — Bundle ONNX Runtime's WebGPU-enabled core for Windows and Linux x64

Date: 2026-09-25

Status: Accepted

Research: `docs/work/2026-09-25-free-gpu-linux-test-hosts.md` (F11, F13)

## Context

ADR-0112 gave Windows and Linux a GPU path through `Microsoft.ML.OnnxRuntime.EP.WebGpu`, a plugin
execution provider, because the NuGet core (`Microsoft.ML.OnnxRuntime` 1.30.0) has WebGPU compiled
in only on osx-arm64. The first run with a real adapter showed the plugin cannot work. With Mesa's
lavapipe driver on the `ubuntu-latest` runner, the plugin built its session and then aborted the
process on the first run (`ortdevice.h:77 … Invalid memory type: -1`). That is
[onnxruntime#28329](https://github.com/microsoft/onnxruntime/issues/28329): plugin execution
providers hand the core a legacy memory type it rejects. An abort cannot be caught, so 1.51.2 turned
the plugin off and put Windows and Linux back on the CPU (ADR-0112 amendment).

The workaround suggested on that issue is a core with WebGPU compiled in, appended by name, as the
built-in provider is on macOS. ONNX Runtime publishes one: the `onnxruntime-node` 1.30.0 npm package
ships `libonnxruntime` for linux-x64, win-x64 and win-arm64 with Dawn inside. It comes from the same
release and commit as the managed package (`ORT Build Info … git-commit-id=f2c39fe2f`), and it has
no dependencies beyond glibc/libstdc++ on Linux and the MSVC runtime on Windows. Its linux-arm64 core
has no WebGPU. With that core swapped into the test output on CI, a lavapipe session ran on `WebGPU`,
matched the CPU session's vectors at cosine > 0.999, and survived two concurrent sessions, with no
abort (F13).

## Decision

**Ship ONNX Runtime's `onnxruntime-node` 1.30.0 core in the win-x64, win-arm64 and linux-x64
packages, and load it instead of the NuGet core.** `scripts/download-webgpu-core.py` fetches the
npm tarball (sha256-pinned in `scripts/src/bundle.py`) and unpacks each RID's core into the
git-ignored `src/AiRaccoon/webgpu-core/<rid>/`. On Windows it also takes `dxcompiler.dll` and
`dxil.dll`, the shader compiler Dawn's D3D12 backend loads, which the plugin also shipped. It never
takes the Node binding or `DirectML.dll`: nothing imports them. The build copies the files into
`webgpu/`, as it did the plugin's, and the pack refuses a RID whose core is missing
(`RequireWebGpuCoreFiles`).

**`OnnxRuntimeCore.EnsureWebGpuCore()` points ORT's native import at that core.** It registers a
`DllImportResolver` on the ORT managed assembly that loads `webgpu/onnxruntime.dll` or
`webgpu/libonnxruntime.so` for the `onnxruntime` import. The server's and the test host's module
initializers call it before any ORT API runs. ORT's own resolver steps aside when one is already
registered (`NativeMethods` static constructor, 1.30.0). A package without `webgpu/` keeps the NuGet
core.

**The generator asks the loaded core, not the OS.** `CreateGpuSessionOrNull` appends the built-in
WebGPU provider wherever `GetAvailableProviders()` lists it: macOS's NuGet core and the bundled core
alike. Elsewhere (linux-arm64, linux-musl-x64) it records
`CPU (GPU refused: this platform's ONNX Runtime core has no WebGPU)`. The plugin package, its copy
step, the plugin session path and its refusal cache are removed. The plugin registration helpers stay
for CUDA and MLX.

## Consequences

- Windows x64/arm64 and Linux x64 run the bundled engine on WebGPU again under `auto`. It is measured
  only on Linux x64 through lavapipe, a CPU Vulkan driver; a real GPU on Linux and D3D12 on Windows
  are unmeasured.
- Package size roughly nets out against the plugin. linux-x64: core 45.8 MB instead of 29.0 MB, minus
  the 16 MB plugin. win-x64: core 28.8 MB instead of 16.5 MB, the same DXC files, minus the 11.3 MB
  plugin DLL. The NuGet core still ships beside it, unused on those RIDs.
- The managed package and the npm core must move together. A bump of `Microsoft.ML.OnnxRuntime`
  needs the matching `onnxruntime-node` tarball and hash in `bundle.py`.
- The opt-in CUDA plugin now registers against the bundled core. It still goes through the plugin
  path of onnxruntime#28329 and may abort the same way; it stays opt-in and untested.
- `build-slow` installs lavapipe after the main suites and runs the GPU session tests with
  `AIRACCOON_REQUIRE_WEBGPU=1`, so a core that loses WebGPU, or a session that falls back, fails CI.

## Evidence

`docs/work/2026-09-25-free-gpu-linux-test-hosts.md`: F11 (the plugin's abort on CI run 36078566359),
F13 (the node core's WebGPU run on CI run 36081136475, PR #748, and the `strings` reading of its
build info and per-RID Dawn content). `NativeMethods.shared.cs` at ONNX Runtime `v1.30.0` (the
resolver's registration and its fallback when one exists). `objdump -p` on the win-x64/arm64
`onnxruntime.dll` (no static import of DirectML or DXC).

## Related decisions

- [ADR-0112 — The WebGPU plugin off macOS, and an opt-in CUDA path](0112-webgpu-plugin-off-macos-and-opt-in-cuda.md):
  this ADR replaces its WebGPU plugin with a bundled core and keeps its CUDA path.
- [ADR-0108 — One bundled engine for memory and code, GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md):
  its GPU-then-CPU session shape now holds on Windows and Linux x64 too.
