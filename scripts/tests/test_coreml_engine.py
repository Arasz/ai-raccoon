"""Engine-level CoreML EP fixes that need numpy/onnxruntime importable: not in the stdlib-only CI
harness list (build.yml's scripts-harness job installs only pytest/httpx), run locally instead,
same as the llamaindex/p3_cli_parity heavy suites documented in that job's own comment.
"""

from __future__ import annotations

import os
import time

import numpy as np
import pytest

from retrieval_tuning.chunk_window import Engine, _capture_stderr


class _FakeEncoding:
    def __init__(self, ids: list[int]) -> None:
        self.ids = ids


class _FakeTokenizer:
    def encode(self, text: str, add_special_tokens: bool = True) -> _FakeEncoding:
        return _FakeEncoding([1, 2, 3])


class _FakeSession:
    def run(self, output_names: list[str], feed: dict[str, object]) -> list[np.ndarray]:
        return [np.zeros((1, 8), dtype=np.float32)]


class _SlowRebuildSessions:
    """Stands in for BucketSessions: the first get() pays a rebuild cost, like a cold session build."""

    def __init__(self, rebuild_s: float) -> None:
        self._rebuild_s = rebuild_s

    def get(self, bucket: int) -> _FakeSession:
        time.sleep(self._rebuild_s)
        return _FakeSession()


def _bare_coreml_engine(sessions: object) -> Engine:
    engine = object.__new__(Engine)  # skip __init__: no real model files needed for this test
    engine.tokenizer = _FakeTokenizer()
    engine.window = 128
    engine.device = "coreml"
    engine.buckets = (64,)
    engine.pad_to = 0
    engine.sessions = sessions
    engine.bucket_first_run_ms = {}
    engine.padded_tokens = 0
    engine.seen_lengths = set()
    return engine


# ---------------------------------------------------------------------------------------------
# MUST2: embed()'s wall/CPU timer must include a session (re)build, not just session.run()


def test_embed_wall_ms_includes_a_session_rebuild_triggered_by_this_row() -> None:
    engine = _bare_coreml_engine(_SlowRebuildSessions(rebuild_s=0.05))

    _, _, wall_ms, _ = engine.embed("hello")

    assert wall_ms >= 45, "the 50ms session rebuild must fall inside the timed window, not before it"


# ---------------------------------------------------------------------------------------------
# S7: a CoreML log that doesn't match the expected partition-line format is recorded, not swallowed


def test_read_coreml_log_records_the_parse_error_instead_of_swallowing_it() -> None:
    engine = object.__new__(Engine)
    engine.coreml_nodes = None
    engine.coreml_partitions = None
    engine.compute_plan = {}
    engine.coreml_log_parse_error = None
    engine._profile_compute_plan = False

    engine._read_coreml_log("nothing useful in this log\n")

    assert engine.coreml_log_parse_error is not None
    assert engine.coreml_nodes is None


# ---------------------------------------------------------------------------------------------
# N12: _capture_stderr re-emits the captured text and cleans up its temp file on an exception


def test_capture_stderr_reemits_captured_text_on_exception(capfd: pytest.CaptureFixture[str]) -> None:
    def build() -> None:
        os.write(2, b"partition info that would otherwise be lost\n")
        raise RuntimeError("boom")

    with pytest.raises(RuntimeError):
        _capture_stderr(build)

    assert "partition info that would otherwise be lost" in capfd.readouterr().err


def test_capture_stderr_cleans_up_its_temp_file_on_exception() -> None:
    import tempfile as tempfile_module
    from pathlib import Path

    seen_paths: list[str] = []
    real_mkstemp = tempfile_module.mkstemp

    def spying_mkstemp(*args: object, **kwargs: object) -> tuple[int, str]:
        fd, path = real_mkstemp(*args, **kwargs)
        seen_paths.append(path)
        return fd, path

    tempfile_module.mkstemp = spying_mkstemp
    try:
        with pytest.raises(RuntimeError):
            _capture_stderr(lambda: (_ for _ in ()).throw(RuntimeError("boom")))
    finally:
        tempfile_module.mkstemp = real_mkstemp

    assert seen_paths and not Path(seen_paths[0]).exists()


def test_capture_stderr_still_returns_the_captured_text_on_success() -> None:
    def build() -> str:
        os.write(2, b"ok\n")
        return "built"

    result, text = _capture_stderr(build)

    assert result == "built"
    assert "ok" in text
