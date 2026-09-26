"""Whole-system energy from `ioreg -rn AppleSmartBattery` PowerTelemetryData (stdlib only, no sudo).

Measured on a MacBook Air M4 (macOS 27.0, on AC, not charging), polling ioreg at 1 Hz for 150 s:

- The PowerTelemetryData dictionary is republished about once a minute (59.8 s between publishes);
  every poll in between returns the previous values unchanged.
- Each publish advances SystemPowerInAccumulatorCount and SystemLoadAccumulatorCount by ~59: the
  firmware accumulates one mW reading per second and publishes the batch.
- ΔAccumulatedSystemPowerIn / ΔSystemPowerInAccumulatorCount is the batch's mean input power in mW
  (19.4 W with a build running, 25.5 W with four `yes` loops added). ΔAccumulatedSystemEnergyConsumed
  agrees to 0.01% when read as µWh (Δacc_mW·s / 3.6).
- SystemPowerIn / SystemLoad are one-second readings taken at publish time, not means: integrating
  them at 1 Hz would weight one second per minute, so they are never used for energy here.
- Under a load the adapter could not cover, BatteryPower read -4636 mW (printed as its unsigned
  64-bit wrap) and the SystemLoad mean exceeded the SystemPowerIn mean (24.4 W vs 22.5 W): SystemLoad
  is what the system drew from adapter and battery together, SystemPowerIn only the adapter's share.
- When the battery started charging mid-session, SystemPowerIn's mean rose to 32.6 W with
  BatteryPower +18.6 W while SystemLoad's stayed at 15.7 W: SystemLoad excludes charging.
- Most publishes are 60 s apart, but some came 5-15 s apart (around charge-state changes); a
  segment is whatever lies between two publishes.

So system energy is SystemLoad's accumulator: each publish closes a segment whose mean power is
Δacc/Δcount, and a window's energy is Σ mean × segment seconds over the segments it overlaps. The
resolution is a minute, so callers align a timed window to publish boundaries and subtract the idle
baseline over the whole covered span. A nonzero BatteryPower in the span (charging or topping up
the adapter) is flagged; SystemLoad still counts only what the system drew. A Mac with no
battery has no PowerTelemetryData: system energy is unavailable there.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

_BLOCK_RE = re.compile(r'"PowerTelemetryData"\s*=\s*\{([^}]*)\}')
_FIELD_RE = re.compile(r'"(\w+)"=(\d+)')
_SIGNED_LIMIT = 1 << 63

LOAD_ACC, LOAD_COUNT = "AccumulatedSystemLoad", "SystemLoadAccumulatorCount"
IN_ACC, IN_COUNT = "AccumulatedSystemPowerIn", "SystemPowerInAccumulatorCount"


def parse_telemetry(ioreg_text: str) -> dict[str, int] | None:
    """PowerTelemetryData's integer fields (64-bit wraps read back as signed), or None without one."""
    match = _BLOCK_RE.search(ioreg_text)
    if match is None:
        return None
    fields = {}
    for key, raw in _FIELD_RE.findall(match.group(1)):
        value = int(raw)
        fields[key] = value - (1 << 64) if value >= _SIGNED_LIMIT else value
    return fields


@dataclass(frozen=True)
class Reading:
    t: float
    telemetry: dict[str, int]


@dataclass(frozen=True)
class Segment:
    start: float
    end: float
    load_mw: float
    in_mw: float | None
    battery_mw: int

    @property
    def seconds(self) -> float:
        return self.end - self.start


def _mean_mw(before: dict[str, int], after: dict[str, int], acc: str, count: str) -> float | None:
    ticks = after.get(count, 0) - before.get(count, 0)
    if ticks <= 0 or acc not in after or acc not in before:
        return None
    return (after[acc] - before[acc]) / ticks


def segments(readings: list[Reading]) -> list[Segment]:
    """One segment per publish after the first: from the previous publish to this one."""
    publishes: list[Reading] = []
    for reading in readings:
        if LOAD_COUNT not in reading.telemetry:
            continue
        if not publishes or reading.telemetry[LOAD_COUNT] != publishes[-1].telemetry[LOAD_COUNT]:
            publishes.append(reading)
    result = []
    for before, after in zip(publishes, publishes[1:]):
        load = _mean_mw(before.telemetry, after.telemetry, LOAD_ACC, LOAD_COUNT)
        if load is None:
            continue
        result.append(Segment(before.t, after.t, load, _mean_mw(before.telemetry, after.telemetry, IN_ACC, IN_COUNT),
                              after.telemetry.get("BatteryPower", 0)))
    return result


def _overlapping(segs: list[Segment], start: float, end: float) -> list[Segment]:
    return [s for s in segs if s.start < end and s.end > start]


@dataclass(frozen=True)
class SpanEnergy:
    joules: float | None
    start: float | None
    end: float | None

    @property
    def seconds(self) -> float | None:
        return None if self.start is None or self.end is None else self.end - self.start


def window_energy(segs: list[Segment], start: float, end: float) -> SpanEnergy:
    """Σ load mean × seconds over the segments overlapping [start, end), with the span they cover."""
    covering = _overlapping(segs, start, end)
    if not covering:
        return SpanEnergy(None, None, None)
    joules = sum(s.load_mw / 1000.0 * s.seconds for s in covering)
    return SpanEnergy(joules, covering[0].start, covering[-1].end)


def idle_power_w(segs: list[Segment], start: float, end: float) -> float | None:
    """Mean watts over the segments lying wholly inside [start, end], or None when none does."""
    inside = [s for s in segs if s.start >= start and s.end <= end]
    seconds = sum(s.seconds for s in inside)
    if not inside or seconds <= 0:
        return None
    return sum(s.load_mw / 1000.0 * s.seconds for s in inside) / seconds


def battery_flow(segs: list[Segment], start: float, end: float) -> bool:
    """True when any segment overlapping [start, end) published a nonzero BatteryPower."""
    return any(s.battery_mw != 0 for s in _overlapping(segs, start, end))
