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


def test_seeded_sample_length_matches_n_when_n_is_within_the_pool() -> None:
    rows = list(range(100))

    assert len(seeded_sample(rows, 10, seed=1)) == 10


def test_seeded_sample_draws_distinct_items_without_replacement() -> None:
    rows = list(range(100))

    sample = seeded_sample(rows, 10, seed=1)

    assert len(set(sample)) == 10
    assert set(sample) <= set(rows)


def test_seeded_sample_returns_the_whole_pool_shuffled_when_n_at_least_matches_it() -> None:
    rows = list(range(5))

    sample = seeded_sample(rows, 5, seed=1)

    assert sorted(sample) == rows
    assert sample != rows, "n >= len(pool) must shuffle, not return the pool in its original order"


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


def test_summarize_mean_is_the_arithmetic_mean_not_the_median() -> None:
    # [10, 11, 15]: median would be 11.0, the arithmetic mean is 12.0 - distinct enough to catch
    # a stat.median/fmean mix-up.
    summary = summarize({"cpu": [10.0, 11.0, 15.0]})

    assert summary["cpu"]["mean"] == 12.0


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


def test_paired_ratios_a_over_b_not_b_over_a() -> None:
    assert paired_ratios([30.0, 10.0, 20.0], [10.0, 5.0, 10.0]) == [3.0, 2.0, 2.0]


def test_paired_ratios_rejects_mismatched_lengths() -> None:
    with pytest.raises(ValueError):
        paired_ratios([1.0, 2.0], [1.0, 2.0, 3.0])


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


# ---------------------------------------------------------------------------------------------
# summarize_ab: exact per-metric values from a non-proportional fixture.
#
# _three_config_per_config's metrics all scale together (cpu_s, p50_ms, p95_ms, footprint all rise
# and fall in lockstep across reps and configs), so a mutation that reads the wrong metric column
# (e.g. p95_ms where p50_ms belongs) still produces plausible-looking numbers and no assertion
# above catches it. These values are deliberately unrelated per metric.


def _non_proportional_per_config() -> dict[str, list[dict]]:
    return {
        "mlx": [
            {"cpu_s": 1.0, "p50_ms": 50.0, "p95_ms": 9.0, "phys_kib_peak": 300, "neural_kib_peak": 7},
            {"cpu_s": 3.0, "p50_ms": 10.0, "p95_ms": 90.0, "phys_kib_peak": 100, "neural_kib_peak": 21},
        ],
        "ane": [
            {"cpu_s": 5.0, "p50_ms": 2.0, "p95_ms": 400.0, "phys_kib_peak": 900, "neural_kib_peak": 3},
            {"cpu_s": 7.0, "p50_ms": 40.0, "p95_ms": 40.0, "phys_kib_peak": 100, "neural_kib_peak": 33},
        ],
    }


def test_summarize_ab_reads_each_metric_from_its_own_column() -> None:
    per_config = _non_proportional_per_config()

    result = summarize_ab(list(per_config), per_config, reference="mlx")

    assert result["cpu_s"]["mlx"] == {"min": 1.0, "max": 3.0, "mean": 2.0}
    assert result["cpu_s"]["ane"] == {"min": 5.0, "max": 7.0, "mean": 6.0}
    assert result["p50_ms"]["mlx"] == {"min": 10.0, "max": 50.0, "mean": 30.0}
    assert result["p50_ms"]["ane"] == {"min": 2.0, "max": 40.0, "mean": 21.0}
    assert result["p95_ms"]["mlx"] == {"min": 9.0, "max": 90.0, "mean": 49.5}
    assert result["p95_ms"]["ane"] == {"min": 40.0, "max": 400.0, "mean": 220.0}
    assert result["footprint_kib_peak"]["mlx"] == {"min": 121, "max": 307, "mean": 214.0}
    assert result["footprint_kib_peak"]["ane"] == {"min": 133, "max": 903, "mean": 518.0}
