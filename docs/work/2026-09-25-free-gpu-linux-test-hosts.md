# Research: free Linux GPU hosts for testing AiRaccoon on x64 and a second architecture

**Date:** 2026-09-25
**Question:** Where can AiRaccoon get free compute with a GPU to test its Linux x64 GPU paths (WebGPU plugin over Vulkan, opt-in CUDA 13), plus at least one more architecture?

```chart:matrix
title: free options against what ADR-0112 needs
option, x64, arm64, real GPU, Vulkan, CUDA 13, automatable from CI
GitHub ubuntu-latest, yes, no, no, lavapipe only, no, yes
GitHub ubuntu-24.04-arm, no, yes, no, lavapipe only, no, yes
Kaggle notebook, yes, no, T4 / P100, unverified, driver says 13.2, partly
Google Colab free, yes, no, T4 (not guaranteed), unverified, unverified, partly
Lightning AI free, yes, no, T4 (~22 h/mo), unverified, unverified, partly
Oracle Always Free A1, no, yes, no, lavapipe only, no, self-hosted runner
```

## Findings

### F1 — No free option pairs arm64 with a GPU [INFERRED]

Every free arm64 host found is CPU-only: GitHub's arm64 runners (F3), Oracle's Always Free A1 (F7)
and AWS's t4g trial (a Graviton CPU instance). Every free GPU found is x64 (F4-F6). Reasons from
F3-F7 plus a search that turned up no free arm64 GPU offer; a real linux-arm64 GPU run needs a paid
host (e.g. AWS g5g, Graviton2 + T4G). So "x64 + GPU" and "second arch" come from different hosts.

### F2 — The Linux GPU paths need Vulkan (WebGPU) or CUDA 13, not just "a GPU" [READ]

The WebGPU plugin `dlopen`s `libvulkan.so.1` at run time, so a host without a Vulkan loader and ICD
registers the plugin but yields no adapter. The opt-in CUDA provider needs `libcudart.so.13` and
friends — CUDA 13, not 12. A GPU host that only exposes the CUDA compute stack (typical of notebook
containers) tests the CUDA path but may not test WebGPU at all.

**Evidence:** `docs/work/2026-09-25-webgpu-off-macos.md` F8 (lines 64-73) and F10 (lines 83-87); `docs/adr/0112-*.md:35-42`.

### F3 — GitHub gives this repo free linux-arm64 (and win-arm64) runners, with no GPU [READ]

The repo is public. `ubuntu-24.04-arm` / `ubuntu-22.04-arm` / `ubuntu-26.04-arm` are standard
4-CPU/16 GB arm64 runners, free for public repos; `windows-11-arm` likewise. No GPU runner is in the
standard (free) set; GitHub's T4 GPU runners are larger runners, billed per minute, Team/Enterprise
only. Every CI job today is `ubuntu-latest`/`ubuntu-slim` x64, so linux-arm64 is untested in CI.

**Evidence:** https://docs.github.com/en/actions/reference/runners/github-hosted-runners (standard runner table, fetched 2026-09-25); https://github.blog/changelog/2025-08-07-arm64-hosted-runners-for-public-repositories-are-now-generally-available/; `gh repo view --json visibility` → `PUBLIC`; `grep runs-on .github/workflows/*.yml`.

### F4 — Kaggle gives ~30 GPU-hours/week on T4 x2 or P100, x64, 12 h sessions [INFERRED]

Phone-verified account, no card. A shell is available through notebook `!` cells. A 2026-09 report
shows driver 595.91.07 / CUDA 13.2 on Kaggle, which would satisfy F2's CUDA 13 requirement on the
T4 (Turing); CUDA 13 dropped Pascal, so the P100 is not a CUDA 13 target.

Inferred from web-search summaries only: Kaggle's own doc page (https://www.kaggle.com/docs/efficient-gpu-usage) renders client-side and WebFetch returned only its title. The driver figure comes from a third-party summary; an `nvidia-smi` run settles it.

### F5 — Google Colab free gives a T4 most of the time, no fixed quota, 12 h cap [INFERRED]

x64. Availability is dynamic and not guaranteed; the Colab CLI (June 2026) gives terminal access,
but free users get the same constrained T4.

