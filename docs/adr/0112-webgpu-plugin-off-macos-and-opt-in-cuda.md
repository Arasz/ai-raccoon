# 0112 — The WebGPU plugin off macOS, and an opt-in CUDA path

Date: 2026-09-25

Status: Accepted

Research: `docs/work/2026-09-25-webgpu-off-macos.md`

## Context

ADR-0108 put the bundled engine on the GPU through ONNX Runtime's WebGPU execution provider, but
only on macOS: `GpuAvailable()` returns true only when `OperatingSystem.IsMacOS()` and the runtime
lists `WebGpuExecutionProvider`, so a Windows or Linux session goes straight to the CPU (F1). That
guard is not an oversight waiting to be lifted. The standard ORT 1.30.0 package's Windows and Linux
binaries carry the `WebGpuExecutionProvider` string exactly once, in the provider-name table, and
zero Dawn/wgpu implementation strings; the osx-arm64 binary carries 38 and 353 respectively.
Removing the guard on those platforms would not append a working provider — it would just fail
differently (F2).

Microsoft ships the missing implementation as a separate package. `Microsoft.ML.OnnxRuntime.EP.WebGpu`
0.4.0, a Microsoft-owned nuget package, carries native plugins for win-x64, win-arm64, linux-x64,
linux-arm64 and osx-arm64. It asks for ORT 1.24.4 or later — the repo's pinned 1.30.0 clears that
floor comfortably — and, on Linux, for the system Vulkan loader, `libvulkan.so.1`. There is no
linux-musl-x64 build, so that RID stays CPU-only regardless of anything decided here (F3).

The plugin loads Vulkan at run time rather than linking against it. The linux-x64 binary's own
`NEEDED` entries are ordinary glibc/libstdc++ libraries, and `libvulkan.so.1` shows up only as a
string next to `Couldn't load Vulkan: %s` — Dawn `dlopen`s it while it enumerates adapters.
Registering the plugin therefore succeeds even on a host with no Vulkan loader at all; the failure
only shows up later, as no WebGPU device found. ONNX Runtime does not fall back to the CPU by
itself when that happens — appending a provider is the caller's decision, so this generator has to
treat "no device" exactly like it already treats a thrown `OnnxRuntimeException`, and build a CPU
session instead (F8).

CUDA is a different shape of problem, not a smaller one. `Microsoft.ML.OnnxRuntime.Gpu.Linux`
1.30.0 is a 236 MB nupkg on its own, and its `libonnxruntime_providers_cuda.so` alone is
272,054,000 bytes — past the roughly 250 MB ceiling nuget.org enforces per package, the same
ceiling ADR-0108 measured against the model weights. CUDA cannot ship inside the tool; it has to be
a library the operator installs separately and points the tool at (F9). That provider exports the
same `CreateEpFactories`/`ReleaseEpFactory` entry points the WebGPU plugin does, and needs
`libcudart.so.13`, `libcublas.so.13`, `libcublasLt.so.13`, `libcurand.so.10` and `libcuda.so.1` on
the host — CUDA 13, not CUDA 12 (F10). Registering it as a plugin library against the unmodified
CPU core should work, on the strength of that shared entry point and the precedent MLX already set
(ADR-0110), provided the provider's own build matches the core version exactly. Nobody has run it:
no NVIDIA host was available while writing this ADR (F11).

## Decision

**CUDA is opt-in through a device value and a path, never auto-detected.**
`ai-raccoon settings model device cuda <path-to-provider-library>` stores `embedding.device=cuda`
together with a new settings key, `embedding.cudaLibrary`, holding `Path.GetFullPath(path)`. Three
shapes are refused outright, with nothing written and an `ErrorCode.Usage.InvalidValue` exit:
`cuda` with no path, a path that does not exist on disk, and a path supplied together with any
device value other than `cuda`. The stored library path is only ever read back when the current
device is `cuda` — switching to `gpu` and back leaves it stored but inert (D1).

