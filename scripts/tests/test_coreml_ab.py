"""CoreML A/B helpers: seeded sampling, rotated interleave, and range-separation summaries."""

from __future__ import annotations

from retrieval_tuning.coreml import beats, interleave, paired_ratios, seeded_sample, summarize


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
