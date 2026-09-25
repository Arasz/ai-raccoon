"""ane_powermetrics.py pure helpers: powermetrics plist parsing and per-phase power summary (stdlib only)."""

from __future__ import annotations

import importlib.util
import plistlib
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

CLI_PATH = Path(__file__).resolve().parent.parent / "retrieval_tuning" / "ane_powermetrics.py"


def _load():
    spec = importlib.util.spec_from_file_location("ane_powermetrics", CLI_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


pm = _load()

T0 = datetime(2026, 9, 25, 20, 0, 0, tzinfo=timezone.utc)


def _sample(seconds: float, ane_mw: float, cpu_mw: float) -> bytes:
    return plistlib.dumps({
        "timestamp": T0 + timedelta(seconds=seconds),
        "processor": {"ane_power": ane_mw, "cpu_power": cpu_mw, "clusters": [{"name": "E0"}]},
        "gpu": {"gpu_energy": 5},
    })


def _stream(*samples: bytes) -> bytes:
    # powermetrics --format plist separates samples with a NUL byte, trailing one included
    return b"\0".join(samples) + b"\0"


def test_split_samples_parses_every_nul_separated_plist() -> None:
    samples = pm.split_samples(_stream(_sample(0, 1, 2), _sample(1, 3, 4)))

    assert [s["processor"]["ane_power"] for s in samples] == [1, 3]


def test_split_samples_skips_a_truncated_last_sample() -> None:
    # a powermetrics run cut short leaves a half-written plist at the end
    data = _stream(_sample(0, 1, 2)) + _sample(1, 3, 4)[:40]

    assert len(pm.split_samples(data)) == 1


def test_power_fields_collects_numeric_leaves_named_power_by_dotted_path() -> None:
    fields = pm.power_fields({"processor": {"ane_power": 7, "cpu_power": 9, "freq": 3}, "gpu": {"gpu_power": 2}})

    assert fields == {"processor.ane_power": 7, "processor.cpu_power": 9, "gpu.gpu_power": 2}


def test_phase_of_returns_the_phase_whose_window_holds_the_timestamp() -> None:
    phases = [{"name": "idle", "start": 0.0, "end": 10.0}, {"name": "ane", "start": 10.0, "end": 40.0}]

    assert pm.phase_of(5.0, phases) == "idle"
    assert pm.phase_of(10.0, phases) == "ane"
    assert pm.phase_of(45.0, phases) is None


def test_summarize_means_each_power_field_per_phase() -> None:
    samples = [pm.split_samples(_stream(s))[0] for s in (_sample(1, 0, 100), _sample(2, 10, 300), _sample(12, 800, 50))]
    epoch = T0.timestamp()
    phases = [{"name": "idle", "start": epoch, "end": epoch + 10}, {"name": "ane", "start": epoch + 10, "end": epoch + 40}]

    summary = pm.summarize(samples, phases)

    assert summary["idle"]["processor.ane_power"] == pytest.approx(5.0)
    assert summary["idle"]["processor.cpu_power"] == pytest.approx(200.0)
    assert summary["ane"]["processor.ane_power"] == pytest.approx(800.0)
    assert summary["idle"]["samples"] == 2