Inferred from secondary sources (https://chatforest.com/builders-log/google-colab-cli-gpu-tpu-terminal-agent-builder-guide/, https://joshthompson.co.uk/ai/google-colab-2026-guide-free-compute-automations-pro-tips/); Google's own FAQ was not read.

### F6 — Lightning AI free gives 15 credits/month (~22 T4-hours) with a real Studio VM [INFERRED]

x64, phone verification, no card. A Studio is a persistent Linux VM with SSH, closer to a dev box
than Kaggle/Colab, and the only free GPU option here where installing a Vulkan ICD is plausibly
under our control.

Inferred from search summaries of https://lightning.ai/pricing/ (renders client-side; WebFetch got only a loading message) and https://aicreditmart.com/ai-credits-providers/lightning-ai-free-plan-22-gpu-hours-month-guide-2026/.

### F7 — Oracle Always Free A1 was cut to 2 OCPU / 12 GB arm64 in June 2026, no GPU [INFERRED]

Usable as a persistent linux-arm64 self-hosted runner, but it adds nothing F3 does not already give
for free with zero maintenance.

Inferred from a search summary of https://www.infoq.com/news/2026/07/oracle-cloud-free-tier-limits/; Oracle's own page (https://docs.oracle.com/iaas/Content/FreeTier/freetier.htm) was not read.

### F8 — Mesa lavapipe can exercise the WebGPU code path on GitHub runners without a GPU [INFERRED]

lavapipe is a CPU Vulkan ICD installable with apt (`mesa-vulkan-drivers`); projects use it to run
WebGPU/Dawn suites on GPU-less GitHub runners. On `ubuntu-latest` and `ubuntu-24.04-arm` it would let
the plugin actually build a WebGPU session and compare vectors against CPU — the numeric check
(webgpu-off-macos F6) that currently always falls back. Reasons from F2, F3 and
https://github.com/oneilltomhq/three-rs/issues/18. Whether Dawn in the ORT plugin accepts a
CPU-type adapter by default, and whether ORT's device list reports it as `GPU` for our
`HardwareDevice.Type` filter, was not checked — either could make it a no-op.

### F11 — On x64 CI, lavapipe reaches Dawn, and the WebGPU plugin then aborts the process [MEASURED]

With `mesa-vulkan-drivers` installed on `ubuntu-latest`, `vulkaninfo` lists `llvmpipe` (CPU type).
The runner's Hyper-V display passes our `HardwareDevice.Type == GPU` filter, and Dawn accepts the
lavapipe adapter. The plugin session is built, then the test host aborts on the first run:
`ortdevice.h:77 … Invalid memory type: -1`, SIGABRT, exit 134. That is onnxruntime#28329. It
answers F8's open part (the adapter and the filter both pass on x64) and led to the 1.51.2 hotfix
(#747), which turns the plugin off on Windows and Linux.

**Evidence:** CI run 36078566359, job `build-slow`, step "WebGPU plugin on lavapipe", 2026-09-25T00:46Z.

### F12 — On linux-arm64, ORT reports no GPU device, so lavapipe never reaches Dawn [MEASURED]

In an `ubuntu:24.04` arm64 container with lavapipe installed, `vulkaninfo` lists `llvmpipe`, but the
session is refused with `no WebGPU GPU device after registration`. ONNX Runtime's device discovery
reads PCI/sysfs, not Vulkan, and the VM has no display device.

**Evidence:** `scripts/gpu-host-probe.sh` in Docker `--platform linux/arm64 ubuntu:24.04` on an Apple M4, 2026-09-25, commit c9b644d.

### F13 — onnxruntime-node's core runs built-in WebGPU on Linux x64 without aborting [MEASURED]

onnxruntime-node 1.30.0 ships `libonnxruntime` from the same ORT commit as our managed package
(`f2c39fe2f`) with WebGPU compiled in for linux-x64, win-x64 and win-arm64, but not linux-arm64.
Swapped into the test bin on x64 CI with lavapipe, the session runs on `WebGPU`, its vectors match the
CPU session's at cosine > 0.999, and two concurrent WebGPU sessions succeed. This is the workaround
suggested on onnxruntime#28329.

**Evidence:** CI run 36081136475, job `build-slow`, step "Built-in WebGPU with onnxruntime-node's core (experiment)" (PR #748): total 5, succeeded 3, skipped 2. `strings` on the npm package's `bin/napi-v6/*/libonnxruntime*`/`onnxruntime.dll`.

### F14 — The first linux-arm64 run found three test-only portability bugs, and no product bug [MEASURED]

The retrieval harness manifest pinned reference extensions only for osx-arm64 and linux-x64. The
MiniLM golden's bit-exact check compared only the CPU architecture, so macOS-captured Arm64 bits
were held against Linux Arm64 (384/384 differ). The search-envelope golden compared a cosine to the
last bit (`0.71177268…` vs `0.71177273…`). All three are fixed in #746. The rest of the Fast (4283)
and Slow suites passed on arm64.

**Evidence:** PR #746 CI, job `build-arm64`, 2026-09-25.

### F15 — On a Lightning AI Tesla T4, the bundled WebGPU core and the opt-in CUDA provider both run [MEASURED]

Lightning AI free Studio: x86_64, Ubuntu 24.04, Tesla T4, driver 580.178.04 (CUDA 13.0). The probe at
main 567e92e (1.52.0) ran `BundledEngineGpuSessionTests` (4 passed, only the macOS test skipped, no
`GPU refused` anywhere) and `BundledEngineCudaSessionTests` (5 passed, including
`CudaLibraryConfigured_RunsOnCuda_WithTheCpuSessionsVectors`: provider `CUDA`, cosine ≥ 0.999 against
the CPU). So on this host the CUDA plugin did **not** hit the onnxruntime#28329 abort. `vulkaninfo`
found no device there, because the container mounts `libGLX_nvidia.so.0` without its ICD manifest,
yet WebGPU still landed on the GPU. Which Dawn backend it used was not checked.

**Evidence:** `scripts/gpu-host-probe.py` over SSH on the Studio, 2026-09-25; PROBE SUMMARY `webgpu: Passed! total 5, failed 0, succeeded 4, skipped 1`, `cuda: Passed! total 6, failed 0, succeeded 5, skipped 1`. With a user-level `nvidia_icd.json` in `VK_DRIVER_FILES`, `vulkaninfo --summary` lists `Tesla T4 (DISCRETE_GPU)`, driver NVIDIA 580.178.04.

### F16 — The installed 1.52.0 tool embeds on WebGPU and about 4× faster than 1.51.2 on the same T4 [MEASURED]

Same Studio and workload for both: `serve --idle-timeout 0` on a fresh bank, `model embedding set
local`, then over the stdio MCP proxy 300 `memory_write` calls (each note is about 6× a 60-word
sentence), one `memory_embed_pending`, and one `memory_search`. 1.51.2 from nuget.org logged `execution
provider CPU (GPU refused: the WebGPU plugin aborts …)`, with the GPU at 0 % / 0 MiB. 1.52.0, packed
from main 567e92e because it was not on nuget.org yet, logged `execution provider WebGPU`, with the GPU
at 50–56 % / 169 MiB. There were no errors or aborts.

```chart:bars
title: seconds for 300 writes on a T4 (one run each)
1.51.2 CPU: 111.2
1.52.0 WebGPU: 26.4
```

**Evidence:** `mcp_drive.py` (session scratchpad) against ports 7731 (1.51.2) and 7732 (1.52.0) on the Studio, 2026-09-25, one run each. The drain afterwards took 14.4 s for 61 rows on 1.51.2 and 2.5 s for 101 rows on 1.52.0. `nvidia-smi --query-gpu=utilization.gpu,memory.used -l 1` sampled during each run.

### F9 — Kaggle/Colab/Lightning containers expose Vulkan [UNVERIFIED]

NVIDIA containers often ship only the compute driver capabilities, without the Vulkan ICD JSON.
Nobody ran `vulkaninfo --summary` on any of them. That one command decides whether a free x64 GPU
host tests WebGPU or only CUDA.

### F10 — Paid fallbacks are small [UNVERIFIED]

GitHub's T4 larger runners and RunsOn (GPU EC2 on any GitHub plan) cost cents per minute per
their own pages' summaries; prices were not read from a primary source.

## Recommendation

1. **Second architecture, today, free:** add an `ubuntu-24.04-arm` job to `build.yml` running the
   existing Linux suite (F3). Zero cost, CI-native.
2. **WebGPU code path in CI:** install lavapipe on both Linux jobs and see whether
   `BundledEngineGpuSessionTests` stops falling back (F8). Cheap to try; settles F8's open part.
3. **Real x64 GPU, manual:** a Kaggle notebook (T4, CUDA 13.2 driver) for the CUDA opt-in path, and
   Lightning AI if Kaggle lacks Vulkan (F4, F6, F9). Not CI-automatable for free; treat as a
   release-checklist step.
4. **Real arm64 GPU:** not free (F1). Defer or budget a few hours of AWS g5g.

## Still open

- Does Kaggle's T4 container have a Vulkan ICD? Settle with `!vulkaninfo --summary` in a GPU notebook.
- Kaggle, Colab and Lightning were not run: no browser session or credentials were available to the
  agent. `scripts/gpu-host-probe.py` does the whole check in one cell; the owner runs it once per host.
- Whether Windows D3D12 aborts like Linux under the plugin (inferred, same ORT code path), and whether
  the node core's Windows build needs `DirectML.dll` at load time.
- Kaggle's current driver (595.x / CUDA 13.2) is from a secondary source; `!nvidia-smi` settles it.
- Can a .NET 10 global tool install and run inside a Kaggle/Colab session (no root issues, internet on)?
  Likely yes via `dotnet-install.sh`, not tried.
