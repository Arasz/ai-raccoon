#!/usr/bin/env python3
"""Cross-device embedding benchmark: the same fixed corpus embedded through the real product on each
device (auto = WebGPU, cpu, mlx, coreml), measuring time to completion, SoC and whole-system energy,
and the server's peak memory.

Run it as yourself on an Apple Silicon Mac, on AC power, with other heavy work stopped. It asks for
the sudo password once, for powermetrics alone (skip with --no-power; system energy from the battery
telemetry needs no sudo).

Usage:
    python3 scripts/device-benchmark.py [--devices auto,cpu,mlx,coreml] [--repeats 3] [--idle-seconds 20]
        [--binary $(which ai-raccoon)] [--corpus-sha <sha>] [--out ~/ai-raccoon-device-benchmark/<ts>/]
        [--no-power] [--allow-busy] [--drain-timeout 1800]

Needs httpx (the MCP client): use the repo's .venv or `python3 -m pip install httpx`.
Writes <out>/result.json (schemaVersion 1), <out>/result.md, <out>/powermetrics.plist and one
directory per run under <out>/runs/. Never touches port 7721 or ~/.ai-raccoon.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import signal
import sqlite3
import subprocess
import sys
import tarfile
import threading
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "scripts" / "src"))
sys.path.insert(0, str(REPO / "scripts" / "retrieval_tuning"))

from device_benchmark import battery, power, protocol, result  # noqa: E402
from retrieval_tuning.coreml import compiler_cpu_delta, parse_ps_pids, parse_ps_time  # noqa: E402
from retrieval_tuning.process_memory import memory_kib  # noqa: E402
from retrieval_tuning.server import assert_port_not_7721, assert_safe_data_root, start_server  # noqa: E402
from run_code_eval import check_not_busy  # noqa: E402

# The docs/adr tree at the 1.53.0 release (main after #769): 118 ADRs, ~1.3 MB of markdown.
DEFAULT_CORPUS_SHA = "43138aa99e40be6989a3814692cc7dbf7ab0f9d3"
# Corpus tiers at the pinned sha: the benchmark uses the smallest one on which the fastest device's
# drain projects to at least MIN_WINDOW_S (tier 0 ~1.1 MB, tier 1 ~3.7 MB, tier 2 ~21 MB).
CORPUS_TIERS = (
    ("docs/adr",),
    ("docs/adr", "docs/plans", "docs/reviews", "docs/reference", "docs/research"),
    ("docs/adr", "docs/plans", "docs/reviews", "docs/reference", "docs/research", "docs/work"),
)
MIN_WINDOW_S = 30.0
PROJECT_ID = "device-benchmark"
WARMUP_PROJECT_ID = "device-benchmark-warmup"
COMPILER_NAMES = ("aned", "ANECompilerService")
NEURAL_ENGINE_TIMEOUT_S = 300.0
PUBLISH_WAIT_S = 75.0
POLL_S = 0.5


class BenchmarkError(RuntimeError):
    """The session cannot continue (unsafe environment, missing tool, broken corpus)."""


# ---------------------------------------------------------------------------------------------
# Small process helpers


def _run(argv: list[str], timeout: float = 600.0, check: bool = True) -> str:
    proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    if check and proc.returncode != 0:
        raise BenchmarkError(f"{' '.join(argv)} failed (exit {proc.returncode}): {proc.stderr.strip()[:800]}")
    return proc.stdout


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _cpu_seconds(pid: int) -> float | None:
    out = _run(["ps", "-o", "time=", "-p", str(pid)], check=False).strip()
    return parse_ps_time(out) if out else None


def _compiler_cpu() -> dict[int, float]:
    return parse_ps_pids(_run(["ps", "-A", "-o", "pid=,time=,comm="], check=False), COMPILER_NAMES)


def _processes_on_root(root: Path) -> list[str]:
    """ps lines of any process still holding this data root (a leaked settings backend included)."""
    out = _run(["ps", "-Ao", "pid,command"], check=False)
    return [line.strip() for line in out.splitlines() if str(root) in line and "ps -Ao" not in line]


# ---------------------------------------------------------------------------------------------
# Machine, product, corpus


def machine_info() -> dict:
    def sysctl(name: str) -> str:
        return _run(["sysctl", "-n", name], check=False).strip()

    batt = _run(["pmset", "-g", "batt"], check=False)
    settings = _run(["pmset", "-g"], check=False)
    low_power = next((line.split()[-1] for line in settings.splitlines() if "lowpowermode" in line), None)
    return {
        "chip": sysctl("machdep.cpu.brand_string"), "model": sysctl("hw.model"),
        "p_cores": sysctl("hw.perflevel0.physicalcpu"), "e_cores": sysctl("hw.perflevel1.physicalcpu"),
        "memsize_bytes": int(sysctl("hw.memsize") or 0),
        "macos": _run(["sw_vers", "-productVersion"], check=False).strip(),
        "macos_build": _run(["sw_vers", "-buildVersion"], check=False).strip(),
        "on_ac": "AC Power" in batt, "low_power_mode": low_power == "1", "pmset_batt": batt.strip(),
        "loadavg_start": list(os.getloadavg()),
    }


def _version(path: Path) -> str:
    """`--version` output; the CLI prints it on stderr."""
    proc = subprocess.run([str(path), "--version"], capture_output=True, text=True, timeout=60)
    return (proc.stdout.strip() or proc.stderr.strip()).splitlines()[-1]


def product_info(binary: str) -> dict:
    path = Path(shutil.which(binary) or binary)
    if not path.exists():
        raise BenchmarkError(f"--binary {binary} not found")
    real = path.resolve()
    info = {"binary": str(path), "binary_realpath": str(real), "binary_sha256": _sha256(real),
            "version": _version(path)}
    dll = real.parent / "AiRaccoon.dll"
    if dll.exists():
        info["dll_sha256"] = _sha256(dll)
    return info


def tree_bytes(sha: str, directory: str) -> int:
    """Total blob bytes under directory at sha."""
    out = _run(["git", "-C", str(REPO), "ls-tree", "-r", "-l", sha, directory])
    return sum(int(line.split()[3]) for line in out.splitlines() if line.split()[3].isdigit())


def extract_corpus(sha: str, dirs: tuple[str, ...], target: Path) -> dict:
    """`git archive <sha> <dirs>` unpacked under target, plus a per-file sha256 manifest."""
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    archive = subprocess.run(["git", "-C", str(REPO), "archive", "--format=tar", sha, *dirs],
                             capture_output=True, timeout=120)
    if archive.returncode != 0:
        raise BenchmarkError(f"git archive {sha} {' '.join(dirs)} failed: {archive.stderr.decode()[:400]} "
                             f"(fetch the pinned commit first: git fetch origin {sha})")
    with tarfile.open(fileobj=io.BytesIO(archive.stdout)) as tar:
        tar.extractall(target, filter="data")
    files = sorted(p for p in target.rglob("*") if p.is_file())
    manifest = [{"path": str(p.relative_to(target)), "bytes": p.stat().st_size, "sha256": _sha256(p)} for p in files]
    lines = "".join(f"{m['sha256']}  {m['path']}\n" for m in manifest)
    (target.parent / "corpus-manifest.json").write_text(json.dumps(manifest, indent=1))
    return {"git_sha": sha, "dirs": list(dirs), "files": len(manifest), "bytes": sum(m["bytes"] for m in manifest),
            "sha256": hashlib.sha256(lines.encode()).hexdigest(), "chunk_tokens": None}


# ---------------------------------------------------------------------------------------------
# Samplers: ioreg (1 Hz, no sudo), powermetrics (sudo), server footprint


class IoregSampler:
    """Polls AppleSmartBattery PowerTelemetryData at 1 Hz; readings carry wall-clock time."""

    def __init__(self) -> None:
        self.readings: list[battery.Reading] = []
        self.available = self._read() is not None
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    @staticmethod
    def _read() -> dict[str, int] | None:
        return battery.parse_telemetry(_run(["ioreg", "-rn", "AppleSmartBattery"], check=False))

    def _loop(self) -> None:
        while not self._stop.is_set():
            telemetry = self._read()
            if telemetry is not None:
                self.readings.append(battery.Reading(time.time(), telemetry))
            self._stop.wait(1.0)

    def start(self) -> None:
        if self.available:
            self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def publishes_after(self, t: float) -> list[float]:
        return [s.end for s in battery.segments(list(self.readings)) if s.end > t]

    def wait_publishes(self, after: float, count: int, timeout: float) -> float | None:
        """Block until `count` publishes land after `after`; the last one's time, or None on timeout."""
        if not self.available:
            return None
        deadline = time.time() + timeout
        while time.time() < deadline:
            ends = self.publishes_after(after)
            if len(ends) >= count:
                return ends[count - 1]
            time.sleep(0.25)
        return None


