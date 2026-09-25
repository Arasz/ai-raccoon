#!/usr/bin/env python3
"""Confirms Neural Engine dispatch with powermetrics: ANE/CPU/GPU power while the ANE-layout export
embeds rows on CoreML, against an idle window and the shipped graph on the CPU EP.

Run it as yourself, not under sudo: it asks for the sudo password once, for powermetrics alone.
Engines load and every bucket compiles before sampling starts, so the windows measure steady-state
rows, not the compiler.

Usage:
    python3 ane_powermetrics.py --ane-dir <export dir> --product-dir <granite dir with weights> \\
        [--cache-dir <warm coreml cache>] [--out-dir /tmp/ai-raccoon-powermetrics] [--seconds 30]

Writes <out-dir>/<timestamp>/: powermetrics.plist (raw samples), phases.json (window bounds, rows
per window) and summary.json (mean of every *power* field per window), and prints the summary.
"""

from __future__ import annotations

import argparse
import json
import os
import plistlib
import xml.parsers.expat
import subprocess
import sys
import time
from datetime import timezone
from pathlib import Path

SAMPLE_MS = 500
BUCKETS = (256, 512, 768, 1024)
ROW_TOKENS = (200, 450, 700, 1000)
TEXT_SOURCE = Path(__file__).resolve().parents[2] / "docs" / "work" / "2026-09-25-coreml-ane-buckets-and-residency.md"


# ---------------------------------------------------------------------------------------------
# Pure helpers (stdlib only)


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
        stamp = sample["timestamp"].replace(tzinfo=timezone.utc)  # plist dates are UTC, loaded naive
        name = phase_of(stamp.timestamp(), phases)
        if name is not None:
            grouped[name].append(power_fields(sample))
    summary: dict[str, dict[str, float]] = {}
    for name, rows in grouped.items():
        keys = sorted({k for row in rows for k in row})
        summary[name] = {k: sum(r[k] for r in rows if k in r) / sum(1 for r in rows if k in r) for k in keys}
        summary[name]["samples"] = len(rows)
    return summary


# ---------------------------------------------------------------------------------------------
# Run


def _rows(tokenizer) -> list[str]:
    ids = tokenizer.encode(TEXT_SOURCE.read_text(), add_special_tokens=False).ids
    return [tokenizer.decode(ids[i * 50: i * 50 + n]) for i, n in enumerate(ROW_TOKENS)]


def _run_phase(name: str, seconds: float, engine, rows: list[str]) -> dict:
    start = time.time()
    count = 0
    while time.time() - start < seconds:
        if engine is None:
            time.sleep(0.1)
            continue
        engine.embed(rows[count % len(rows)])
        count += 1
    return {"name": name, "start": start, "end": time.time(), "rows": count}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--ane-dir", type=Path, required=True)
    parser.add_argument("--product-dir", type=Path, required=True)
    parser.add_argument("--cache-dir", type=Path, default=Path("/tmp/ai-raccoon-powermetrics/coreml-cache"))
    parser.add_argument("--out-dir", type=Path, default=Path("/tmp/ai-raccoon-powermetrics"))
    parser.add_argument("--seconds", type=float, default=30.0)
    args = parser.parse_args()

    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
    from retrieval_tuning import chunk_window  # noqa: E402

    out = args.out_dir / time.strftime("%Y%m%d-%H%M%S")
    out.mkdir(parents=True, exist_ok=True)

    print("loading engines and compiling every bucket (first run: about 2 min)...", flush=True)
    ane = chunk_window.Engine(args.ane_dir, 1, "coreml", buckets=BUCKETS, compute_units="CPUAndNeuralEngine",
                              coreml_cache_dir=args.cache_dir)
    cpu = chunk_window.Engine(args.product_dir, 1, "cpu")
    rows = _rows(ane.tokenizer)
    for row in rows:
        ane.embed(row)
        cpu.embed(row)

    subprocess.run(["sudo", "-v"], check=True)
    total_s = 2 + 10 + 2 * args.seconds + 5
    plist = out / "powermetrics.plist"
    sampler = subprocess.Popen(["sudo", "powermetrics", "--samplers", "cpu_power,gpu_power,ane_power",
                                "-i", str(SAMPLE_MS), "-n", str(int(total_s * 1000 / SAMPLE_MS)),
                                "--format", "plist", "-o", str(plist)],
                               stdout=subprocess.DEVNULL)
    time.sleep(2)
    phases = [_run_phase("idle", 10, None, rows),
              _run_phase("ane-coreml", args.seconds, ane, rows),
              _run_phase("cpu-ep", args.seconds, cpu, rows)]
    sampler.wait()
    subprocess.run(["sudo", "chmod", "a+r", str(plist)], check=True)

    summary = summarize(split_samples(plist.read_bytes()), phases)
    (out / "phases.json").write_text(json.dumps(phases, indent=1))
    (out / "summary.json").write_text(json.dumps(summary, indent=1))
    print(json.dumps(summary, indent=1))
    print(f"results: {out}")
    sys.stdout.flush()
    os._exit(0)  # CoreML teardown can abort at interpreter exit, as in coreml_ab.py


if __name__ == "__main__":
    main()
