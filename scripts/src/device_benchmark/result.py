"""The benchmark's JSON document (schemaVersion 1) and its markdown summary (stdlib only).

soc_* numbers are powermetrics' CPU+GPU+ANE rails; system_* numbers are what the whole machine drew
(AppleSmartBattery SystemLoad accumulator). *_net subtracts that run's idle baseline; a negative net
is kept and flagged below_idle. Summaries use warm runs with status ok only.
"""

from __future__ import annotations

import statistics
from typing import Sequence

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
    "neural_engine_ready_s", "neural_engine_how",
)

TOP_KEYS = ("schemaVersion", "machine", "product", "corpus", "power_sources", "compile", "runs", "summary")

SUMMARY_METRICS = (
    "wall_s", "system_energy_net_j", "soc_energy_net_j", "mj_per_chunk_system_net", "mj_per_chunk_soc_net",
    "server_cpu_s", "phys_footprint_peak_kib", "neural_footprint_peak_kib",
)

VERDICT_METRICS = {"system_energy_net_j": "net system energy", "soc_energy_net_j": "net SoC energy", "wall_s": "wall time"}


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
