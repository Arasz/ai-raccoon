"""device_benchmark.protocol: run order, provider/status decisions and the session loop (stdlib only)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from device_benchmark import protocol

SESSION_428 = (
    "info: AiRaccoon.Infrastructure.Embedding.EmbeddingService[428]\n"
    "      Embedding session created: intra-op threads 5 (halved-core default), execution provider {provider}\n"
)


# ---------------------------------------------------------------------------------------------
# EventId 428: the provider the session actually got


def test_provider_from_log_takes_the_last_session_line() -> None:
    log = SESSION_428.format(provider="CPU") + "noise\n" + SESSION_428.format(provider="WebGPU")

    assert protocol.provider_from_log(log) == "WebGPU"


def test_provider_from_log_keeps_a_refusal_suffix() -> None:
    log = SESSION_428.format(provider="WebGPU (MLX refused: plugin missing)")

    assert protocol.provider_from_log(log) == "WebGPU (MLX refused: plugin missing)"


def test_provider_from_log_is_none_without_a_session_line() -> None:
    assert protocol.provider_from_log("info: started\n") is None


@pytest.mark.parametrize(("device", "provider", "status"), [
    ("auto", "WebGPU", "ok"),
    ("cpu", "CPU", "ok"),
    ("mlx", "MLX", "ok"),
    ("mlx", "WebGPU (MLX refused: plugin missing)", "fell_back"),
    ("auto", "CPU (GPU refused: no adapter)", "fell_back"),
    ("cpu", None, "fell_back"),
])
def test_provider_status_is_ok_only_for_the_expected_provider(device: str, provider: str | None, status: str) -> None:
    assert protocol.provider_status(device, provider) == status


# ---------------------------------------------------------------------------------------------
# status.json: the Neural Engine switch


class _Clock:
    def __init__(self) -> None:
        self.now = 0.0

    def __call__(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds


def _status(path: Path, state: str, pid: int, reason: str = "r") -> None:
    path.write_text(json.dumps({"state": state, "trigger": "t", "reason": reason, "at": "2026-09-26T00:00:00Z", "pid": pid}))


def _wait(path: Path, pid: int, timeout: float = 10.0, on_poll=None) -> protocol.NeuralEngineWait:
    clock = _Clock()

    def sleep(seconds: float) -> None:
        clock.sleep(seconds)
        if on_poll is not None:
            on_poll(clock.now)

    return protocol.wait_for_neural_engine(path, pid, timeout=timeout, clock=clock, sleep=sleep, poll=0.5)


def test_wait_returns_serving_once_this_server_reaches_neural_engine_serving(tmp_path: Path) -> None:
    status = tmp_path / "status.json"
    _status(status, "CompilingNeuralEngine", 42)

    result = _wait(status, 42, on_poll=lambda now: now >= 2.0 and _status(status, "NeuralEngineServing", 42))

    assert result.state == "serving"
    assert result.seconds == pytest.approx(2.0)


def test_wait_ignores_a_status_left_by_another_process(tmp_path: Path) -> None:
    # A warm cache moved in carries the previous server's NeuralEngineServing.
    status = tmp_path / "status.json"
    _status(status, "NeuralEngineServing", 7)

    assert _wait(status, 42, timeout=3.0).state == "timeout"


def test_wait_returns_refused_with_the_reason(tmp_path: Path) -> None:
    status = tmp_path / "status.json"
    _status(status, "Refused", 42, reason="probe cosine 0.9 is below 0.999")

    result = _wait(status, 42)

    assert result.state == "refused"
    assert result.reason == "probe cosine 0.9 is below 0.999"


def test_wait_keeps_polling_through_a_half_written_file(tmp_path: Path) -> None:
    status = tmp_path / "status.json"
    status.write_text('{"state": "Neural')

    result = _wait(status, 42, on_poll=lambda now: now >= 1.0 and _status(status, "NeuralEngineServing", 42))

    assert result.state == "serving"


def test_wait_times_out_when_nothing_settles(tmp_path: Path) -> None:
    result = _wait(tmp_path / "status.json", 42, timeout=3.0)

    assert result.state == "timeout"


def test_neural_engine_log_tells_a_cache_load_from_a_compile() -> None:
    log = "info: X[437]\n      Embedding now runs on the Neural Engine: sessions loaded from cache in 4.2 s and passed the parity probe.\n"

    assert protocol.neural_engine_from_log(log) == ("loaded from cache", 4.2)
    assert protocol.neural_engine_from_log(log.replace("loaded from cache", "compiled")) == ("compiled", 4.2)
    assert protocol.neural_engine_from_log("") is None


# ---------------------------------------------------------------------------------------------
# Schedule


def test_schedule_runs_one_cold_coreml_first_then_rotates_every_repeat() -> None:
    slots = protocol.schedule(["auto", "cpu", "coreml"], repeats=2)

    assert [(s.device, s.repeat, s.cold) for s in slots] == [
        ("coreml", 0, True),
        ("auto", 0, False), ("cpu", 0, False), ("coreml", 0, False),
        ("cpu", 1, False), ("coreml", 1, False), ("auto", 1, False),
    ]
    assert [s.index for s in slots] == list(range(7))


def test_schedule_has_no_cold_run_without_coreml() -> None:
    assert not any(s.cold for s in protocol.schedule(["auto", "cpu"], repeats=1))


# ---------------------------------------------------------------------------------------------
# Session loop


def test_a_refused_or_failing_run_does_not_stop_the_session() -> None:
    slots = protocol.schedule(["auto", "coreml", "cpu"], repeats=1)

    def run_one(slot: protocol.Slot) -> dict:
        if slot.device == "coreml":
            return {"status": "refused", "reason": "graph missing", "chunks": None}
        if slot.device == "cpu":
            raise RuntimeError("server exited early")
        return {"status": "ok", "chunks": 10}

    runs = protocol.run_session(slots, run_one)

    assert [(r["device"], r["status"]) for r in runs] == [
        ("coreml", "refused"), ("auto", "ok"), ("coreml", "refused"), ("cpu", "error")]
    assert runs[3]["reason"] == "server exited early"
    assert runs[0]["slot"] == 0 and runs[0]["cold"] is True


def test_chunk_mismatch_marks_the_odd_run_invalid() -> None:
    runs = [{"status": "ok", "chunks": 120}, {"status": "fell_back", "chunks": 120},
            {"status": "ok", "chunks": 119}, {"status": "refused", "chunks": None}]

    reference = protocol.mark_chunk_mismatch(runs)

    assert reference == 120
    assert [r["status"] for r in runs] == ["ok", "fell_back", "invalid", "refused"]
    assert "119" in runs[2]["reason"]


def test_coreml_refusal_reads_a_plan_time_refusal_off_the_session_line() -> None:
    # A plan-time refusal never builds the switch, so no status.json is written: the 428 line is the only record.
    assert protocol.coreml_refusal("WebGPU (CoreML refused: graph missing)") == "graph missing"
    assert protocol.coreml_refusal("WebGPU (CoreML compiling)") is None
    assert protocol.coreml_refusal(None) is None


def test_the_slot_identity_wins_over_blank_keys_in_a_run_record() -> None:
    # A runner that starts from a template of every run key must not blank out the slot's device.
    runs = protocol.run_session(protocol.schedule(["cpu"], repeats=1),
                                lambda slot: {"device": None, "repeat": None, "slot": None, "cold": None, "status": "ok"})

    assert (runs[0]["device"], runs[0]["repeat"], runs[0]["slot"], runs[0]["cold"]) == ("cpu", 0, 0, False)