class PowerMetrics:
    """One session-long `sudo powermetrics` writing plist samples; sudo kept alive every 60 s."""

    def __init__(self, plist: Path) -> None:
        self.plist = plist
        self.proc: subprocess.Popen | None = None
        self._stop = threading.Event()

    def start(self) -> None:
        subprocess.run(["sudo", "-v"], check=True)
        self.proc = subprocess.Popen(["sudo", "-n", "powermetrics", "--samplers", "cpu_power,gpu_power,ane_power,thermal",
                                      "-i", "500", "--format", "plist", "-o", str(self.plist)],
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        threading.Thread(target=self._keepalive, daemon=True).start()
        time.sleep(2.0)

    def _keepalive(self) -> None:
        while not self._stop.wait(60.0):
            subprocess.run(["sudo", "-n", "-v"], check=False, capture_output=True)

    def stop(self) -> list[dict]:
        self._stop.set()
        if self.proc is None:
            return []
        subprocess.run(["sudo", "-n", "kill", "-INT", str(self.proc.pid)], check=False, capture_output=True)
        try:
            self.proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            subprocess.run(["sudo", "-n", "kill", "-KILL", str(self.proc.pid)], check=False, capture_output=True)
        subprocess.run(["sudo", "-n", "chmod", "a+r", str(self.plist)], check=False, capture_output=True)
        return power.split_samples(self.plist.read_bytes()) if self.plist.exists() else []


class FootprintPeak:
    """Samples another pid's phys and neural footprint every 0.5 s; keeps the peaks (KiB)."""

    def __init__(self, pid: int) -> None:
        self.pid = pid
        self.phys = 0
        self.neural = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                sample = memory_kib(self.pid)
            except OSError:
                return
            self.phys = max(self.phys, sample.footprint_peak, sample.footprint)
            self.neural = max(self.neural, sample.neural_footprint_peak, sample.neural_footprint)
            self._stop.wait(POLL_S)

    def __enter__(self) -> "FootprintPeak":
        self._thread.start()
        return self

    def __exit__(self, *exc: object) -> None:
        self._stop.set()
        self._thread.join(timeout=5)


# ---------------------------------------------------------------------------------------------
# One run


def _cli(binary: str, root: Path, port: int, *verb: str) -> str:
    assert_port_not_7721(port)
    return _run([binary, "--data-root", str(root), "--port", str(port), *verb], timeout=600)


def _pending(db: Path, project: str) -> int:
    conn = sqlite3.connect(f"file:{db.resolve()}?mode=ro", uri=True, timeout=5)
    try:
        return int(conn.execute("SELECT count(*) FROM entries WHERE embed_state = 'pending' AND project_id = ?",
                                (project,)).fetchone()[0])
    finally:
        conn.close()


def _chunks(db: Path, project: str) -> int:
    conn = sqlite3.connect(f"file:{db.resolve()}?mode=ro", uri=True, timeout=5)
    try:
        return int(conn.execute("SELECT count(*) FROM entries WHERE project_id = ?", (project,)).fetchone()[0])
    finally:
        conn.close()


def _stop_server(server, root: Path) -> None:
    pid = server.proc.pid
    server.stop()
    deadline = time.time() + 20
    while _alive(pid) and time.time() < deadline:
        time.sleep(0.25)
    if _alive(pid):
        raise BenchmarkError(f"server pid {pid} is still running after stop")
    leftovers = _processes_on_root(root)
    if leftovers:
        raise BenchmarkError(f"processes still hold {root}: {leftovers}")


class Session:
    def __init__(self, args: argparse.Namespace, out: Path, corpus: Path, ioreg: IoregSampler) -> None:
        self.args = args
        self.out = out
        self.corpus = corpus
        self.ioreg = ioreg
        # CoreML keys its ANE compile cache by the compiled model's absolute path, so every coreml run
        # shares this one root: a warm run wipes the bank and keeps coreml-cache/ in place.
        self.coreml_root = out / "coreml-root"

    def configure(self, root: Path, device: str, log_path: Path) -> None:
        """One-shot settings on the run's root through a setup server that is stopped before measuring."""
        server = start_server(root, binary=self.args.binary, log_path=log_path, idle_timeout="0")
        try:
            _cli(self.args.binary, root, server.port, "model", "embedding", "set", "local")
            _cli(self.args.binary, root, server.port, "settings", "model", "device", device)
            _cli(self.args.binary, root, server.port, "settings", "ingest", "scope", "add", "*", str(self.corpus))
        finally:
            _stop_server(server, root)

    def run(self, slot: protocol.Slot) -> dict:
        run_dir = self.out / "runs" / f"{slot.index:02d}-{slot.device}{'-cold' if slot.cold else ''}"
        if run_dir.exists():
            shutil.rmtree(run_dir)
        run_dir.mkdir(parents=True)
        if slot.device == "coreml":
            root = self.coreml_root
            if slot.cold and root.exists():
                shutil.rmtree(root)
            if root.exists():
                protocol.reset_bank_state(root)
            root.mkdir(parents=True, exist_ok=True)
        else:
            root = run_dir / "data-root"
            root.mkdir(parents=True)
        assert_safe_data_root(root)
        record: dict = {key: None for key in result.RUN_KEYS}
        record.update(loadavg_start=list(os.getloadavg()), data_root=str(root))

        self.configure(root, slot.device, run_dir / "setup.log")

        # Idle baseline, no server: SoC needs idle_seconds; system needs one whole publish segment.
        idle_start = time.time()
        time.sleep(self.args.idle_seconds)
        idle_end = time.time()
        if self.ioreg.available:
            second = self.ioreg.wait_publishes(idle_start, 2, 2 * PUBLISH_WAIT_S)
            idle_end = max(idle_end, second or time.time())
        record.update(idle_start=idle_start, idle_end=idle_end)

        server = start_server(root, binary=self.args.binary, log_path=run_dir / "serve.log", idle_timeout="0")
        try:
            record.update(self._measure(slot, server, root, run_dir))
        finally:
            _stop_server(server, root)
        return record

    def _measure(self, slot: protocol.Slot, server, root: Path, run_dir: Path) -> dict:
        pid = server.proc.pid
        record: dict = {"server_pid": pid}
        log_path = run_dir / "serve.log"

        # Warm-up: one search builds the engine (sessions are created lazily, on first embed).
        ready_start = time.time()
        compiler_before = _compiler_cpu()
        server.client.memory_search(WARMUP_PROJECT_ID, "warm up the embedding engine", limit=1)
        deadline = time.time() + 60
        provider = protocol.provider_from_log(log_path.read_text(errors="replace"))
        while provider is None and time.time() < deadline:
            time.sleep(POLL_S)
            provider = protocol.provider_from_log(log_path.read_text(errors="replace"))
        record["provider_actual"] = provider

        if slot.device == "coreml":
            refusal = protocol.coreml_refusal(provider)
            if refusal is not None:
                return {**record, "status": "refused", "reason": refusal}
            wait = protocol.wait_for_neural_engine(root / "coreml-cache" / "status.json", pid,
                                                   timeout=NEURAL_ENGINE_TIMEOUT_S)
            ready_end = time.time()
            logged = protocol.neural_engine_from_log(log_path.read_text(errors="replace"))
            record.update(neural_engine_ready_s=wait.seconds, neural_engine_how=logged[0] if logged else None,
                          ready_start=ready_start, ready_end=ready_end)
            compiled, _ = compiler_cpu_delta(compiler_before, _compiler_cpu())
            record["ready_compiler_cpu_s"] = compiled
            if wait.state == "refused":
                return {**record, "status": "refused", "reason": wait.reason}
            if wait.state == "timeout":
                return {**record, "status": "timeout", "reason": f"no NeuralEngineServing within {NEURAL_ENGINE_TIMEOUT_S:.0f} s"}
            record["provider_actual"] = "CoreML"
        status = protocol.provider_status(slot.device, record["provider_actual"])

        # Align t0 to a battery-telemetry publish so the covered span starts with the work.
        if self.ioreg.available:
            self.ioreg.wait_publishes(time.time(), 1, PUBLISH_WAIT_S)

        db = root / "memory.db"
        cpu_before = _cpu_seconds(pid)
        compiler_before = _compiler_cpu()
        with FootprintPeak(pid) as peak:
            t0 = time.time()
            ingest = server.client.ingest_directory(PROJECT_ID, str(self.corpus))
            ingest_returned = time.time()
            first_embedded = None
            total = _chunks(db, PROJECT_ID)
            deadline = t0 + self.args.drain_timeout
            while True:
                pending = _pending(db, PROJECT_ID)
                if first_embedded is None and pending < total:
                    first_embedded = time.time()
                if pending == 0:
                    break
                if time.time() > deadline:
                    return {**record, "status": "timeout", "reason": f"{pending} rows still pending after {self.args.drain_timeout:.0f} s"}
                time.sleep(POLL_S)
            t1 = time.time()
        cpu_after = _cpu_seconds(pid)
        compiled, vanished = compiler_cpu_delta(compiler_before, _compiler_cpu())

        tail_publish = None
        if self.ioreg.available:
            tail_publish = self.ioreg.wait_publishes(t1, 1, PUBLISH_WAIT_S)

        (run_dir / "ingest.json").write_text(json.dumps(ingest, indent=1, default=str))
        return {
            **record, "status": status, "t0": t0, "t1": t1, "wall_s": t1 - t0, "chunks": _chunks(db, PROJECT_ID),
            "ingest_s": ingest_returned - t0, "first_embed_after_ingest_s": None if first_embedded is None else first_embedded - ingest_returned,
            "server_cpu_s": None if cpu_before is None or cpu_after is None else cpu_after - cpu_before,
            "compiler_cpu_s": compiled, "compiler_pids_vanished": vanished,
            "phys_footprint_peak_kib": peak.phys, "neural_footprint_peak_kib": peak.neural,
            "tail_publish": tail_publish,
        }


# ---------------------------------------------------------------------------------------------
# Session


def _finalize(runs: list[dict], soc_samples: list[dict] | None, ioreg: IoregSampler) -> dict:
    segments = battery.segments(ioreg.readings) if ioreg.available else None
    compile_info: dict = {"cold_s": None, "cold_j": None, "compiler_cpu_s": None, "warm_ready_s": None}
    for run in runs:
        if run.get("t0") is not None and run.get("t1") is not None:
            run.update(result.energy_fields(t0=run["t0"], t1=run["t1"], idle_start=run["idle_start"],
                                            idle_end=run["idle_end"], chunks=run.get("chunks"),
                                            soc_samples=soc_samples, segments=segments))
            if soc_samples:
                run["thermal_pressure_max"] = power.thermal_pressure_max(soc_samples, run["t0"], run["t1"])
        if run["cold"] and run.get("ready_start") is not None:
            fields = result.energy_fields(t0=run["ready_start"], t1=run["ready_end"], idle_start=run["idle_start"],
                                          idle_end=run["idle_end"], chunks=None, soc_samples=soc_samples, segments=segments)
            compile_info.update(cold_s=run["neural_engine_ready_s"], cold_j=fields["system_energy_net_j"],
                                cold_soc_j=fields["soc_energy_net_j"], compiler_cpu_s=run.get("ready_compiler_cpu_s"),
                                how=run.get("neural_engine_how"))
    warm = [r["neural_engine_ready_s"] for r in runs if r["device"] == "coreml" and not r["cold"]
            and r.get("neural_engine_ready_s") is not None]
    if warm:
        compile_info["warm_ready_s"] = sorted(warm)[len(warm) // 2]
        compile_info["warm_how"] = sorted({r.get("neural_engine_how") for r in runs if r["device"] == "coreml" and not r["cold"]} - {None})
    return compile_info


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--devices", default="auto,cpu,mlx,coreml")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--idle-seconds", type=float, default=20.0)
    parser.add_argument("--binary", default=shutil.which("ai-raccoon") or "ai-raccoon")
    parser.add_argument("--corpus-sha", default=DEFAULT_CORPUS_SHA)
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--no-power", action="store_true", help="skip powermetrics (no sudo); system energy still runs")
    parser.add_argument("--allow-busy", action="store_true")
    parser.add_argument("--drain-timeout", type=float, default=1800.0)
    parser.add_argument("--no-calibrate", action="store_true", help="keep docs/adr even when the fastest drain is short")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if os.geteuid() == 0:
        print("refusing to run as root: run as yourself; only powermetrics is started under sudo", file=sys.stderr)
        return 2
    if sys.platform != "darwin":
        print("this benchmark measures Apple Silicon Macs only", file=sys.stderr)
        return 2
    devices = [d.strip() for d in args.devices.split(",") if d.strip()]
    unknown = [d for d in devices if d not in protocol.EXPECTED_PROVIDER]
    if unknown:
        print(f"unknown device(s): {unknown}", file=sys.stderr)
        return 2

    busy = check_not_busy(allow_busy=args.allow_busy)
    out = (args.out or Path.home() / "ai-raccoon-device-benchmark" / time.strftime("%Y%m%d-%H%M%S")).expanduser()
    out.mkdir(parents=True, exist_ok=True)
    notes: list[str] = []
    if busy:
        notes.append(f"--allow-busy: {len(busy)} competing process(es) at start")

    machine = machine_info()
    if not machine["on_ac"]:
        notes.append("not on AC power: system energy includes battery discharge")
    if machine["low_power_mode"]:
        notes.append("Low Power Mode is on")
    product = product_info(args.binary)
    args.binary = product["binary"]
    print(f"AiRaccoon {product['version']} at {product['binary_realpath']}", flush=True)

    caffeinate = subprocess.Popen(["caffeinate", "-i", "-w", str(os.getpid())])
    ioreg = IoregSampler()
    ioreg.start()
    if not ioreg.available:
        notes.append("no AppleSmartBattery PowerTelemetryData (desktop Mac?): system energy unavailable")
    meter = None if args.no_power else PowerMetrics(out / "powermetrics.plist")
    corpus_dir = out / "corpus"
    corpus = extract_corpus(args.corpus_sha, CORPUS_TIERS[0], corpus_dir)
    session = Session(args, out, corpus_dir, ioreg)
    runs: list[dict] = []
    soc_samples: list[dict] | None = None
    try:
        if meter is not None:
            meter.start()
        slots = protocol.schedule(devices, args.repeats)
        total_slots = len(slots)

        def run_one(slot: protocol.Slot) -> dict:
            label = "calibration" if slot.index < 0 else f"{slot.index + 1}/{total_slots}"
            print(f"[{label}] {slot.device}{' (cold)' if slot.cold else ''} repeat {slot.repeat}", flush=True)
            record = session.run(slot)
            print(f"    -> {record.get('status')} wall {record.get('wall_s')} provider {record.get('provider_actual')} "
                  f"chunks {record.get('chunks')}", flush=True)
            return record

        if not args.no_calibrate:
            # The cold coreml run doubles as the calibration run: its compile is what it measures, and its
            # drain is the fastest one this benchmark sees. Without coreml (or if it is refused), auto calibrates.
            if slots and slots[0].cold:
                runs = protocol.run_session(slots[:1], run_one)
                slots = slots[1:]
            calibrating = runs[0] if runs and runs[0].get("wall_s") is not None else None
            cold_run_calibrated = calibrating is not None
            if calibrating is None:
                calibrating = protocol.run_session([protocol.Slot(-1, -1, "auto", False)], run_one)[0]
            wall = calibrating.get("wall_s")
            if wall is None:
                raise BenchmarkError(f"calibration run did not drain: {calibrating.get('status')} {calibrating.get('reason')}")
            tier = protocol.calibrate_tier(wall, [sum(tree_bytes(args.corpus_sha, d) for d in dirs) for dirs in CORPUS_TIERS],
                                           min_seconds=MIN_WINDOW_S)
            if tier > 0:
                corpus = extract_corpus(args.corpus_sha, CORPUS_TIERS[tier], corpus_dir)
            corpus["calibration"] = {"device": calibrating["device"], "tier0_wall_s": wall, "tier": tier,
                                     "min_window_s": MIN_WINDOW_S}
            notes.extend(protocol.calibration_notes(calibrating["device"], cold_run_calibrated, wall, tier,
                                                    CORPUS_TIERS[tier]))
            shutil.rmtree(out / "runs" / "-1-auto", ignore_errors=True)

        runs += protocol.run_session(slots, run_one)
    finally:
        if meter is not None:
            soc_samples = meter.stop()
        ioreg.stop()
        caffeinate.send_signal(signal.SIGTERM)

    for run in runs:
        for key in result.RUN_KEYS:
            run.setdefault(key, None)
    protocol.mark_chunk_mismatch([r for r in runs if not r["cold"]])
    compile_info = _finalize(runs, soc_samples, ioreg)
    if ioreg.available:
        (out / "ioreg-readings.json").write_text(json.dumps([{"t": r.t, **r.telemetry} for r in ioreg.readings]))
    doc = result.build_result(
        machine=machine, product=product, corpus=corpus,
        power_sources={"soc": "off (--no-power)" if soc_samples is None else "powermetrics cpu/gpu/ane (sudo)",
                       "system": "AppleSmartBattery SystemLoad accumulator" if ioreg.available else "unavailable"},
        compile=compile_info, runs=runs, reference="auto", notes=notes)
    (out / "result.json").write_text(json.dumps(doc, indent=1, default=str))
    markdown = result.render_markdown(doc)
    (out / "result.md").write_text(markdown)
    print(markdown)
    print(f"results: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
