#!/usr/bin/env python3
"""Probes a Linux GPU host (Kaggle, Colab, Lightning AI, any NVIDIA VM) for the bundled engine's GPU
paths: WebGPU through the bundled core (needs Vulkan) and opt-in CUDA (needs CUDA 13 libraries).

    curl -fsSL https://raw.githubusercontent.com/Arasz/ai-raccoon/main/scripts/gpu-host-probe.py | python3 -

Standard library only, so it runs before the repository is cloned. Prints a PROBE SUMMARY block at
the end; paste it into the release checklist. AIRACCOON_REPO_URL, AIRACCOON_REF and
AIRACCOON_PROBE_DIR override the clone source, branch and working directory.
"""

import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path

ORT_VERSION = "1.30.0"
# SHA-256 of Microsoft.ML.OnnxRuntime.Gpu.Linux 1.30.0 from nuget.org (its SHA-512 matches the catalog).
ORT_GPU_NUPKG_SHA256 = "77ca23682fc164789e67cd0fa4ce022b1b1c94e1978046c6446eee5fb0a3a3d6"
CUDA_WHEELS = ("nvidia-cuda-runtime==13.*", "nvidia-cublas==13.*", "nvidia-curand==10.*", "nvidia-cudnn-cu13==9.*")
CUDA_SONAMES = ("libcudart.so.13", "libcublas.so.13", "libcublasLt.so.13", "libcurand.so.10", "libcudnn.so.9")
TESTS_DLL = "tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll"
GPU_TESTS = "AiRaccoon.Tests.Integration.Embedding.BundledEngineGpuSessionTests"
CUDA_TESTS = "AiRaccoon.Tests.Integration.Embedding.BundledEngineCudaSessionTests"


def test_summary(output: str) -> str:
    """One line from a Microsoft Testing Platform run: the verdict and the counts."""
    verdict = re.search(r"Test run summary: (\w+!)", output)
    if verdict is None:
        return "no test run summary"
    counts = re.findall(r"^\s+(total|failed|succeeded|skipped): (\d+)", output, re.MULTILINE)
    return "%s %s" % (verdict.group(1), ", ".join("%s %s" % pair for pair in counts))


def refusal_reason(output: str) -> str | None:
    """The first `CPU (GPU refused: …)` execution provider in the output, or None when none fell back."""
    match = re.search(r'CPU \(GPU refused: [^"\n]*?\)(?=["\s]|$)', output)
    return match.group(0) if match else None


def vulkan_devices(summary: str) -> list[str]:
    """Each device `vulkaninfo --summary` lists, as `name (type)`; empty when no driver loaded."""
    types = re.findall(r"deviceType\s*=\s*PHYSICAL_DEVICE_TYPE_(\w+)", summary)
    names = re.findall(r"deviceName\s*=\s*(.+)", summary)
    return ["%s (%s)" % (name.strip(), kind) for name, kind in zip(names, types)]


def sha256_of(path: Path) -> str:
    """The file's SHA-256 as lowercase hex."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def library_dirs(root: Path, sonames: list[str] | tuple[str, ...]) -> list[Path]:
    """The directories under root holding each soname, first match per name, in first-seen order."""
    found: list[Path] = []
    for soname in sonames:
        match = next(iter(sorted(root.rglob(soname + "*"))), None)
        if match is not None and match.parent not in found:
            found.append(match.parent)
    return found


@dataclass
class Probe:
    """What the run found, for the closing summary."""

    host: str = ""
    vulkan: str = "not checked"
    webgpu: str = "not run"
    cuda: str = "skipped (no NVIDIA driver)"
    commit: str = ""


def section(title: str) -> None:
    print("\n==== %s ====" % title, flush=True)


def run(command: list[str], env: dict[str, str] | None = None, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    """Runs a command, capturing stdout and stderr together; never raises on a non-zero exit."""
    return subprocess.run(command, env=env, cwd=cwd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)


def tail(text: str, lines: int) -> str:
    return "\n".join(text.rstrip().splitlines()[-lines:])


def nvidia_gpu() -> str | None:
    """`name, driver` of the first NVIDIA GPU, or None without a working driver."""
    if shutil.which("nvidia-smi") is None:
        return None
    result = run(["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"])
    return result.stdout.strip().splitlines()[0] if result.returncode == 0 and result.stdout.strip() else None


def probe_host(found: Probe) -> None:
    section("host")
    os_release = Path("/etc/os-release")
    pretty = re.search(r'^PRETTY_NAME="?([^"\n]+)', os_release.read_text(), re.MULTILINE) if os_release.exists() else None
    print("%s %s, %s" % (platform.system(), platform.machine(), pretty.group(1) if pretty else "unknown distro"))
    gpu = nvidia_gpu()
    print("nvidia-smi: %s" % (gpu or "none"))
    if gpu is not None:
        cuda = re.search(r"CUDA Version: ([0-9.]+)", run(["nvidia-smi"]).stdout)
        print("driver CUDA version: %s" % (cuda.group(1) if cuda else "unknown"))
    found.host = "%s, %s" % (platform.machine(), gpu or "no NVIDIA GPU")


NVIDIA_VULKAN_LIBRARY = "libGLX_nvidia.so.0"


def nvidia_icd_manifest(library: str = NVIDIA_VULKAN_LIBRARY) -> str:
    """A Vulkan ICD manifest for NVIDIA's driver, for containers that mount the library but not its JSON."""
    return json.dumps({"file_format_version": "1.0.1", "ICD": {"library_path": library, "api_version": "1.4.312"}}) + "\n"


