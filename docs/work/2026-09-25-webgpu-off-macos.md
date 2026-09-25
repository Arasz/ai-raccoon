# Research: the execution provider off macOS, and whether WebGPU can reach it

**Date:** 2026-09-25
**Question:** What execution provider does the bundled engine use on hosts other than macOS, and can it be WebGPU there (and CUDA as an opt-in)?

## Findings

### F1 — Off macOS the session always runs on the CPU [READ]

`GpuAvailable()` returns true only when `OperatingSystem.IsMacOS()` and ORT lists
`WebGpuExecutionProvider`. On Windows and Linux it is false, so `preferGpu` is ignored and the
constructor goes straight to `CreateCpuSession`. MLX is gated on macOS arm64 as well. ADR-0108
records this as a known consequence, with a DirectML or CUDA build named as the follow-up.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:99,219-221,293`; `docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md:110-112`

### F2 — The standard ORT 1.30.0 package implements WebGPU only in its osx-arm64 binary [MEASURED]

The osx-arm64 dylib carries 38 `WebGpuExecutionProvider` strings and 353 Dawn/wgpu strings. The
linux-x64, linux-arm64, win-x64 and win-arm64 binaries each carry one `WebGpuExecutionProvider` string
and zero Dawn/wgpu strings. The single hit matches their single `DmlExecutionProvider` and
`CUDAExecutionProvider` hits, so it is ORT's table of provider names, not an implementation. Removing
the `IsMacOS()` guard would therefore not help: those builds would refuse the append.

**Evidence:** `strings <binary> | grep -c WebGpuExecutionProvider` and `grep -c -E 'wgpu|dawn::|WGPU'` over `~/.nuget/packages/microsoft.ml.onnxruntime/1.30.0/runtimes/*/native/`, Apple M4, 2026-09-25.

### F3 — Microsoft ships WebGPU for Windows and Linux as a separate plugin EP package [MEASURED]

`Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0 (verified Microsoft owner on nuget.org) ships native
plugins for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64. The README asks for
`Microsoft.ML.OnnxRuntime` 1.24.4 or later (the repo pins 1.30.0), and on Linux it needs the system
Vulkan loader `libvulkan.so.1`. It has no linux-musl-x64 build, and that is one of the tool's RIDs.

**Evidence:** Downloaded `https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.EP.WebGpu/0.4.0`, unzipped it and listed `runtimes/`. Read `README.md` and the nuspec from the package. `strings` on the linux-x64 `.so` shows `libvulkan.so.1`. RIDs from `src/AiRaccoon/AiRaccoon.csproj:5`.

### F4 — The plugin is loaded the same way the repo already loads MLX [READ]

The plugin's documented usage is `RegisterExecutionProviderLibrary`, then `GetEpDevices`, then
`AppendExecutionProvider(env, devices, …)`. The MLX session already uses that plugin-EP path, so a
WebGPU plugin session can reuse the same shape. It also keeps the process-wide `GpuGate`
serialization and the `GpuSessionConfigEntries`.

**Evidence:** `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0 `README.md` "Usage"; `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:398-403`

### F5 — The plugin adds 14-40 MB of native files per RID and fits the package ceiling [INFERRED]

On disk the plugin is 16.5 MB for linux-x64, 13.9 MB for linux-arm64, and 35.3 MB for win-x64 (the
plugin DLL plus `dxcompiler.dll` and `dxil.dll`). ADR-0108 puts the nuget.org ceiling at about 250 MB
per package and the model at 97 MB. The per-RID tool package should stay well under the limit. This
estimate comes from uncompressed file sizes (`ls -la` on the unzipped package) and the ADR's figures.
No package was packed to confirm it.

### F6 — WebGPU on Windows/Linux reproduces CPU vectors for granite fp16 [UNVERIFIED]

ADR-0108 measured cosine 0.9998 between WebGPU fp16 and CPU fp16 only on Metal (M4). On Windows,
Dawn runs through D3D12, and on Linux through Vulkan, and those backends have different kernels. No
Windows or Linux GPU machine was available.

### F7 — WebGPU on those hosts is faster than their CPU path [UNVERIFIED]

Not measured. On CPU-only CI runners and headless servers there is often no adapter, so the append
fails and the session falls back to the CPU.

### F8 — The WebGPU plugin loads Vulkan at run time, so a missing loader is ours to fall back from [MEASURED]

The linux-x64 plugin's `NEEDED` entries are only glibc/libstdc++ libraries; `libvulkan.so.1` appears
as a string next to `Couldn't load Vulkan: %s`, i.e. Dawn `dlopen`s it when it enumerates adapters.
Registering the plugin therefore succeeds on a host without Vulkan, and the failure surfaces later as
no WebGPU device (the plugin README's own example throws `No WebGPU device found` there). ONNX Runtime
does not fall back on its own: the append is our call, so the generator must treat "no device" and any
`OnnxRuntimeException` as a refusal and build a CPU session — the same shape as the MLX path.

**Evidence:** `objdump -p` (NEEDED) and `strings` on `runtimes/linux-x64/native/libonnxruntime_providers_webgpu.so` from `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0; README "Troubleshooting". Apple M4, 2026-09-25.

### F9 — The CUDA provider is 272 MB, over the nuget.org package ceiling, so it cannot be bundled [MEASURED]

`Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.30.0 is a 236 MB nupkg; its `libonnxruntime_providers_cuda.so`
alone is 272,054,000 bytes. ADR-0108 puts the nuget.org ceiling at about 250 MB. CUDA has to be a
library the user installs and points the tool at.

**Evidence:** `curl` of `https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.linux/1.30.0/…nupkg`, `ls -la` of the unzipped `runtimes/linux-x64/native/`; `docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md:52-53`.

### F10 — The CUDA provider exports the plugin-EP entry points, and needs CUDA 13 on the host [MEASURED]

`libonnxruntime_providers_cuda.so` 1.30.0 exports `CreateEpFactories` and `ReleaseEpFactory` (the same
entry points the WebGPU plugin exports), and lists `libcudart.so.13`, `libcublas.so.13`,
`libcublasLt.so.13`, `libcurand.so.10` and `libcuda.so.1` as `NEEDED`. It also needs
`libonnxruntime_providers_shared.so`, which the standard CPU package already ships.

**Evidence:** `objdump -T` / `strings` / `objdump -p` on the unzipped `Microsoft.ML.OnnxRuntime.Gpu.Linux` 1.30.0 native files; `ls` of `~/.nuget/packages/microsoft.ml.onnxruntime/1.30.0/runtimes/linux-x64/native/`.

### F11 — The standard CPU core can register the CUDA provider as a plugin library [INFERRED]

From F10 (the provider exposes `CreateEpFactories`) and F4 (the core already registers plugin
libraries for MLX), `RegisterExecutionProviderLibrary("CUDAExecutionProvider", <path>)` against our
unchanged `Microsoft.ML.OnnxRuntime` core should work, provided the provider's version matches the core
exactly (1.30.0). Not run: no NVIDIA host was available. If it fails, the refusal message says why and
the session falls back.

### F12 — A one-off Linux x64 GPU benchmark on Azure costs well under 5 EUR [READ]

`Standard_NC4as_T4_v3` (4 vCPU, 28 GiB, 1x T4, x64) lists at $0.526/h on demand and $0.149/h spot
(Linux, East US). A two-hour session is about $1 on demand. New subscriptions usually have zero
NC-family quota, so a quota request comes first.

**Evidence:** https://instances.vantage.sh/azure/vm/nc4ast4-v3 and https://www.devzero.io/instances/azure/Standard_NC4as_T4_v3, read 2026-09-25.

### F13 — Windows GPU and linux-arm64 GPU VMs cost more and were not priced [UNVERIFIED]

Windows licensing adds to the NC hourly rate; Azure has no arm64 GPU size I know of (AWS `g5g`,
Graviton + T4G, would be the arm64 option). Free notebook GPUs (Colab, Kaggle T4) run Linux x64 only.
None of these prices was read.

### F14 — On a GPU-less ubuntu runner the plugin loads, then refuses at adapter discovery, and the session falls back to the CPU [MEASURED]

On GitHub's `ubuntu-latest` x64 runner the plugin registered, ONNX Runtime reported the Hyper-V
display (`MSFT1000`) as a GPU device, so the `HardwareDevice.Type == GPU` filter passed it. Dawn then
logged `vkCreateInstance: Found no drivers!` and session creation threw
`Failed to get a WebGPU adapter: No supported adapters`. The generator caught it and built the CPU
session: `CPU (GPU refused: [ErrorCode:Fail] … Failed to get a WebGPU adapter: No supported adapters)`.
No crash, and the Slow lane took 3m32s (the previous head 4m24s). The device filter does not keep out
a virtual adapter; the catch is the containment.

**Evidence:** PR #740 CI run 36072932297, job `build-slow` 107877792069, test `BundledEngineGpuSessionTests.PreferGpu_OffMacOs_RunsOnThePluginWebGpu_OrFallsBackWithAReason` failure text and stderr, 2026-09-24T23:30Z.

## Still open

- Numeric parity (F6) and latency (F7) on a real Windows D3D12 and a Linux Vulkan GPU. Settle them by running the golden-vector and bench gates there.
- Behaviour on a Linux host without `libvulkan.so.1` (F8 says registration succeeds and discovery fails), and on one with only a software Vulkan driver (Mesa lavapipe) — would Dawn offer a CPU-emulated adapter that is slower than the CPU path? Filtering devices to `HardwareDevice.Type == GPU` should exclude it; unverified.
- Whether CUDA registers as a plugin against the CPU core (F11) — settle on the first NVIDIA host, e.g. the F12 VM.
- The plugin is versioned 0.x. Its ABI compatibility with future ORT bumps beyond "1.24.4 or later" is not documented.
- linux-musl-x64 has no plugin build, so it stays CPU-only.
- Whether the plugin should replace the built-in WebGPU on osx-arm64 too, so there is one code path. Changing the provider changes nothing for vectors, but the M4 bench would need re-running.
