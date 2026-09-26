"""device_benchmark.battery: whole-system energy from AppleSmartBattery PowerTelemetryData (stdlib only)."""

from __future__ import annotations

import pytest

from device_benchmark import battery

# A real `ioreg -rn AppleSmartBattery` line from a MacBook Air M4 on AC, not charging.
REAL_LINE = (
    '  |   "PowerTelemetryData" = {"AccumulatedWallEnergyEstimate"=619144936,"SystemEnergyConsumed"=3660,'
    '"SystemPowerInAccumulatorCount"=153016,"AdapterEfficiencyLoss"=193,"SystemLoad"=13177,'
    '"AccumulatedSystemLoad"=2118775727,"AccumulatedSystemEnergyConsumed"=586363042,"SystemCurrentIn"=664,'
    '"WallEnergyEstimate"=3853,"SystemLoadAccumulatorCount"=167702,"SystemVoltageIn"=19825,"SystemPowerIn"=13177,'
    '"AccumulatedBatteryPower"=20679136,"BatteryPower"=0,"AccumulatedSystemPowerIn"=2111167537}\n'
)
REAL_IOREG = '+-o AppleSmartBattery  <class AppleSmartBattery>\n    {\n      "ExternalConnected" = Yes\n' + REAL_LINE + "    }\n"


def _reading(t: float, load_acc: int, load_count: int, in_acc: int = 0, in_count: int = 0, battery_mw: int = 0) -> battery.Reading:
    return battery.Reading(t, {
        "AccumulatedSystemLoad": load_acc, "SystemLoadAccumulatorCount": load_count,
        "AccumulatedSystemPowerIn": in_acc, "SystemPowerInAccumulatorCount": in_count, "BatteryPower": battery_mw,
    })


def test_parse_telemetry_reads_every_field_of_a_real_ioreg_dump() -> None:
    telemetry = battery.parse_telemetry(REAL_IOREG)

    assert telemetry is not None
    assert telemetry["AccumulatedSystemPowerIn"] == 2111167537
    assert telemetry["SystemPowerInAccumulatorCount"] == 153016
    assert telemetry["AccumulatedSystemLoad"] == 2118775727
    assert telemetry["BatteryPower"] == 0


def test_parse_telemetry_reads_a_wrapped_negative_as_signed() -> None:
    # ioreg prints BatteryPower = -4636 mW as its unsigned 64-bit wrap.
    telemetry = battery.parse_telemetry('"PowerTelemetryData" = {"BatteryPower"=18446744073709546980}')

    assert telemetry == {"BatteryPower": -4636}


def test_parse_telemetry_is_none_without_a_battery() -> None:
    assert battery.parse_telemetry("") is None


def test_segments_are_the_accumulator_deltas_between_publishes() -> None:
    # The dictionary republishes about once a minute; polls in between repeat the last values.
    readings = [_reading(0.0, 1_000, 10), _reading(1.0, 1_000, 10),
                _reading(60.0, 1_000 + 20_000 * 60, 70), _reading(61.0, 1_000 + 20_000 * 60, 70),
                _reading(120.0, 1_000 + 20_000 * 60 + 30_000 * 30, 100)]

    segments = battery.segments(readings)

    assert [(s.start, s.end) for s in segments] == [(0.0, 60.0), (60.0, 120.0)]
    assert segments[0].load_mw == pytest.approx(20_000)
    # 30 ticks at 30 W over a 60 s span: the mean is per tick (Δacc/Δcount), not per wall second.
    assert segments[1].load_mw == pytest.approx(30_000)


def test_window_energy_sums_every_segment_that_overlaps_the_window() -> None:
    readings = [_reading(0.0, 0, 0), _reading(60.0, 10_000 * 60, 60), _reading(120.0, 10_000 * 60 + 20_000 * 60, 120),
                _reading(180.0, 10_000 * 60 + 20_000 * 60 + 10_000 * 60, 180)]

    energy = battery.window_energy(battery.segments(readings), 70.0, 100.0)

    assert energy.joules == pytest.approx(20.0 * 60)
    assert (energy.start, energy.end) == (60.0, 120.0)


def test_window_energy_is_unavailable_when_no_segment_covers_the_window() -> None:
    energy = battery.window_energy([], 0.0, 10.0)

    assert energy.joules is None


def test_idle_power_means_only_segments_wholly_inside_the_idle_window() -> None:
    readings = [_reading(0.0, 0, 0), _reading(60.0, 50_000 * 60, 60), _reading(120.0, 50_000 * 60 + 5_000 * 60, 120)]

    # [0, 60) straddles the idle start; only [60, 120) lies inside [30, 125).
    assert battery.idle_power_w(battery.segments(readings), 30.0, 125.0) == pytest.approx(5.0)
    assert battery.idle_power_w(battery.segments(readings), 30.0, 100.0) is None


def test_battery_flow_flags_any_nonzero_battery_power_in_the_span() -> None:
    readings = [_reading(0.0, 0, 0), _reading(60.0, 60, 60, battery_mw=-4636), _reading(120.0, 120, 120)]
    segments = battery.segments(readings)

    assert battery.battery_flow(segments, 0.0, 60.0) is True
    assert battery.battery_flow(segments, 60.0, 120.0) is False