def probe_vulkan(found: Probe, env: dict[str, str]) -> None:
    section("vulkan")
    if shutil.which("apt-get") is not None:
        sudo = ["sudo"] if os.geteuid() != 0 and shutil.which("sudo") else []
        run(sudo + ["apt-get", "update", "-qq"])
        if run(sudo + ["apt-get", "install", "-y", "-qq", "libvulkan1", "vulkan-tools"]).returncode != 0:
            print("apt install of vulkan-tools failed")
    for icd_dir in (Path("/usr/share/vulkan/icd.d"), Path("/etc/vulkan/icd.d")):
        if icd_dir.is_dir():
            print("%s: %s" % (icd_dir, ", ".join(sorted(p.name for p in icd_dir.iterdir())) or "empty"))
    if shutil.which("vulkaninfo") is None:
        found.vulkan = "vulkaninfo not available"
        print(found.vulkan)
        return
    summary = run(["vulkaninfo", "--summary"], env=env).stdout
    devices = vulkan_devices(summary)
    if not devices and NVIDIA_VULKAN_LIBRARY in run(["ldconfig", "-p"]).stdout:
        # NVIDIA containers often mount the Vulkan driver without its ICD manifest; supply one.
        manifest = Path(tempfile.mkdtemp(prefix="airaccoon-vulkan-")) / "nvidia_icd.json"
        manifest.write_text(nvidia_icd_manifest())
        env["VK_DRIVER_FILES"] = str(manifest)
        print("no Vulkan device; %s is present, retrying with VK_DRIVER_FILES=%s" % (NVIDIA_VULKAN_LIBRARY, manifest))
        summary = run(["vulkaninfo", "--summary"], env=env).stdout
        devices = vulkan_devices(summary)
    found.vulkan = "; ".join(devices) if devices else "no device (%s)" % tail(summary, 1)
    if "VK_DRIVER_FILES" in env and devices:
        found.vulkan += " (via a supplied ICD manifest)"
    print(found.vulkan)


def ensure_dotnet(env: dict[str, str]) -> None:
    section("dotnet")
    sdks = run(["dotnet", "--list-sdks"], env=env).stdout if shutil.which("dotnet", path=env["PATH"]) else ""
    if not re.search(r"^10\.", sdks, re.MULTILINE):
        installer = Path("/tmp/dotnet-install.sh")
        urllib.request.urlretrieve("https://dot.net/v1/dotnet-install.sh", installer)
        dotnet_root = Path.home() / ".dotnet"
        run(["bash", str(installer), "--channel", "10.0", "--install-dir", str(dotnet_root)])
        env["DOTNET_ROOT"] = str(dotnet_root)
        env["PATH"] = "%s:%s" % (dotnet_root, env["PATH"])
    print(run(["dotnet", "--version"], env=env).stdout.strip())