**Device selection filters on hardware type, not only on provider name.** For both the WebGPU
plugin and CUDA, the generator takes the first `OrtEpDevice` whose `EpName` matches and whose
`HardwareDevice.Type` is `OrtHardwareDeviceType.GPU`. MLX keeps the filter it already has (D2).

**Packaging follows the shape already used for MLX, not a `NativeCopyLocalItems` target.**
`Directory.Packages.props` pins `Microsoft.ML.OnnxRuntime.EP.WebGpu` at 0.4.0 — its nuspec declares
no dependencies — and `AiRaccoon.csproj` references it with `ExcludeAssets="all"` and
`GeneratePathProperty="true"`, so it contributes no assets of its own beyond a
`$(PkgMicrosoft_ML_OnnxRuntime_EP_WebGpu)` path property. A `None` item then copies
`$(PkgMicrosoft_ML_OnnxRuntime_EP_WebGpu)/runtimes/<rid>/native/*` into a `webgpu/` output folder,
`CopyToOutputDirectory="PreserveNewest"`, for `win-x64`, `win-arm64`, `linux-x64` and
`linux-arm64` only. The effective RID is `$(RuntimeIdentifier)`, or `$(NETCoreSdkRuntimeIdentifier)`
when that is empty — a RID-less test or dev build resolves to the host's own RID, so a macOS
build still gets nothing under `webgpu/` and an Ubuntu CI build gets the linux-x64 files.
`osx-arm64` and `linux-musl-x64` never receive this folder, matching the plugin's own build matrix
and the musl gap noted above (D3).

**macOS keeps its existing, built-in path untouched.** The session still calls
`AppendExecutionProvider("WebGPU")` directly there; the plugin is never registered on macOS, even
though both paths expose the same `WebGpuExecutionProvider` name. Windows and Linux instead resolve
a library file: `ResolveWebGpuPluginLibrary(baseDirectory)` looks for
`<base>/webgpu/onnxruntime_providers_webgpu.dll` on Windows or
`<base>/webgpu/libonnxruntime_providers_webgpu.so` on Linux, returning null when neither exists —
which the generator turns into the refusal "WebGPU plugin library not found under `<dir>`". Nothing
calls `WebGpuEp.GetLibraryPath()` (D4).

**One order, three GPU candidates, then the CPU.** A session tries MLX first when `device` is
`mlx`, then CUDA when `device` is `cuda`, then WebGPU — built-in on macOS, the plugin on Windows and
Linux — and only then the CPU. `ExecutionProvider` on the resulting session log is one of
`"CUDA"`, `"WebGPU"`, `"MLX"` or `"CPU"`, with the existing `"(X refused: reason)"` suffix chained
for every candidate that was tried and lost — for example `"WebGPU (CUDA refused: …)"` or
`"CPU (GPU refused: no WebGPU GPU device after registration (Linux needs libvulkan.so.1)) (CUDA
refused: …)"`. A plugin WebGPU session takes the same process-wide `GpuGate` the built-in one
already does, and sets `_needsGpuGateForRun`; CUDA sessions take no such gate (D5).

**`gpu` and `cuda` both reach the plugin path, at different scopes.** `device gpu` for a
downloaded, non-bundled model now goes through the same `CreateGpuSessionOrNull` the bundled engine
uses, so it can reach the WebGPU plugin on Windows and Linux too. `device cuda` applies to every
local model, bundled or not: `PrefersGpu(Cuda, _)` is true regardless of engine — a new case on the
existing helper, not a separate one — `PrefersMlx(Cuda)` is false, and a new `CudaLibraryFor(device,
stored)` returns the stored path only when `device == Cuda` and nothing otherwise (D6).

