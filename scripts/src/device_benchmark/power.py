"""SoC energy from `powermetrics --format plist` samples (stdlib only).

Each sample covers the `elapsed_ns` before its `timestamp` (UTC, loaded naive). The plist date is
truncated to whole seconds, so a sample's end is rebuilt from the running sum of elapsed_ns, anchored
at the latest lower bound max(timestamp_i − Σelapsed_≤i). Energy over a window is each sample's mW
times its own elapsed time, over the samples whose interval midpoint falls inside the window. "combined" is powermetrics' processor.combined_power (CPU+GPU+ANE);
"summed" adds cpu/gpu/ane_power when combined is absent; "unavailable" when neither is present.
"""

from __future__ import annotations

import plistlib
import xml.parsers.expat
from dataclasses import dataclass
from datetime import timezone

_RAILS = ("cpu_power", "gpu_power", "ane_power")
_THERMAL_RANK = {"Nominal": 0, "Fair": 1, "Moderate": 2, "Serious": 3, "Heavy": 4, "Critical": 5, "Trapping": 6, "Sleeping": 7}


def split_samples(data: bytes) -> list[dict]:
    """powermetrics --format plist output: NUL-separated plist docs; a truncated tail is dropped."""
    samples = []
    for chunk in data.split(b"\0"):
        if not chunk.strip():
            continue
        try:
            samples.append(plistlib.loads(chunk))
        except (plistlib.InvalidFileException, xml.parsers.expat.ExpatError, ValueError):
            continue
    return samples


def power_fields(node: object, prefix: str = "") -> dict[str, float]:
    """Numeric leaves whose key mentions power, by dotted path (lists are not descended)."""
    fields: dict[str, float] = {}
    if isinstance(node, dict):
        for key, value in node.items():
            path = f"{prefix}{key}"
            if isinstance(value, dict):
                fields.update(power_fields(value, f"{path}."))
            elif isinstance(value, (int, float)) and not isinstance(value, bool) and "power" in key:
                fields[path] = float(value)
    return fields


def phase_of(epoch: float, phases: list[dict]) -> str | None:
    """Name of the phase whose [start, end) window holds epoch, or None."""
    for phase in phases:
        if phase["start"] <= epoch < phase["end"]:
            return phase["name"]
    return None


def summarize(samples: list[dict], phases: list[dict]) -> dict[str, dict[str, float]]:
    """Per phase: mean of every power field over the samples stamped inside it, plus the count."""
    grouped: dict[str, list[dict[str, float]]] = {p["name"]: [] for p in phases}
    for sample in samples:
        name = phase_of(sample_end(sample), phases)
        if name is not None:
            grouped[name].append(power_fields(sample))
    summary: dict[str, dict[str, float]] = {}
    for name, rows in grouped.items():
        keys = sorted({k for row in rows for k in row})
        summary[name] = {k: sum(r[k] for r in rows if k in r) / sum(1 for r in rows if k in r) for k in keys}
        summary[name]["samples"] = len(rows)
    return summary


def sample_end(sample: dict) -> float:
    """The sample's timestamp as epoch seconds (plist dates are UTC, loaded naive)."""
    return sample["timestamp"].replace(tzinfo=timezone.utc).timestamp()


def sample_seconds(sample: dict) -> float:
    return sample.get("elapsed_ns", 0) / 1e9


def sample_ends(samples: list[dict]) -> list[float]:
    """Each sample's end epoch, rebuilt from the elapsed_ns running sum (timestamps are whole seconds)."""
    cumulative = []
    total = 0.0
    for sample in samples:
        total += sample_seconds(sample)
        cumulative.append(total)
    if not samples:
        return []
    anchor = max(sample_end(s) - c for s, c in zip(samples, cumulative))
    return [anchor + c for c in cumulative]


def _windowed(samples: list[dict], start: float, end: float) -> list[dict]:
    """The samples whose interval midpoint lies in [start, end)."""
    return [s for s, e in zip(samples, sample_ends(samples)) if start <= e - sample_seconds(s) / 2 < end]


def soc_power_mw(sample: dict) -> tuple[float | None, str]:
    """(mW, source) of one sample: combined_power, else the summed rails, else (None, 'unavailable')."""
    processor = sample.get("processor") or {}
    if isinstance(processor.get("combined_power"), (int, float)):
        return float(processor["combined_power"]), "combined"
    gpu = sample.get("gpu") or {}
    rails = [processor.get(rail, gpu.get(rail)) for rail in _RAILS]
    present = [float(v) for v in rails if isinstance(v, (int, float))]
    if present:
        return sum(present), "summed"
    return None, "unavailable"


@dataclass(frozen=True)
class Energy:
    joules: float | None
    source: str
    samples: int
    seconds: float


def soc_energy(samples: list[dict], start: float, end: float) -> Energy:
    """Σ mW × elapsed over the samples inside [start, end); the source is the weakest one used."""
    joules = 0.0
    seconds = 0.0
    count = 0
    sources: set[str] = set()
    for sample in _windowed(samples, start, end):
        mw, source = soc_power_mw(sample)
        if mw is None:
            continue
        joules += mw / 1000.0 * sample_seconds(sample)
        seconds += sample_seconds(sample)
        count += 1
        sources.add(source)
    if count == 0:
        return Energy(None, "unavailable", 0, 0.0)
    return Energy(joules, "summed" if "summed" in sources else "combined", count, seconds)


def mean_power_w(samples: list[dict], start: float, end: float) -> float | None:
    """Mean watts over the window: energy over the seconds the included samples cover."""
    energy = soc_energy(samples, start, end)
    if energy.joules is None or energy.seconds <= 0:
        return None
    return energy.joules / energy.seconds


@dataclass(frozen=True)
class NetEnergy:
    joules: float | None
    below_idle: bool


def net_energy(gross_j: float | None, idle_mean_w: float | None, duration_s: float) -> NetEnergy:
    """gross − idle × duration; a negative result is kept and flagged, never clamped."""
    if gross_j is None or idle_mean_w is None:
        return NetEnergy(None, False)
    net = gross_j - idle_mean_w * duration_s
    return NetEnergy(net, net < 0)


def thermal_pressure_max(samples: list[dict], start: float, end: float) -> str | None:
    """The highest thermal pressure level any sample in the window reported, or None."""
    levels = [s["thermal_pressure"] for s in _windowed(samples, start, end) if "thermal_pressure" in s]
    if not levels:
        return None
    return max(levels, key=lambda level: _THERMAL_RANK.get(level, -1))