def prepare_source(work: Path, env: dict[str, str], found: Probe) -> bool:
    """Clones and builds the tests; False, with the failure in found.commit, when either step fails."""
    section("source")
    if not (work / ".git").is_dir():
        repo = env.get("AIRACCOON_REPO_URL", "https://github.com/Arasz/ai-raccoon.git")
        ref = env.get("AIRACCOON_REF", "main")
        clone = run(["git", "clone", "-q", "--depth", "1", "--branch", ref, repo, str(work)])
        if clone.returncode != 0:
            found.commit = "clone failed: %s" % tail(clone.stdout, 1)
            print(clone.stdout.rstrip())
            return False
    found.commit = run(["git", "log", "--format=%h %s", "-1"], cwd=work).stdout.strip()
    print(found.commit)
    for script in ("download-embedding-model.py", "download-webgpu-core.py"):
        print(tail(run([sys.executable, "scripts/" + script], cwd=work).stdout, 1))
    (work / ".nupkg-local").mkdir(exist_ok=True)
    build = run(["dotnet", "build", "tests/AiRaccoon.Tests", "--nologo", "-v", "q"], env=env, cwd=work)
    print(tail(build.stdout, 2))
    if build.returncode != 0:
        found.commit += " (build failed)"
        return False
    return True


def prepare_cuda(work: Path, env: dict[str, str]) -> Path | None:
    """The ORT CUDA provider with its CUDA 13 wheels on LD_LIBRARY_PATH, or None without an NVIDIA driver."""
    section("cuda provider")
    if nvidia_gpu() is None:
        print("no NVIDIA driver; CUDA skipped")
        return None
    wheels = work / ".cuda-wheels"
    print(tail(run([sys.executable, "-m", "pip", "install", "-q", "--target", str(wheels), *CUDA_WHEELS]).stdout, 1))
    dirs = library_dirs(wheels, CUDA_SONAMES)
    env["LD_LIBRARY_PATH"] = ":".join([*map(str, dirs), env.get("LD_LIBRARY_PATH", "")]).rstrip(":")

    nupkg = work / ".ort-gpu.nupkg"
    url = "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.Gpu.Linux/%s" % ORT_VERSION
    urllib.request.urlretrieve(url, nupkg)
    actual = sha256_of(nupkg)
    if actual != ORT_GPU_NUPKG_SHA256:
        nupkg.unlink()
        print("%s: sha256 %s, expected %s; CUDA skipped" % (url, actual, ORT_GPU_NUPKG_SHA256))
        return None
    native = "runtimes/linux-x64/native/"
    with zipfile.ZipFile(nupkg) as archive:
        archive.extractall(work / ".ort-gpu", [n for n in archive.namelist() if n.startswith(native)])
    provider = work / ".ort-gpu" / native / "libonnxruntime_providers_cuda.so"
    missing = [line.strip() for line in run(["ldd", str(provider)], env=env).stdout.splitlines() if "not found" in line]
    print("\n".join(missing) or "all CUDA provider dependencies resolve")
    return provider


def run_tests(work: Path, env: dict[str, str], test_class: str) -> str:
    result = run(["dotnet", "exec", TESTS_DLL, "--filter-class", test_class], env=env, cwd=work)
    print(tail(result.stdout, 25))
    return result.stdout


def main() -> int:
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", TESTINGPLATFORM_TELEMETRY_OPTOUT="1")
    work = Path(env.get("AIRACCOON_PROBE_DIR", str(Path.home() / "airaccoon-probe")))
    found = Probe()

    probe_host(found)
    work.parent.mkdir(parents=True, exist_ok=True)
    probe_vulkan(found, env)
    ensure_dotnet(env)
    if prepare_source(work, env, found):
        cuda_provider = prepare_cuda(work, env)

        section("WebGPU session")
        output = run_tests(work, env, GPU_TESTS)
        reason = refusal_reason(output)
        found.webgpu = test_summary(output) + (", reason: %s" % reason if reason else "")

        if cuda_provider is not None:
            section("CUDA session")
            found.cuda = test_summary(run_tests(work, dict(env, AIRACCOON_TEST_CUDA_LIBRARY=str(cuda_provider)), CUDA_TESTS))
    else:
        found.cuda = "not run"

    section("PROBE SUMMARY")
    print("host:    %s" % found.host)
    print("vulkan:  %s" % found.vulkan)
    print("webgpu:  %s" % found.webgpu)
    print("cuda:    %s" % found.cuda)
    print("commit:  %s" % found.commit)
    return 0


if __name__ == "__main__":
    sys.exit(main())