**Registration is one shared, name-keyed table.** `EnsureRegistered(string name, string libraryPath,
Action<string, string> register)` is keyed by provider name under one lock and takes the actual
registration call as a delegate, so a test can swap in a fake without touching `OrtEnv`; a second
overload, `EnsureRegistered(OrtEnv env, string name, string libraryPath)`, wraps it with
`env.RegisterExecutionProviderLibrary` for real callers. Registering the same name again at the same
path is a no-op; the same name at a different path is refused with "a different `<name>` provider
library is already registered in this process (restart the server)" — process-wide plugin
registration cannot be undone, so a second, different library under the same name has nowhere to
go. A failed registration is never recorded, so the next attempt tries again rather than replaying
a stale failure. MLX registers through the same table via the `OrtEnv` overload; only its
`SingleThreadExecutor` path — construction, Run, and Dispose pinned to one thread — stays its own.
WebGPU-plugin and CUDA sessions share a small new `CreatePluginSessionOrNull` instead (D7).

**Only a refusal that applies to every session is cached process-wide.** A missing library, a
failed registration, or no GPU device found after registration are kept in a static field, and
later session constructions on the same process reuse that cached reason instead of registering and
enumerating devices all over again — the case this guards is a CI runner building many sessions
with no GPU at all. A refusal from actually building one model's session on already-registered
devices is not cached: it is specific to that model, not to the process, so the next session
retries the plugin from scratch rather than replaying someone else's bad luck. There is no
environment-variable kill switch for any of this; an operator who wants no GPU attempts already has
`settings model device cpu` (D8).

**Plugin paths catch a fixed, narrow exception set — not everything.** `OnnxRuntimeException`,
`DllNotFoundException`, `EntryPointNotFoundException` and `BadImageFormatException` become a
refusal carrying the exception's message; a GPU attempt does not throw out of session construction
for any of the listed exceptions, but nothing here is a blanket `catch (Exception)`. The macOS
built-in WebGPU path is not a plugin load and keeps catching only `OnnxRuntimeException`, as before.
The CUDA path also refuses a library path that is not fully qualified — "provider library path must
be absolute: `<path>`" — before it ever hands the operator-supplied string to the native loader,
since a relative path resolves against the process's current directory rather than wherever the
operator meant (D9).

**The constructor gains one new, optional parameter.** `string? cudaLibraryPath = null` — null
means don't try CUDA at all, and an empty string means CUDA was asked for but has nothing to try,
which surfaces as a refusal with the hint "run `ai-raccoon settings model device cuda <path>`".
`EmbeddingService.CreateLocal` reads `EmbeddingSettingsKeys.CudaLibrary` only when the stored
device is `cuda` (D10).

**No new log events.** Every refusal already travels inside the `ExecutionProvider` string the
existing `EmbeddingSessionCreated` log line records; this ADR adds no `EventId` (D11).

## Consequences

- The `win-x64`, `win-arm64`, `linux-x64` and `linux-arm64` tool packages each grow by roughly
  14-35 MB, for the plugin file plus, on Windows, `dxcompiler.dll` and `dxil.dll`. `osx-arm64` and
  `linux-musl-x64` are unaffected.
- Speed and vector parity on a real D3D12 or Vulkan GPU are not measured — no Windows or Linux GPU
  machine was available while writing this ADR. Shipping it anyway is the owner's call, on the same
  terms ADR-0110 shipped MLX unmeasured on one axis; anyone who hits a problem is asked to report it
  on the repository.
- The engine fingerprint does not change. Nothing here alters what vector a given input produces
  once a GPU session is running — it only changes which execution provider gets tried, and in what
  order.
- Linux needs the system Vulkan loader, `libvulkan.so.1`, to actually get a WebGPU device. ONNX
  Runtime does not fall back to the CPU when that loader is missing: the plugin registers fine and
  only fails later, silently, unless something checks for a device. This code is that something —
  the failure surfaces as a `"CPU (GPU refused: …)"` suffix rather than a crash, or a silent CPU
  fallback the caller cannot tell apart from success. Measured on the GitHub ubuntu runner (loader
  present, no Vulkan driver): ONNX Runtime reports the Hyper-V display as a GPU device, so the
  hardware-type filter passes it, and session creation fails with `"CPU (GPU refused: … Failed to
  get a WebGPU adapter: No supported adapters)"`. The filter is not what contains this; the catch is.
  A host with no loader at all is expected to give the same or the "no WebGPU GPU device" refusal
  (not observed).
