"""device_benchmark.result: JSON schema v1 and the markdown summary (stdlib only)."""

from __future__ import annotations

import pytest

from device_benchmark import result


def _run(device: str, repeat: int, wall: float, sys_net: float | None, soc_net: float | None, status: str = "ok",
         cold: bool = False) -> dict:
    run = {key: None for key in result.RUN_KEYS}
    run.update(device=device, repeat=repeat, slot=repeat, cold=cold, status=status, chunks=100, wall_s=wall,
               server_cpu_s=wall * 2, system_energy_net_j=sys_net, soc_energy_net_j=soc_net,
               mj_per_chunk_system_net=None if sys_net is None else sys_net * 10,
               mj_per_chunk_soc_net=None if soc_net is None else soc_net * 10,
               phys_footprint_peak_kib=512 * 1024, neural_footprint_peak_kib=0)
    return run


def _coreml(repeat: int, wall: float, sys_net: float, soc_net: float, ready_s: float, compiler_s: float,
            cold: bool = False) -> dict:
    run = _run("coreml", repeat, wall, sys_net, soc_net, cold=cold)
    run.update(neural_engine_ready_s=ready_s, ready_compiler_cpu_s=compiler_s)
    return run


RUNS = [
    _coreml(0, 90.0, 400.0, 200.0, 36.1, 29.5, cold=True),
    _run("auto", 0, 30.0, 300.0, 150.0), _coreml(0, 40.0, 200.0, 90.0, 0.7, 0.1),
    _coreml(1, 42.0, 210.0, 95.0, 34.6, 29.4), _run("auto", 1, 31.0, 320.0, 160.0),
    _run("cpu", 0, 60.0, None, None, status="fell_back"),
]


def _result() -> dict:
    return result.build_result(
        machine={"chip": "Apple M4", "model": "Mac16,12"},
        product={"version": "1.53.0", "binary_realpath": "/x/AiRaccoon", "binary_sha256": "ab"},
        corpus={"git_sha": "c0ffee", "files": 3, "bytes": 10, "sha256": "cd", "chunk_tokens": None},
        power_sources={"soc": "powermetrics", "system": "AppleSmartBattery SystemLoad accumulator"},
        compile={"cold_s": 55.0, "cold_j": 400.0, "compiler_cpu_s": 12.0, "warm_ready_s": 3.0},
        runs=RUNS, reference="auto")


def test_build_result_carries_schema_version_1_and_every_section() -> None:
    doc = _result()

    assert doc["schemaVersion"] == 1
    assert set(result.TOP_KEYS) <= set(doc)


def test_build_result_refuses_a_run_missing_a_required_key() -> None:
    broken = dict(RUNS[1])
    del broken["soc_energy_net_j"]

    with pytest.raises(ValueError, match="soc_energy_net_j"):
        result.build_result(machine={}, product={}, corpus={}, power_sources={}, compile={}, runs=[broken], reference="auto")


def test_summary_uses_only_ok_warm_runs() -> None:
    summary = _result()["summary"]["devices"]

    assert summary["coreml"]["wall_s"] == {"median": 41.0, "min": 40.0, "max": 42.0, "n": 2}
    assert summary["cpu"]["wall_s"]["n"] == 0
    assert summary["cpu"]["statuses"] == {"fell_back": 1}


def test_verdict_calls_separation_only_when_the_ranges_do_not_overlap() -> None:
    verdicts = _result()["summary"]["verdicts"]

    assert verdicts["coreml"]["system_energy_net_j"] == "lower"
    assert verdicts["coreml"]["wall_s"] == "higher"
    assert verdicts["cpu"]["system_energy_net_j"] == "no data"


def test_verdict_is_overlap_when_the_medians_differ_but_the_ranges_meet() -> None:
    runs = [_run("auto", 0, 30.0, 300.0, 1.0), _run("auto", 1, 30.0, 320.0, 1.0),
            _run("mlx", 0, 30.0, 250.0, 1.0), _run("mlx", 1, 30.0, 350.0, 1.0)]

    assert result.summarize(runs, "auto")["verdicts"]["mlx"]["system_energy_net_j"] == "overlap"


def test_markdown_snapshot() -> None:
    assert result.render_markdown(_result()) == EXPECTED_MARKDOWN


EXPECTED_MARKDOWN = """\
# Device benchmark: Apple M4 (Mac16,12)

AiRaccoon 1.53.0 · corpus c0ffee (3 files) · 100 chunks · SoC: powermetrics · system: AppleSmartBattery SystemLoad accumulator

| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s | peak phys MiB | peak neural MiB |
|---|---|---|---|---|---|---|---|---|---|
| auto | ok×2 | 30.5 | 310.0 | 155.0 | 3100.0 | 1550.0 | 61.0 | 512 | 0 |
| coreml | ok×2 | 41.0 | 205.0 | 92.5 | 2050.0 | 925.0 | 82.0 | 512 | 0 |
| cpu | fell_back×1 | – | – | – | – | – | – | – | – |

Cold coreml compile: 55.0 s, 400.0 J system, 12.0 compiler CPU-s; warm load 3.0 s.

coreml starts: slot 0 cold 36.1 s, 29.5 compiler CPU-s; slot 0 warm (cache hit) 0.7 s, 0.1 compiler CPU-s; slot 1 warm (recompiled) 34.6 s, 29.4 compiler CPU-s

- coreml vs auto: net system energy separated? yes (coreml lower); net SoC energy separated? yes (coreml lower); wall time separated? yes (coreml higher)
- cpu vs auto: net system energy separated? no data; net SoC energy separated? no data; wall time separated? no data
"""


# ---------------------------------------------------------------------------------------------
# energy_fields: one run's SoC and system numbers from its windows


def _segment(start: float, end: float, watts: float):
    from device_benchmark.battery import Segment

    return Segment(start, end, watts * 1000, watts * 1000, 0)


def test_system_net_subtracts_idle_over_the_whole_covered_span_not_just_the_window() -> None:
    # idle 5 W over [0, 60); the work runs [70, 100) inside the publish span [60, 120) that averaged 15 W.
    segments = [_segment(0, 60, 5.0), _segment(60, 120, 15.0)]

    fields = result.energy_fields(t0=70.0, t1=100.0, idle_start=0.0, idle_end=60.0, chunks=100,
                                  soc_samples=None, segments=segments)

    assert fields["system_energy_gross_j"] == pytest.approx(900.0)
    assert fields["system_energy_net_j"] == pytest.approx(900.0 - 5.0 * 60)
    assert fields["system_span_s"] == pytest.approx(60.0)
    assert fields["system_mean_w_net"] == pytest.approx(600.0 / 30.0)
    assert fields["mj_per_chunk_system_net"] == pytest.approx(6000.0)
    assert fields["soc_energy_gross_j"] is None


def test_energy_fields_leave_system_empty_without_battery_telemetry() -> None:
    fields = result.energy_fields(t0=0.0, t1=10.0, idle_start=-20.0, idle_end=0.0, chunks=10,
                                  soc_samples=None, segments=None)

    assert fields["system_energy_net_j"] is None
    assert fields["system_below_idle"] is False
    assert fields["mj_per_chunk_system_net"] is None
