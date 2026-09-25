"""CoreML EP pure helpers (stdlib only): buckets, free-dim overrides, cache dirs, log parsing,
lazy per-bucket sessions, and the A/B scheduling/summary helpers. onnxruntime and numpy are the
Engine's job (chunk_window.py), never this module's, so this stays importable in a bare venv.
"""

from __future__ import annotations

import random
import re
import statistics
from collections import OrderedDict
from pathlib import Path
from typing import Callable, Mapping, Sequence, TypeVar

T = TypeVar("T")

BATCH_DIM_NAME = "batch_size"

_PARTITION_RE = re.compile(
    r"number of partitions supported by CoreML:\s*(\d+)\s*"
    r"number of nodes in the graph:\s*(\d+)\s*"
    r"number of nodes supported by CoreML:\s*(\d+)"
)
_COMPUTE_PLAN_RE = re.compile(r"Operation:\s*[\w.]+,\s*Device Usage:\s*<ML(\w+?)ComputeDevice:")


# ---------------------------------------------------------------------------------------------
# Buckets, overrides, cache dirs


def bucket_lengths(step: int, top: int) -> list[int]:
    """Multiples of step up to top; the last bucket is always top, even off-multiple or below step."""
    if step <= 0 or top <= 0:
        raise ValueError(f"step and top must be positive, got step={step} top={top}")
    buckets = list(range(step, top + 1, step))
    if not buckets or buckets[-1] != top:
        buckets.append(top)
    return buckets


def free_dim_overrides(input_shapes: Mapping[str, Sequence[object]], n: int) -> dict[str, int]:
    """add_free_dimension_override_by_name targets: batch pinned to 1, every other symbolic dim to n.

    Names come from the graph's own input shapes (e.g. onnxruntime's session.get_inputs()), so an
    input whose non-batch dim isn't literally "sequence_length" (attention_mask's is not) still
    gets pinned.
    """
    overrides: dict[str, int] = {}
    for dims in input_shapes.values():
        for dim in dims:
            if not isinstance(dim, str):
                continue
            overrides[dim] = 1 if dim == BATCH_DIM_NAME else n
    return overrides


def cache_dir_for(root: Path, model_sha: str, ort_version: str, units: str, bucket: int,
                   specialization: str | None = None) -> Path:
    """Compiled-model cache dir, one per (model, ORT version, compute units, bucket, specialization).

    A cache dir shared across buckets, or across specialization strategies, serves a compiled
    artifact built for the wrong shape or strategy and CoreML crashes at Run, so both must be their
    own path segments, not folded into a combined key.
    """
    return Path(root) / model_sha[:12] / ort_version / units / f"bucket-{bucket}" / (specialization or "default")


# ---------------------------------------------------------------------------------------------
# ORT verbose log parsing


def parse_partition_log(text: str) -> dict[str, int]:
    """(partitions, nodes_total, nodes_supported) from ORT's CoreML GetCapability log line."""
    match = _PARTITION_RE.search(text)
    if match is None:
        raise ValueError("no CoreML GetCapability partition line found in log")
    partitions, nodes_total, nodes_supported = (int(g) for g in match.groups())
    return {"partitions": partitions, "nodes_total": nodes_total, "nodes_supported": nodes_supported}


def parse_compute_plan(text: str) -> dict[str, int]:
    """Op count per compute device (CPU, NeuralEngine, GPU, ...) from a ProfileComputePlan log."""
    counts: dict[str, int] = {}
    for device in _COMPUTE_PLAN_RE.findall(text):
        counts[device] = counts.get(device, 0) + 1
    return counts


# ---------------------------------------------------------------------------------------------
# BucketSessions


class BucketSessions:
    """Lazy per-bucket session cache with an optional LRU eviction budget (max_live=0: unbounded)."""

    def __init__(self, factory: Callable[[int], object], max_live: int = 0) -> None:
        self._factory = factory
        self._max_live = max_live
        self._live: "OrderedDict[int, object]" = OrderedDict()
        self.builds = 0
        self.evictions = 0
        self.peak_live = 0

    def get(self, bucket: int) -> object:
        if bucket in self._live:
            self._live.move_to_end(bucket)
            return self._live[bucket]
        session = self._factory(bucket)
        self.builds += 1
        self._live[bucket] = session
        if self._max_live and len(self._live) > self._max_live:
            self._live.popitem(last=False)  # no close() on InferenceSession: drop the reference, let GC collect
            self.evictions += 1
        self.peak_live = max(self.peak_live, len(self._live))
        return session

    @property
    def live_count(self) -> int:
        return len(self._live)


# ---------------------------------------------------------------------------------------------
# ANE compiler CPU: ps parsing and per-pid delta


