"""The benchmark's run order, per-run decisions and session loop (stdlib only; no I/O but status.json reads).

Statuses: ok (ran on the device asked for), fell_back (the product served it on another provider),
refused (coreml: the Neural Engine switch refused), timeout (coreml never settled), invalid (its
chunk count differs from the session's), error (the run raised; its reason is kept).
"""

from __future__ import annotations

import json
import re
import shutil
import statistics
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Sequence

from retrieval_tuning.coreml import interleave

EXPECTED_PROVIDER = {"auto": "WebGPU", "gpu": "WebGPU", "cpu": "CPU", "mlx": "MLX", "coreml": "CoreML"}

_SESSION_RE = re.compile(r"Embedding session created: .*?execution provider (.+?)\s*$", re.MULTILINE)
_NEURAL_ENGINE_RE = re.compile(r"Embedding now runs on the Neural Engine: sessions (.+?) in ([\d.]+) s")


def provider_from_log(log_text: str) -> str | None:
    """The execution provider of the last EventId 428 session line, refusal suffix kept, or None."""
    matches = _SESSION_RE.findall(log_text)
    return matches[-1] if matches else None


def provider_status(device: str, provider: str | None) -> str:
    """ok when the session runs on the device's own provider; anything else is a fallback."""
    return "ok" if provider is not None and provider == EXPECTED_PROVIDER.get(device) else "fell_back"


_COREML_REFUSED_RE = re.compile(r"\(CoreML refused: (.+)\)$")


def coreml_refusal(provider: str | None) -> str | None:
    """The reason when the session line says CoreML was refused before any compile started, else None."""
    match = _COREML_REFUSED_RE.search(provider or "")
    return match.group(1) if match else None


def neural_engine_from_log(log_text: str) -> tuple[str, float] | None:
    """("compiled" | "loaded from cache", seconds) from the EventId 437 line, or None."""
    matches = _NEURAL_ENGINE_RE.findall(log_text)
    if not matches:
        return None
    how, seconds = matches[-1]
    return how, float(seconds)


@dataclass(frozen=True)
class NeuralEngineWait:
    state: str  # serving | refused | timeout
    reason: str | None
    seconds: float


def read_status(path: Path) -> dict | None:
    """status.json's content, or None while it is missing or half-written."""
    try:
        return json.loads(Path(path).read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def wait_for_neural_engine(status_path: Path, pid: int, *, timeout: float, clock: Callable[[], float] = time.monotonic,
                           sleep: Callable[[float], None] = time.sleep, poll: float = 0.5) -> NeuralEngineWait:
    """Poll status.json until this pid's switch serves on or refuses the Neural Engine, or timeout."""
    start = clock()
    while True:
        status = read_status(status_path)
        if status is not None and status.get("pid") == pid:
            if status.get("state") == "NeuralEngineServing":
                return NeuralEngineWait("serving", status.get("reason"), clock() - start)
            if status.get("state") == "Refused":
                return NeuralEngineWait("refused", status.get("reason"), clock() - start)
        if clock() - start >= timeout:
            return NeuralEngineWait("timeout", None, clock() - start)
        sleep(poll)


@dataclass(frozen=True)
class Slot:
    index: int
    repeat: int
    device: str
    cold: bool


def schedule(devices: Sequence[str], repeats: int) -> list[Slot]:
    """One cold coreml run first when coreml is measured, then every device once per repeat, rotated."""
    order: list[tuple[int, str, bool]] = [(0, "coreml", True)] if "coreml" in devices else []
    order += [(repeat, device, False) for repeat, device in interleave(list(devices), repeats)]
    return [Slot(i, repeat, device, cold) for i, (repeat, device, cold) in enumerate(order)]


def run_session(slots: Sequence[Slot], run_one: Callable[[Slot], dict]) -> list[dict]:
    """Every slot runs whatever an earlier one returned; an exception becomes status error with its reason."""
    runs = []
    for slot in slots:
        try:
            result = dict(run_one(slot))
        except Exception as exc:  # noqa: BLE001 — one failed run must not lose the rest of the session
            result = {"status": "error", "reason": str(exc)}
        runs.append({**result, "device": slot.device, "repeat": slot.repeat, "slot": slot.index, "cold": slot.cold})
    return runs


def mark_chunk_mismatch(runs: list[dict]) -> int | None:
    """The session's chunk count (the most common among runs that embedded); runs off it become invalid."""
    counts = [r["chunks"] for r in runs if r.get("status") in ("ok", "fell_back") and r.get("chunks") is not None]
    if not counts:
        return None
    reference = statistics.mode(counts)
    for run in runs:
        if run.get("status") in ("ok", "fell_back") and run.get("chunks") != reference:
            run["status"] = "invalid"
            run["reason"] = f"chunk count {run.get('chunks')} differs from the session's {reference}"
    return reference


def calibration_notes(device: str, cold_run_calibrated: bool, wall: float, tier: int,
                      tier_dirs: Sequence[str]) -> list[str]:
    """Result notes for the calibration; the cold coreml run is credited only when it was the one that calibrated."""
    notes = [f"calibration: {device} drained tier 0 (docs/adr) in {wall:.1f} s; "
             f"benchmark corpus is tier {tier} ({', '.join(tier_dirs)})"]
    if tier > 0 and cold_run_calibrated:
        notes.append("the cold coreml run drained the tier 0 corpus; only its compile numbers compare")
    return notes


def calibrate_tier(base_seconds: float, tier_bytes: Sequence[int], min_seconds: float) -> int:
    """The smallest corpus tier whose drain, projected by bytes from tier 0's, reaches min_seconds (else the last)."""
    for index, size in enumerate(tier_bytes):
        if base_seconds * size / tier_bytes[0] >= min_seconds:
            return index
    return len(tier_bytes) - 1


CACHE_HIT_COMPILER_CPU_S = 2.0


def reset_bank_state(root: Path) -> None:
    """Delete everything under a coreml data root except coreml-cache/, so the next server starts a fresh
    bank at the same absolute path (CoreML keys its ANE compile cache by the compiled model's path)."""
    for entry in Path(root).iterdir():
        if entry.name == "coreml-cache":
            continue
        if entry.is_dir() and not entry.is_symlink():
            shutil.rmtree(entry)
        else:
            entry.unlink()


def coreml_start_label(cold: bool, compiler_cpu_s: float | None) -> str:
    """cold, or a warm start judged by the ANE compiler CPU it cost: under 2 s is a cache hit."""
    if cold:
        return "cold"
    if compiler_cpu_s is None:
        return "warm (unknown)"
    return "warm (cache hit)" if compiler_cpu_s < CACHE_HIT_COMPILER_CPU_S else "warm (recompiled)"
