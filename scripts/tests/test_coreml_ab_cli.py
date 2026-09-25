"""coreml_ab.py CLI-level fixes: needs numpy/onnxruntime transitively (it imports chunk_window),
so not in the stdlib-only CI harness list, same as test_coreml_engine.py.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

CLI_PATH = Path(__file__).resolve().parent.parent / "retrieval_tuning" / "coreml_ab.py"


def _load():
    spec = importlib.util.spec_from_file_location("coreml_ab_cli", CLI_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


coreml_ab = _load()


# ---------------------------------------------------------------------------------------------
# N10: competing_processes excludes the orchestrator by pid, not by name


def test_count_processes_excludes_the_given_pid_even_when_its_name_matches() -> None:
    text = "  100 python3\n  200 python3\n  300 dotnet\n"

    counts = coreml_ab._count_processes(text, coreml_ab.COMPETING_NAMES, exclude_pid=200)

    assert counts["python3"] == 1
    assert counts["dotnet"] == 1


def test_count_processes_does_not_clamp_when_self_resolves_to_a_different_name() -> None:
    # The orchestrator's own comm can resolve to "python", not "python3" (symlink-dependent); a
    # by-name exclusion would silently clamp the wrong bucket instead of excluding self at all.
    text = "  100 python\n  200 python3\n"

    counts = coreml_ab._count_processes(text, coreml_ab.COMPETING_NAMES, exclude_pid=999)

    assert counts["python3"] == 1


# ---------------------------------------------------------------------------------------------
# N9: coreml_ab validates --coreml-cache-dir up front for any coreml config


def test_require_coreml_cache_dir_raises_when_a_coreml_config_has_no_cache_dir() -> None:
    configs = [{"name": "c", "device": "coreml"}]

    with pytest.raises(SystemExit):
        coreml_ab._require_coreml_cache_dir(configs, None)


def test_require_coreml_cache_dir_allows_non_coreml_configs_without_one() -> None:
    configs = [{"name": "c", "device": "mlx"}]

    coreml_ab._require_coreml_cache_dir(configs, None)  # must not raise


# ---------------------------------------------------------------------------------------------
# Per-config model_dir: one A/B can compare a re-exported graph against the shipped one


def test_config_model_dir_uses_the_configs_own_dir_when_set() -> None:
    config = {"name": "c", "device": "coreml", "model_dir": "/tmp/ane-fp16"}

    assert coreml_ab.config_model_dir(config, Path("/tmp/bundled")) == Path("/tmp/ane-fp16")


def test_config_model_dir_falls_back_to_the_cli_model_dir() -> None:
    config = {"name": "c", "device": "mlx"}

    assert coreml_ab.config_model_dir(config, Path("/tmp/bundled")) == Path("/tmp/bundled")
