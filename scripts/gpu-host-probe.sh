#!/usr/bin/env bash
# Probes a Linux GPU host (Kaggle, Colab, Lightning AI, any NVIDIA VM) for the bundled engine's
# GPU paths: WebGPU through the bundled core (needs Vulkan) and opt-in CUDA (needs CUDA 13 libraries).
#
#   curl -fsSL https://raw.githubusercontent.com/Arasz/ai-raccoon/main/scripts/gpu-host-probe.sh | bash
#
# Prints a PROBE SUMMARY block at the end; paste it into the release checklist.
set -uo pipefail

REPO_URL="${AIRACCOON_REPO_URL:-https://github.com/Arasz/ai-raccoon.git}"
REF="${AIRACCOON_REF:-main}"
WORK="${AIRACCOON_PROBE_DIR:-$HOME/airaccoon-probe}"
ORT_VERSION="1.30.0"
SUDO=""; [ "$(id -u)" != 0 ] && command -v sudo >/dev/null && SUDO="sudo"

section() { printf '\n==== %s ====\n' "$1"; }

section "host"
uname -srm
(. /etc/os-release && echo "$PRETTY_NAME") 2>/dev/null
nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>/dev/null || echo "nvidia-smi: none"
nvidia-smi 2>/dev/null | grep -o 'CUDA Version: [0-9.]*' || true

section "vulkan"
if command -v apt-get >/dev/null; then
  $SUDO apt-get update -qq >/dev/null 2>&1
  $SUDO apt-get install -y -qq libvulkan1 vulkan-tools >/dev/null 2>&1 || echo "apt install of vulkan-tools failed"
fi
ls /usr/share/vulkan/icd.d /etc/vulkan/icd.d 2>/dev/null
VULKAN_SUMMARY="$(vulkaninfo --summary 2>&1)"
echo "$VULKAN_SUMMARY" | grep -E 'deviceName|deviceType|driverName|ERROR|Found no drivers' || echo "$VULKAN_SUMMARY" | tail -5

section "dotnet"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1
dotnet --version

section "source"
if [ ! -d "$WORK/.git" ]; then git clone -q --depth 1 --branch "$REF" "$REPO_URL" "$WORK"; fi
cd "$WORK" || exit 1
git log --oneline -1
python3 scripts/download-embedding-model.py
python3 scripts/download-webgpu-core.py | tail -1
mkdir -p .nupkg-local
dotnet build tests/AiRaccoon.Tests --nologo -v q 2>&1 | tail -2
TESTS=tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll

section "cuda provider"
CUDA_LIB=""
if nvidia-smi >/dev/null 2>&1; then
  python3 -m pip install -q "nvidia-cuda-runtime==13.*" "nvidia-cublas==13.*" "nvidia-curand==10.*" "nvidia-cudnn-cu13==9.*" 2>&1 | tail -1
  SITE="$(python3 -c 'import site; print(site.getsitepackages()[0])')"
  for so in libcudart.so.13 libcublas.so.13 libcublasLt.so.13 libcurand.so.10 libcudnn.so.9; do
    dir="$(dirname "$(find "$SITE/nvidia" -name "$so*" 2>/dev/null | head -1)" 2>/dev/null)"
    [ -n "$dir" ] && [ "$dir" != "." ] && LD_LIBRARY_PATH="$dir:${LD_LIBRARY_PATH:-}"
  done
  export LD_LIBRARY_PATH
  curl -fsSL -o /tmp/ort-gpu.nupkg "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.Gpu.Linux/$ORT_VERSION"
  mkdir -p "$WORK/.ort-gpu" && (cd "$WORK/.ort-gpu" && unzip -qo /tmp/ort-gpu.nupkg 'runtimes/linux-x64/native/*')
  CUDA_LIB="$WORK/.ort-gpu/runtimes/linux-x64/native/libonnxruntime_providers_cuda.so"
  ldd "$CUDA_LIB" | grep 'not found' || echo "all CUDA provider dependencies resolve"
else
  echo "no NVIDIA driver; CUDA skipped"
fi

section "WebGPU session"
dotnet exec "$TESTS" \
  --filter-class AiRaccoon.Tests.Integration.Embedding.BundledEngineGpuSessionTests 2>&1 | tee /tmp/probe-webgpu.log | tail -25
WEBGPU_RESULT="$(grep -E '^(Test run summary|  total|  failed|  succeeded|  skipped)' /tmp/probe-webgpu.log | tr '\n' ' ')"
WEBGPU_REASON="$(grep -o 'CPU (GPU refused: [^"]*' /tmp/probe-webgpu.log | head -1)"

section "CUDA session"
CUDA_RESULT="skipped (no NVIDIA driver)"
if [ -n "$CUDA_LIB" ]; then
  AIRACCOON_TEST_CUDA_LIBRARY="$CUDA_LIB" dotnet exec "$TESTS" \
    --filter-class AiRaccoon.Tests.Integration.Embedding.BundledEngineCudaSessionTests 2>&1 | tee /tmp/probe-cuda.log | tail -25
  CUDA_RESULT="$(grep -E '^(Test run summary|  total|  failed|  succeeded|  skipped)' /tmp/probe-cuda.log | tr '\n' ' ')"
fi

section "PROBE SUMMARY"
echo "host:    $(uname -m), $(nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>/dev/null | head -1 || echo 'no NVIDIA GPU')"
echo "vulkan:  $(echo "$VULKAN_SUMMARY" | grep -m1 -E 'deviceName' | sed 's/^ *//' || echo 'no device')"
echo "webgpu:  $WEBGPU_RESULT ${WEBGPU_REASON:+reason: $WEBGPU_REASON}"
echo "cuda:    $CUDA_RESULT"
echo "commit:  $(git log --format='%h %s' -1)"
