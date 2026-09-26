"""The benchmark's JSON document (schemaVersion 1) and its markdown summary (stdlib only).

soc_* numbers are powermetrics' CPU+GPU+ANE rails; system_* numbers are what the whole machine drew
(AppleSmartBattery SystemLoad accumulator). *_net subtracts that run's idle baseline; a negative net
is kept and flagged below_idle. Summaries use warm runs with status ok only.
"""

from __future__ import annotations

import statistics
from typing import Sequence

from device_benchmark import battery, power
from device_benchmark.protocol import coreml_start_label
from retrieval_tuning.coreml import beats

SCHEMA_VERSION = 1

RUN_KEYS = (
    "device", "repeat", "slot", "cold", "status", "reason", "provider_actual", "chunks", "wall_s",
    "server_cpu_s", "compiler_cpu_s",
    "soc_energy_gross_j", "soc_energy_net_j", "soc_mean_w_gross", "soc_mean_w_net", "idle_mean_w_soc", "soc_source",
    "soc_below_idle",
    "system_energy_gross_j", "system_energy_net_j", "system_mean_w_gross", "system_mean_w_net", "idle_mean_w_system",
    "system_span_s", "system_below_idle", "battery_flow",
    "mj_per_chunk_soc_net", "mj_per_chunk_system_net",
    "phys_footprint_peak_kib", "neural_footprint_peak_kib", "thermal_pressure_max", "loadavg_start",
    "neural_engine_ready_s", "neural_engine_how", "ready_compiler_cpu_s",
)

TOP_KEYS = ("schemaVersion", "machine", "product", "corpus", "power_sources", "compile", "runs", "summary")

SUMMARY_METRICS = (
    "wall_s", "system_energy_net_j", "soc_energy_net_j", "mj_per_chunk_system_net", "mj_per_chunk_soc_net",
    "server_cpu_s", "phys_footprint_peak_kib", "neural_footprint_peak_kib",
)

VERDICT_METRICS = {"system_energy_net_j": "net system energy", "soc_energy_net_j": "net SoC energy", "wall_s": "wall time"}


def _per_chunk_mj(net_j: float | None, chunks: int | None) -> float | None:
    return None if net_j is None or not chunks else net_j * 1000.0 / chunks


def energy_fields(*, t0: float, t1: float, idle_start: float, idle_end: float, chunks: int | None,
                  soc_samples: list[dict] | None, segments: list[battery.Segment] | None) -> dict:
    """One run's energy numbers. SoC: powermetrics over [t0, t1). System: the publish segments covering
    [t0, t1), whose span is wider than the window, so the idle baseline comes off over that whole span;
    system_mean_w_gross is the span's mean draw and system_mean_w_net the extra watts over the window."""
    wall = t1 - t0
    fields: dict = {}

    soc_gross = soc_idle = None
    soc_source = "off"
    if soc_samples is not None:
        energy = power.soc_energy(soc_samples, t0, t1)
        soc_gross, soc_source = energy.joules, energy.source
        soc_idle = power.mean_power_w(soc_samples, idle_start, idle_end)
    soc_net = power.net_energy(soc_gross, soc_idle, wall)
    fields.update(soc_energy_gross_j=soc_gross, soc_energy_net_j=soc_net.joules, idle_mean_w_soc=soc_idle,
                  soc_mean_w_gross=None if soc_gross is None else soc_gross / wall,
                  soc_mean_w_net=None if soc_net.joules is None else soc_net.joules / wall,
                  soc_source=soc_source, soc_below_idle=soc_net.below_idle,
                  mj_per_chunk_soc_net=_per_chunk_mj(soc_net.joules, chunks))

    sys_gross = sys_idle = span_s = None
    flow = False
    if segments is not None:
        span = battery.window_energy(segments, t0, t1)
        sys_gross, span_s = span.joules, span.seconds
        sys_idle = battery.idle_power_w(segments, idle_start, idle_end)
        flow = battery.battery_flow(segments, t0, t1)
    sys_net = power.net_energy(sys_gross, sys_idle, span_s or 0.0)
    fields.update(system_energy_gross_j=sys_gross, system_energy_net_j=sys_net.joules, idle_mean_w_system=sys_idle,
                  system_mean_w_gross=None if sys_gross is None or not span_s else sys_gross / span_s,
                  system_mean_w_net=None if sys_net.joules is None else sys_net.joules / wall,
                  system_span_s=span_s, system_below_idle=sys_net.below_idle, battery_flow=flow,
                  mj_per_chunk_system_net=_per_chunk_mj(sys_net.joules, chunks))
    return fields


def _spread(values: Sequence[float]) -> dict:
    if not values:
        return {"median": None, "min": None, "max": None, "n": 0}
    return {"median": statistics.median(values), "min": min(values), "max": max(values), "n": len(values)}


def _values(runs: Sequence[dict], metric: str) -> list[float]:
    return [r[metric] for r in runs if r[metric] is not None]


