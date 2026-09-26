"""device_benchmark.power: SoC energy over a timed window from powermetrics samples (stdlib only)."""

from __future__ import annotations

import plistlib
from datetime import datetime, timedelta, timezone

import pytest

from device_benchmark import power

T0 = datetime(2026, 9, 26, 12, 0, 0, tzinfo=timezone.utc)
EPOCH0 = T0.timestamp()


def _sample(end_s: float, elapsed_ms: float, processor: dict, thermal: str = "Nominal") -> dict:
    """One powermetrics sample as split_samples returns it: a naive UTC timestamp at the interval's end."""
    raw = plistlib.dumps({
        "timestamp": T0 + timedelta(seconds=end_s),
        "elapsed_ns": int(elapsed_ms * 1_000_000),
        "processor": processor,
        "thermal_pressure": thermal,
    })
    return power.split_samples(raw + b"\0")[0]


def test_energy_weights_each_sample_by_its_own_elapsed_ns() -> None:
    # 1000 mW for 0.5 s, then 1000 mW for 1.5 s: 0.5 J + 1.5 J. A fixed 500 ms interval would give 1.0 J.
    samples = [_sample(0.5, 500, {"combined_power": 1000}), _sample(2.0, 1500, {"combined_power": 1000})]

    energy = power.soc_energy(samples, EPOCH0, EPOCH0 + 2.0)

    assert energy.joules == pytest.approx(2.0)
    assert energy.source == "combined"
    assert energy.samples == 2


def test_energy_excludes_samples_whose_interval_lies_outside_the_window() -> None:
    samples = [_sample(1.0, 1000, {"combined_power": 9000}),   # [0, 1): before the window
               _sample(2.0, 1000, {"combined_power": 2000}),   # [1, 2): inside
               _sample(3.0, 1000, {"combined_power": 9000})]   # [2, 3): after

    energy = power.soc_energy(samples, EPOCH0 + 1.0, EPOCH0 + 2.0)

    assert energy.joules == pytest.approx(2.0)
    assert energy.samples == 1


def test_energy_falls_back_to_the_summed_rails_when_combined_is_absent() -> None:
    samples = [_sample(1.0, 1000, {"cpu_power": 300, "gpu_power": 200, "ane_power": 500})]

    energy = power.soc_energy(samples, EPOCH0, EPOCH0 + 1.0)

    assert energy.joules == pytest.approx(1.0)
    assert energy.source == "summed"


def test_energy_is_unavailable_when_no_sample_carries_a_rail() -> None:
    energy = power.soc_energy([_sample(1.0, 1000, {"freq": 3})], EPOCH0, EPOCH0 + 1.0)

    assert energy.joules is None
    assert energy.source == "unavailable"


def test_mean_power_is_energy_over_covered_seconds() -> None:
    samples = [_sample(1.0, 1000, {"combined_power": 1000}), _sample(3.0, 2000, {"combined_power": 4000})]

    # (1 J + 8 J) / 3 s
    assert power.mean_power_w(samples, EPOCH0, EPOCH0 + 3.0) == pytest.approx(3.0)


def test_net_energy_subtracts_idle_power_times_duration() -> None:
    net = power.net_energy(gross_j=10.0, idle_mean_w=2.0, duration_s=3.0)

    assert net.joules == pytest.approx(4.0)
    assert net.below_idle is False


def test_net_energy_below_idle_is_kept_negative_and_flagged() -> None:
    net = power.net_energy(gross_j=5.0, idle_mean_w=2.0, duration_s=3.0)

    assert net.joules == pytest.approx(-1.0)
    assert net.below_idle is True


def test_net_energy_is_none_when_either_side_is_missing() -> None:
    assert power.net_energy(gross_j=None, idle_mean_w=2.0, duration_s=3.0).joules is None
    assert power.net_energy(gross_j=5.0, idle_mean_w=None, duration_s=3.0).joules is None


def test_thermal_pressure_max_ranks_levels_not_strings() -> None:
    # "Heavy" sorts before "Moderate" alphabetically; the rank must win.
    samples = [_sample(1.0, 1000, {}, "Moderate"), _sample(2.0, 1000, {}, "Heavy"), _sample(3.0, 1000, {}, "Nominal")]

    assert power.thermal_pressure_max(samples, EPOCH0, EPOCH0 + 3.0) == "Heavy"


def test_thermal_pressure_max_is_none_without_samples() -> None:
    assert power.thermal_pressure_max([], EPOCH0, EPOCH0 + 1.0) is None


def test_sample_ends_recover_sub_second_ends_from_whole_second_timestamps() -> None:
    # plist dates keep whole seconds: ends at 0.5, 1.0, 1.5 load as 0, 1, 1.
    samples = [_sample(0.5, 500, {}), _sample(1.0, 500, {}), _sample(1.5, 500, {})]

    assert [e - EPOCH0 for e in power.sample_ends(samples)] == pytest.approx([0.5, 1.0, 1.5])