def parse_ps_time(value: str) -> float:
    """ps's colon-separated `TIME` field (`MM:SS.ss` or `HH:MM:SS`) as seconds."""
    seconds = 0.0
    for part in value.split(":"):
        seconds = seconds * 60 + float(part)
    return seconds


def parse_ps_pids(text: str, names: Sequence[str]) -> dict[int, float]:
    """pid -> CPU seconds, for every `ps -A -o pid=,time=,comm=` line whose comm basename matches."""
    pids: dict[int, float] = {}
    for line in text.splitlines():
        parts = line.split(None, 2)
        if len(parts) != 3:
            continue
        pid_str, time_str, comm = parts
        if not pid_str.isdigit():
            continue
        if comm.strip().rsplit("/", 1)[-1] in names:
            pids[int(pid_str)] = parse_ps_time(time_str)
    return pids


def compiler_cpu_delta(before: Mapping[int, float], after: Mapping[int, float]) -> tuple[float, list[int]]:
    """Sum of after[pid]-before.get(pid, 0) over pids present in `after`, plus the pids present in
    `before` but gone from `after`.

    A lower bound, not an exact delta: a pid that exited between the two samples did CPU work this
    sum cannot see (its own before-value has nowhere to be subtracted from), so its vanishing is
    reported instead of silently making the total go negative.
    """
    total = sum(after[pid] - before.get(pid, 0.0) for pid in after)
    vanished = sorted(set(before) - set(after))
    return total, vanished


# ---------------------------------------------------------------------------------------------
# A/B scheduling and summaries


def seeded_sample(items: Sequence[T], n: int, seed: int) -> list[T]:
    """n items drawn reproducibly for a given seed; the whole (shuffled) pool if n >= len(items)."""
    rng = random.Random(seed)
    pool = list(items)
    if n >= len(pool):
        rng.shuffle(pool)
        return pool
    return rng.sample(pool, n)


def interleave(configs: Sequence[T], repeats: int) -> list[tuple[int, T]]:
    """(repeat_index, config) schedule: every config once per repeat, start rotated each repeat."""
    n = len(configs)
    schedule: list[tuple[int, T]] = []
    for repeat in range(repeats):
        offset = repeat % n if n else 0
        order = list(configs[offset:]) + list(configs[:offset])
        schedule.extend((repeat, config) for config in order)
    return schedule


def summarize(samples: Mapping[str, Sequence[float]]) -> dict[str, dict[str, float]]:
    """min/max/mean per config."""
    return {name: {"min": min(values), "max": max(values), "mean": statistics.fmean(values)}
            for name, values in samples.items()}


def beats(a: Sequence[float], b: Sequence[float]) -> bool:
    """"A beats B" by range separation: every value of a is strictly below every value of b."""
    return max(a) < min(b)


def paired_ratios(a: Sequence[float], b: Sequence[float]) -> list[float]:
    """a/b per rotation, paired by position (same repeat index for both configs)."""
    return [x / y for x, y in zip(a, b, strict=True)]


def summarize_ab(names: Sequence[str], per_config: Mapping[str, Sequence[Mapping[str, float]]],
                  reference: str) -> dict[str, object]:
    """Per-config min/max/mean of cpu_s, p50_ms, p95_ms and peak phys+neural footprint over every
    name in `names` (not just the first two), plus `beats`/paired within-rotation ratio of every
    other config against `reference`.
    """
    if reference not in per_config:
        raise ValueError(f"reference config {reference!r} not among configs {list(per_config)}")

    metric_series = {
        "cpu_s": {name: [rep["cpu_s"] for rep in per_config[name]] for name in names},
        "p50_ms": {name: [rep["p50_ms"] for rep in per_config[name]] for name in names},
        "p95_ms": {name: [rep["p95_ms"] for rep in per_config[name]] for name in names},
        "footprint_kib_peak": {name: [rep["phys_kib_peak"] + rep["neural_kib_peak"] for rep in per_config[name]]
                               for name in names},
    }
    result: dict[str, object] = {metric: summarize(series) for metric, series in metric_series.items()}

    reference_cpu = metric_series["cpu_s"][reference]
    beats_reference: dict[str, bool] = {}
    reference_beats: dict[str, bool] = {}
    paired_ratio_over_reference: dict[str, list[float]] = {}
    for name in names:
        if name == reference:
            continue
        cpu = metric_series["cpu_s"][name]
        beats_reference[name] = beats(cpu, reference_cpu)
        reference_beats[name] = beats(reference_cpu, cpu)
        paired_ratio_over_reference[name] = paired_ratios(cpu, reference_cpu)

    result["reference"] = reference
    result["beats_reference"] = beats_reference
    result["reference_beats"] = reference_beats
    result["paired_ratio_over_reference"] = paired_ratio_over_reference
    return result
