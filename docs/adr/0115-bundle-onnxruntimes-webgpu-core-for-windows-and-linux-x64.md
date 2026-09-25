# 0115 — Bundle ONNX Runtime's WebGPU-enabled core for Windows and Linux x64

Date: 2026-09-25

Status: Accepted, amended 2026-09-25 (NVIDIA containers without an ICD manifest)

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
takes the Node binding or `DirectML.dll`: nothing imports them. The pack refuses a RID whose core is
missing (`RequireWebGpuCoreFiles`).

**The swap happens at build time, in place of the NuGet core, with no runtime code.** A
`Directory.Build.targets` hook after `ResolvePackageAssets` repoints ORT's own native asset items
(`NativeCopyLocalItems` for a RID build, `RuntimeTargetsCopyLocalItems` for a RID-less one) at the
fetched core, keeping their file name and destination, and adds DXC beside it on Windows. The plain
`onnxruntime` import then finds the WebGPU core wherever it would have found the NuGet one: under JIT
probing, under Native AOT's lazily bound P/Invokes, or through a `DirectPInvoke` resolved by the OS
loader ([Native AOT interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)).
The NuGet core stops shipping on those RIDs. A first draft registered a `DllImportResolver` instead.
That needed a module initializer in every entry point, raced ORT's own resolver registration, and
turned a bad core file into an exception on every ORT call. The build-time swap has none of those
problems. The same hook drops the `None` item ORT's `.props` adds to copy the NuGet `win-x64` core
to the output root (a .NET Framework convenience), because it targets the same destination.

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
  plugin DLL. The NuGet core no longer ships on those RIDs.
- Nothing in the process chooses between cores at run time, so a core file that cannot load fails
  every ORT call, just as a bad NuGet core would. `VersionContractPackedTests` checks each packed
  core against the fetched one, byte length for byte length.
- The managed package and the npm core must move together. A bump of `Microsoft.ML.OnnxRuntime`
  needs the matching `onnxruntime-node` tarball and hash in `bundle.py`.
- The opt-in CUDA plugin now registers against the bundled core. It still goes through the plugin
  path of onnxruntime#28329 and may abort the same way; it stays opt-in and untested.
- `build-slow` installs lavapipe after the main suites and runs the GPU session tests with
  `AIRACCOON_REQUIRE_WEBGPU=1`, so a core that loses WebGPU, or a session that falls back, fails CI.

## Amendment (2026-09-25): NVIDIA containers without an ICD manifest

On a Lightning AI Tesla T4 the WebGPU core runs through Dawn's Vulkan backend (`libvulkan.so.1` →
`libGLX_nvidia.so.0`), but the container mounts the driver without its ICD manifest, so the loader finds
no driver and every session fell back to the CPU. The loader trace (`LD_DEBUG=files` on that T4,
2026-09-25) shows `libonnxruntime.so` loading `libvulkan.so.1`, which loads `libGLX_nvidia.so.0` only
once a manifest names it; without one, `vulkaninfo` reports `Found no drivers!` and
`BundledEngineGpuSessionTests` skips its WebGPU-only case. On Linux, the first WebGPU
session now checks the loader's manifest directories (`NvidiaVulkanIcd.ManifestDirectories`). If none
holds a manifest, no `VK_DRIVER_FILES`/`VK_ICD_FILENAMES`/`VK_ADD_DRIVER_FILES` is set, and
`libGLX_nvidia.so.0` exists, it writes a manifest to `$TMPDIR/ai-raccoon/nvidia_icd.json` and sets
`VK_ADD_DRIVER_FILES` through libc's `setenv`, because .NET's own setter does not reach the native
environment on Unix. The change is process-local: nothing under the user's home or `/etc` is written.
On the T4 the GPU session tests then pass with `AIRACCOON_REQUIRE_WEBGPU=1`.

## Evidence

`docs/work/2026-09-25-free-gpu-linux-test-hosts.md`: F11 (the plugin's abort on CI run 36078566359),
F13 (the node core's WebGPU run on CI run 36081136475, PR #748, and the `strings` reading of its
build info and per-RID Dawn content). `objdump -p` on the win-x64/arm64
`onnxruntime.dll` (no static import of DirectML or DXC).

## Related decisions

- [ADR-0112 — The WebGPU plugin off macOS, and an opt-in CUDA path](0112-webgpu-plugin-off-macos-and-opt-in-cuda.md):
  this ADR replaces its WebGPU plugin with a bundled core and keeps its CUDA path.
- [ADR-0108 — One bundled engine for memory and code, GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md):
  its GPU-then-CPU session shape now holds on Windows and Linux x64 too.