- CUDA is not bundled, and cannot be: its native provider is 272 MB, over the nuget.org package
  ceiling on its own. Whether it registers as a plugin against the unmodified CPU core, rather than
  needing a matching CUDA-flavoured core, is unverified — inferred from the shared plugin entry
  points and the MLX precedent, not measured on real hardware.
- The WebGPU plugin is versioned 0.x. Its documented compatibility floor is "ORT 1.24.4 or later",
  with nothing said about a ceiling, so any future ORT version bump has to re-run the off-macOS
  test in this ADR rather than assume the plugin still loads.
- Whether `dxcompiler.dll` and `dxil.dll` actually load and shader-compile on a real Windows GPU is
  unverified for the same reason as the parity numbers above: no Windows GPU host was available.
- `linux-musl-x64` stays CPU-only. The plugin has no musl build, and nothing here changes that.

## Alternatives rejected

- **A DirectML build for Windows.** DirectML is Windows-only, so it would not help Linux, and it
  needs its own ONNX Runtime build variant rather than a plugin against the existing core — a
  second packaging story next to this one, not instead of it.
- **Bundling the full ORT GPU package** (`Microsoft.ML.OnnxRuntime.Gpu.*`) instead of the plugin EP.
  Its CUDA provider alone is 272 MB, well past the nuget.org ceiling, and bundling the package for
  WebGPU only would still carry that weight, because the GPU package ships CUDA and WebGPU
  together.
- **An environment variable or a conventional directory for the CUDA library path**, instead of a
  stored settings key. Settings already have a home for exactly this kind of per-machine value
  (ADR-0014), a `settings model device` verb already existed for `auto`/`gpu`/`cpu`/`mlx`, and an
  environment variable would not survive a restart the way a stored path does.
- **Running the plugin on macOS too**, so there is one code path instead of two. Changing macOS's
  execution provider would change nothing about the vectors it produces, but it would need the M4
  bench from ADR-0108 re-run to confirm parity, and macOS's built-in WebGPU provider already works —
  there is no defect on that platform this would fix.

## Measuring it

A one-off Linux x64 check is affordable. Azure's `Standard_NC4as_T4_v3` (4 vCPU, 28 GiB, one T4
GPU) lists at $0.526/hour on demand and $0.149/hour spot, Linux, East US — a two-hour session is
comfortably under 5 EUR either way. New subscriptions typically start with zero NC-family quota, so
a quota request has to come first. Windows GPU pricing and an arm64 GPU size were not looked up;
Azure appears to have no arm64 GPU size at all, and AWS's `g5g` (Graviton plus T4G) would be the
option there if it is ever worth pricing.

## Evidence

`docs/work/2026-09-25-webgpu-off-macos.md`: F1 (the macOS-only GPU guard, and why), F2 (the
standard package's Windows/Linux binaries carry no WebGPU implementation), F3 (the separate plugin
package, its RIDs, its Vulkan dependency, and the musl gap), F8 (the plugin loads Vulkan by
`dlopen`, so a missing loader is a silent runtime failure this code has to catch), F9 (the CUDA
provider's size against the package ceiling), F10 (the CUDA provider's entry points and its CUDA 13
host requirement), F11 (why registering CUDA as a plugin against the unmodified core is expected to
work, and why that is unverified), F12 (the Azure T4 pricing), F13 (Windows and arm64 GPU pricing,
not checked).

## Related decisions

- [ADR-0108 — One bundled engine for memory and code: granite-embedding-small-english-r2 (fp16),
  GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md): this ADR extends its
  GPU-then-CPU session shape to Windows and Linux, and partially supersedes its "Windows and Linux
  run on the CPU" consequence.
- [ADR-0110 — An opt-in MLX execution provider for the bundled engine, osx-arm64 only](0110-opt-in-mlx-execution-provider-for-the-bundled-engine.md):
  MLX keeps trying first in the selection order this ADR extends, and this ADR's shared
  `EnsureRegistered` table generalises the once-per-process registration guard 0110 introduced for
  MLX alone.
