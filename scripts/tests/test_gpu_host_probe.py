"""Tests for the pure parsing helpers in scripts/gpu-host-probe.py."""

import importlib.util
from pathlib import Path

PROBE_PATH = Path(__file__).resolve().parent.parent / "gpu-host-probe.py"


def _load():
    spec = importlib.util.spec_from_file_location("gpu_host_probe", PROBE_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


probe = _load()

TEST_OUTPUT = """\
skipped AiRaccoon.Tests.Integration.Embedding.BundledEngineGpuSessionTests.PreferGpu_OnMacOs (1ms)
  the standard ORT build implements WebGPU only on macOS
Test run summary: Failed! - /w/AiRaccoon.Tests.dll (net10.0|x64)
  total: 4
  failed: 1
  succeeded: 2
  skipped: 1
  duration: 2s 517ms
"""


def test_test_summary_joins_the_verdict_and_counts():
    assert probe.test_summary(TEST_OUTPUT) == "Failed! total 4, failed 1, succeeded 2, skipped 1"


def test_test_summary_without_a_run_says_so():
    assert probe.test_summary("Unhandled exception: boom") == "no test run summary"


def test_refusal_reason_takes_the_first_cpu_fallback():
    output = 'Actual Value: "CPU (GPU refused: no WebGPU adapter)" and "CPU (GPU refused: other)"'
    assert probe.refusal_reason(output) == "CPU (GPU refused: no WebGPU adapter)"


def test_refusal_reason_is_none_when_the_session_landed_on_a_gpu():
    assert probe.refusal_reason(TEST_OUTPUT) is None


def test_vulkan_devices_lists_each_device_name_and_type():
    summary = """\
Devices:
========
GPU0:
\tdeviceType         = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
\tdeviceName         = Tesla T4
GPU1:
\tdeviceType         = PHYSICAL_DEVICE_TYPE_CPU
\tdeviceName         = llvmpipe (LLVM 20.1.2, 256 bits)
"""
    assert probe.vulkan_devices(summary) == [
        "Tesla T4 (DISCRETE_GPU)",
        "llvmpipe (LLVM 20.1.2, 256 bits) (CPU)",
    ]


def test_vulkan_devices_is_empty_without_a_driver():
    assert probe.vulkan_devices("ERROR: vkCreateInstance failed: Found no drivers!") == []


def test_library_dirs_finds_each_soname_once(tmp_path):
    (tmp_path / "nvidia" / "cu13" / "lib").mkdir(parents=True)
    (tmp_path / "nvidia" / "cu13" / "lib" / "libcudart.so.13").write_bytes(b"")
    (tmp_path / "nvidia" / "cu13" / "lib" / "libcublas.so.13").write_bytes(b"")
    (tmp_path / "nvidia" / "cudnn" / "lib").mkdir(parents=True)
    (tmp_path / "nvidia" / "cudnn" / "lib" / "libcudnn.so.9").write_bytes(b"")

    dirs = probe.library_dirs(tmp_path, ["libcudart.so.13", "libcublas.so.13", "libcudnn.so.9", "libcurand.so.10"])

    assert dirs == [tmp_path / "nvidia" / "cu13" / "lib", tmp_path / "nvidia" / "cudnn" / "lib"]


def test_nvidia_icd_manifest_names_the_library_and_parses_as_json():
    import json

    manifest = json.loads(probe.nvidia_icd_manifest())
    assert manifest["ICD"]["library_path"] == "libGLX_nvidia.so.0"
    assert manifest["file_format_version"] == "1.0.1"