def summarize(runs: Sequence[dict], reference: str) -> dict:
    """Per device: median/min/max of each metric over its warm ok runs, status counts; verdicts vs reference."""
    devices: dict[str, dict] = {}
    for device in sorted({r["device"] for r in runs}):
        own = [r for r in runs if r["device"] == device and not r["cold"]]
        usable = [r for r in own if r["status"] == "ok"]
        statuses: dict[str, int] = {}
        for run in own:
            statuses[run["status"]] = statuses.get(run["status"], 0) + 1
        devices[device] = {"statuses": statuses, **{m: _spread(_values(usable, m)) for m in SUMMARY_METRICS}}

    verdicts: dict[str, dict[str, str]] = {}
    for device in devices:
        if device == reference:
            continue
        verdicts[device] = {}
        for metric in VERDICT_METRICS:
            ours = _values([r for r in runs if r["device"] == device and not r["cold"] and r["status"] == "ok"], metric)
            theirs = _values([r for r in runs if r["device"] == reference and not r["cold"] and r["status"] == "ok"], metric)
            if not ours or not theirs:
                verdicts[device][metric] = "no data"
            elif beats(ours, theirs):
                verdicts[device][metric] = "lower"
            elif beats(theirs, ours):
                verdicts[device][metric] = "higher"
            else:
                verdicts[device][metric] = "overlap"
    return {"reference": reference, "devices": devices, "verdicts": verdicts}


def build_result(*, machine: dict, product: dict, corpus: dict, power_sources: dict, compile: dict,
                 runs: list[dict], reference: str, notes: Sequence[str] = ()) -> dict:
    """The schema-v1 document; raises ValueError when a run lacks a required key."""
    for run in runs:
        missing = [key for key in RUN_KEYS if key not in run]
        if missing:
            raise ValueError(f"run {run.get('slot')} ({run.get('device')}) lacks {', '.join(missing)}")
    return {
        "schemaVersion": SCHEMA_VERSION, "machine": machine, "product": product, "corpus": corpus,
        "power_sources": power_sources, "compile": compile, "runs": runs,
        "summary": summarize(runs, reference), "notes": list(notes),
    }


def _cell(value: float | None, digits: int = 1) -> str:
    return "–" if value is None else f"{value:.{digits}f}"


def _mib(value: float | None) -> str:
    return "–" if value is None else f"{value / 1024:.0f}"


def render_markdown(doc: dict) -> str:
    """One row per device, the cold-compile line and the separation verdicts against the reference."""
    machine, product, corpus, sources = doc["machine"], doc["product"], doc["corpus"], doc["power_sources"]
    chunk_counts = [r["chunks"] for r in doc["runs"] if r["status"] == "ok" and r["chunks"] is not None]
    chunks = statistics.mode(chunk_counts) if chunk_counts else "?"
    lines = [
        f"# Device benchmark: {machine.get('chip')} ({machine.get('model')})",
        "",
        f"AiRaccoon {product.get('version')} · corpus {corpus.get('git_sha')} ({corpus.get('files')} files) · "
        f"{chunks} chunks · SoC: {sources.get('soc')} · system: {sources.get('system')}",
        "",
        "| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s "
        "| peak phys MiB | peak neural MiB |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for device, row in doc["summary"]["devices"].items():
        status = ", ".join(f"{name}×{count}" for name, count in sorted(row["statuses"].items())) or "none"
        lines.append(
            f"| {device} | {status} | {_cell(row['wall_s']['median'])} | {_cell(row['system_energy_net_j']['median'])} "
            f"| {_cell(row['soc_energy_net_j']['median'])} | {_cell(row['mj_per_chunk_system_net']['median'])} "
            f"| {_cell(row['mj_per_chunk_soc_net']['median'])} | {_cell(row['server_cpu_s']['median'])} "
            f"| {_mib(row['phys_footprint_peak_kib']['median'])} | {_mib(row['neural_footprint_peak_kib']['median'])} |")
    compile_info = doc["compile"]
    lines.append("")
    if compile_info.get("cold_s") is not None:
        lines.append(f"Cold coreml compile: {_cell(compile_info.get('cold_s'))} s, {_cell(compile_info.get('cold_j'))} J system, "
                     f"{_cell(compile_info.get('compiler_cpu_s'))} compiler CPU-s; warm load {_cell(compile_info.get('warm_ready_s'))} s.")
    else:
        lines.append("Cold coreml compile: not measured.")
    starts = [r for r in doc["runs"] if r["device"] == "coreml" and r.get("neural_engine_ready_s") is not None]
    if starts:
        lines.append("")
        lines.append("coreml starts: " + "; ".join(
            f"slot {r['slot']} {coreml_start_label(r['cold'], r.get('ready_compiler_cpu_s'))} "
            f"{_cell(r['neural_engine_ready_s'])} s, {_cell(r.get('ready_compiler_cpu_s'))} compiler CPU-s" for r in starts))
    lines.append("")
    reference = doc["summary"]["reference"]
    for device, verdict in doc["summary"]["verdicts"].items():
        parts = []
        for metric, label in VERDICT_METRICS.items():
            outcome = verdict[metric]
            answer = {"lower": f"yes ({device} lower)", "higher": f"yes ({device} higher)",
                      "overlap": "no (ranges overlap)"}.get(outcome, "no data")
            parts.append(f"{label} separated? {answer}")
        lines.append(f"- {device} vs {reference}: " + "; ".join(parts))
    for note in doc.get("notes", []):
        lines.append(f"- note: {note}")
    return "\n".join(lines) + "\n"
