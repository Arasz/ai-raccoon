"""CoreML A/B helpers: seeded sampling, rotated interleave, and range-separation summaries."""

from __future__ import annotations

import pytest

from retrieval_tuning.coreml import (
    beats,
    interleave,
    paired_ratios,
    seeded_sample,
    summarize,
    summarize_ab,
)


# ---------------------------------------------------------------------------------------------
# seeded_sample


def test_seeded_sample_is_reproducible_for_the_same_seed() -> None:
    rows = list(range(100))

    assert seeded_sample(rows, 10, seed=7) == seeded_sample(rows, 10, seed=7)


def test_seeded_sample_differs_across_seeds() -> None:
    rows = list(range(100))

    assert seeded_sample(rows, 10, seed=1) != seeded_sample(rows, 10, seed=2)


def test_seeded_sample_never_exceeds_the_pool() -> None:
    assert sorted(seeded_sample([1, 2, 3], 10, seed=1)) == [1, 2, 3]


# ---------------------------------------------------------------------------------------------
# interleave


def test_interleave_produces_repeats_times_configs_entries() -> None:
    schedule = interleave(["cpu", "coreml"], repeats=3)

    assert len(schedule) == 6


def test_interleave_visits_every_config_exactly_once_per_repeat() -> None:
    schedule = interleave(["cpu", "coreml", "mlx"], repeats=2)

    for repeat in range(2):
        configs_this_repeat = [config for r, config in schedule if r == repeat]
        assert sorted(configs_this_repeat) == ["coreml", "cpu", "mlx"]


def test_interleave_rotates_the_starting_config_each_repeat() -> None:
    schedule = interleave(["A", "B", "C"], repeats=3)

    first_per_repeat = [next(config for r, config in schedule if r == repeat) for repeat in range(3)]

    assert first_per_repeat == ["A", "B", "C"]


# ---------------------------------------------------------------------------------------------
# summarize / beats / paired_ratios


def test_summarize_reports_min_max_and_mean() -> None:
    summary = summarize({"cpu": [10.0, 12.0, 11.0]})

    assert summary["cpu"]["min"] == 10.0
    assert summary["cpu"]["max"] == 12.0
    assert summary["cpu"]["mean"] == 11.0


def test_beats_is_true_only_when_ranges_do_not_overlap() -> None:
    assert beats([1.0, 2.0, 3.0], [4.0, 5.0, 6.0]) is True
    assert beats([1.0, 2.0, 3.0], [3.0, 4.0, 5.0]) is False  # touching ranges: not decided
    assert beats([1.0, 5.0], [2.0, 6.0]) is False  # overlapping: not decided


def test_beats_is_strict_about_the_boundary() -> None:
    # max(a) == min(b): a tie at the boundary is not a beat.
    assert beats([1.0, 2.0], [2.0, 3.0]) is False


def test_paired_ratios_divides_by_matching_rotation_index() -> None:
    a = [10.0, 20.0, 30.0]
    b = [5.0, 10.0, 10.0]

    assert paired_ratios(a, b) == [2.0, 2.0, 3.0]


# ---------------------------------------------------------------------------------------------
# summarize_ab: N-config summary + beats/paired-ratio against a named reference


def _reps(cpu_s: list[float], p50_ms: list[float], p95_ms: list[float],
          phys_kib_peak: list[int], neural_kib_peak: list[int]) -> list[dict]:
    return [{"cpu_s": c, "p50_ms": p50, "p95_ms": p95, "phys_kib_peak": phys, "neural_kib_peak": neural}
            for c, p50, p95, phys, neural in zip(cpu_s, p50_ms, p95_ms, phys_kib_peak, neural_kib_peak)]


def _three_config_per_config() -> dict[str, list[dict]]:
    return {
        "mlx": _reps([1.0, 1.1], [10.0, 11.0], [20.0, 21.0], [1000, 1010], [0, 0]),
        "ane-t1": _reps([5.0, 5.5], [50.0, 55.0], [90.0, 95.0], [2000, 2010], [500, 510]),
        "ane-t3": _reps([6.0, 6.5], [60.0, 65.0], [100.0, 105.0], [2100, 2110], [520, 530]),
    }


def test_summarize_ab_covers_every_config_not_just_the_first_two() -> None:
    per_config = _three_config_per_config()

    result = summarize_ab(list(per_config), per_config, reference="mlx")

    assert set(result["cpu_s"]) == {"mlx", "ane-t1", "ane-t3"}
    assert set(result["p50_ms"]) == {"mlx", "ane-t1", "ane-t3"}
    assert set(result["p95_ms"]) == {"mlx", "ane-t1", "ane-t3"}
    assert set(result["footprint_kib_peak"]) == {"mlx", "ane-t1", "ane-t3"}


def test_summarize_ab_reports_beats_and_paired_ratio_for_every_non_reference_config() -> None:
    per_config = _three_config_per_config()

    result = summarize_ab(list(per_config), per_config, reference="mlx")

    # mlx is cheapest (range separation holds against both), reference itself is excluded.
    assert result["reference"] == "mlx"
    assert set(result["beats_reference"]) == {"ane-t1", "ane-t3"}
    assert set(result["reference_beats"]) == {"ane-t1", "ane-t3"}
    assert result["reference_beats"]["ane-t1"] is True
    assert result["reference_beats"]["ane-t3"] is True
    assert result["beats_reference"]["ane-t1"] is False
    assert set(result["paired_ratio_over_reference"]) == {"ane-t1", "ane-t3"}
    assert result["paired_ratio_over_reference"]["ane-t1"] == paired_ratios(
        [5.0, 5.5], [1.0, 1.1])


def test_summarize_ab_footprint_is_phys_plus_neural_per_repeat() -> None:
    per_config = _three_config_per_config()

    result = summarize_ab(list(per_config), per_config, reference="mlx")

    assert result["footprint_kib_peak"]["ane-t1"]["min"] == 2000 + 500
    assert result["footprint_kib_peak"]["ane-t1"]["max"] == 2010 + 510


def test_summarize_ab_rejects_a_reference_not_among_the_configs() -> None:
    per_config = _three_config_per_config()

    with pytest.raises(ValueError):
        summarize_ab(list(per_config), per_config, reference="not-a-config")
