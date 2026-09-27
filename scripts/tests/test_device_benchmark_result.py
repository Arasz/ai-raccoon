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


def test_build_result_carries_schema_version_2_and_every_section() -> None:
    doc = _result()

    assert doc["schemaVersion"] == 2
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

| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s | median p95 search ms | peak phys MiB | peak neural MiB |
|---|---|---|---|---|---|---|---|---|---|---|
| auto | ok×2 | 30.5 | 310.0 | 155.0 | 3100.0 | 1550.0 | 61.0 | – | 512 | 0 |
| coreml | ok×2 | 41.0 | 205.0 | 92.5 | 2050.0 | 925.0 | 82.0 | – | 512 | 0 |
| cpu | fell_back×1 | – | – | – | – | – | – | – | – | – |

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


# ---------------------------------------------------------------------------------------------
# percentile: nearest-rank


def test_percentile_nearest_rank_on_a_known_list() -> None:
    values = [1.0, 2.0, 3.0, 4.0, 5.0]

    assert result.percentile(values, 50) == 3.0
    assert result.percentile(values, 95) == 5.0


def test_percentile_of_a_single_element_is_that_element_at_any_pct() -> None:
    assert result.percentile([42.0], 50) == 42.0
    assert result.percentile([42.0], 95) == 42.0


def test_percentile_of_an_empty_sequence_is_none() -> None:
    assert result.percentile([], 50) is None
    assert result.percentile([], 95) is None


def test_percentile_ignores_input_order() -> None:
    assert result.percentile([5.0, 1.0, 3.0, 2.0, 4.0], 50) == result.percentile([1.0, 2.0, 3.0, 4.0, 5.0], 50)


# ---------------------------------------------------------------------------------------------
# search_latency_fields: one run's p50/p95 off its raw per-query latencies


def test_search_latency_fields_carries_the_raw_list_and_its_percentiles() -> None:
    fields = result.search_latency_fields([10.0, 20.0, 30.0, 40.0])

    assert fields["search_latency_ms"] == [10.0, 20.0, 30.0, 40.0]
    assert fields["search_p50_ms"] == 20.0
    assert fields["search_p95_ms"] == 40.0


def test_search_latency_fields_of_an_empty_list_leaves_percentiles_none() -> None:
    fields = result.search_latency_fields([])

    assert fields["search_latency_ms"] == []
    assert fields["search_p50_ms"] is None
    assert fields["search_p95_ms"] is None


# ---------------------------------------------------------------------------------------------
# search_latency_loses: range-separation verdict, mirroring `beats`


def test_search_latency_loses_when_every_device_p95_sits_above_every_auto_p95() -> None:
    assert result.search_latency_loses([50.0, 55.0], [10.0, 12.0]) is True


def test_search_latency_does_not_lose_when_the_ranges_overlap() -> None:
    assert result.search_latency_loses([10.0, 30.0], [15.0, 25.0]) is False


def test_search_latency_does_not_lose_when_the_device_range_sits_entirely_below_auto() -> None:
    assert result.search_latency_loses([5.0, 8.0], [20.0, 25.0]) is False


def test_search_latency_loses_is_none_without_data_on_either_side() -> None:
    assert result.search_latency_loses([], [10.0]) is None
    assert result.search_latency_loses([10.0], []) is None


# ---------------------------------------------------------------------------------------------
# Summary + markdown: the median-p95 column and the lose verdict line


def _with_search(run: dict, p95: float | None) -> dict:
    run = dict(run)
    run.update(search_latency_ms=[] if p95 is None else [p95], search_p50_ms=p95, search_p95_ms=p95)
    return run


def test_device_summary_carries_median_p95_search_ms_over_its_repeats() -> None:
    runs = [_with_search(_run("auto", 0, 30.0, 300.0, 150.0), 12.0),
            _with_search(_run("auto", 1, 31.0, 320.0, 160.0), 18.0)]

    summary = result.summarize(runs, "auto")["devices"]["auto"]["search_p95_ms"]

    assert summary == {"median": 15.0, "min": 12.0, "max": 18.0, "n": 2}


def test_summary_search_latency_verdict_loses_when_ranges_separate_higher() -> None:
    runs = [_with_search(_run("auto", 0, 30.0, 300.0, 150.0), 10.0),
            _with_search(_run("auto", 1, 31.0, 320.0, 160.0), 12.0),
            _with_search(_run("coreml", 0, 40.0, 200.0, 90.0), 50.0),
            _with_search(_run("coreml", 1, 42.0, 210.0, 95.0), 55.0)]

    verdicts = result.summarize(runs, "auto")["search_latency_verdicts"]

    assert verdicts["coreml"] is True


def test_summary_search_latency_verdict_is_none_when_the_phase_was_disabled() -> None:
    runs = [_run("auto", 0, 30.0, 300.0, 150.0), _run("coreml", 0, 40.0, 200.0, 90.0)]

    verdicts = result.summarize(runs, "auto")["search_latency_verdicts"]

    assert verdicts["coreml"] is None


def test_markdown_includes_the_search_column_and_the_lose_line_when_data_is_present() -> None:
    doc = _result()
    doc["runs"] = [_with_search(r, 10.0 if r["device"] == "auto" else 50.0) if r["device"] in ("auto", "coreml")
                   else r for r in doc["runs"]]
    doc["summary"] = result.summarize(doc["runs"], "auto")

    markdown = result.render_markdown(doc)

    assert "median p95 search ms" in markdown
    assert "coreml vs auto: " in markdown
    coreml_line = next(line for line in markdown.splitlines() if line.startswith("- coreml vs auto"))
    assert "p95 search latency: lose? yes" in coreml_line


def test_markdown_omits_the_lose_line_when_no_run_carries_search_latency() -> None:
    markdown = result.render_markdown(_result())

    assert "median p95 search ms" in markdown
    assert "p95 search latency" not in markdown
