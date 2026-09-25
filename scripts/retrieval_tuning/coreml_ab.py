#!/usr/bin/env python3
"""CoreML EP A/B probe: subprocess-isolated device timing over a fixed seeded row set.

Builds a long set (memory-1022 chunks, 300-1022 tokens) and a short set (code-510 chunks) once,
writes them to <out>-rows.json so every config sees the identical rows, then times each config in
--configs for --repeats fresh subprocesses each, in a rotated interleave (coreml.interleave). Each
subprocess does one untimed warm-up pass over every row (compiling every CoreML bucket it touches)
before the timed pass. Writes one JSON: per-repeat row p50/p95, CPU-s, peak phys+neural footprint,
per-slot loadavg/competing-process counts, ANE compiler CPU-s delta (aned + ANECompilerService,
sampled around the slot's subprocess), and (CoreML configs only) sessions_live_peak/evictions off
the BucketSessions LRU and cache_disk_kib off the compiled-model cache dir. Plus the coreml.py
N-config range-separation summary (min/max/mean of cpu_s/p50/p95/footprint, plus beats and paired
within-rotation ratio of every config against --reference).

Usage:
    python3 coreml_ab.py --model-dir <granite dir> --configs configs.json --out out/ab.json \\
        [--repeats 3] [--long-n 400] [--short-n 400] [--seed N] [--reference NAME] \\
        [--dry-run 20] [--coreml-cache-dir <dir>]

configs.json: [{"name": str, "device": "cpu"|"mlx"|"coreml", "units": str|null, "step": int,
                "threads": int, "max_sessions": int, "cache_limit_mib": int|null,
                "mlx_dir": str|null}, ...]
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from retrieval_tuning import chunk_window, coreml  # noqa: E402
from retrieval_tuning.process_memory import memory_kib  # noqa: E402

REPO = Path(__file__).resolve().parents[2]

LONG_CHUNK_TOKENS = 1022
LONG_MIN_TOKENS = 300
LONG_MAX_TOKENS = 1022
SHORT_CHUNK_TOKENS = 510
DEFAULT_SET_SIZE = 400
DEFAULT_SEED = 20260925
COMPETING_NAMES = ("dotnet", "python", "python3")


def _percentile(values: list[float], p: float) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1))))]


def build_row_set(corpus: str, chunk_tokens: int, tokenizer, min_tokens: int, max_tokens: int,
                  n: int, seed: int) -> list[str]:
    """n seeded row texts from `corpus`'s chunks at `chunk_tokens`, filtered to [min_tokens, max_tokens]."""
    documents, _ = (chunk_window.memory_corpus if corpus == "memory" else chunk_window.code_corpus)(REPO)
    chunker = chunk_window.chunk_markdown if corpus == "memory" else chunk_window.chunk_code

    def count(text: str) -> int:
        return len(tokenizer.encode(text, add_special_tokens=False).ids)

    rows = [c.text for _, text in documents for c in chunker(text, chunk_tokens, count)
            if min_tokens <= count(c.text) <= max_tokens]
    return coreml.seeded_sample(rows, n, seed)


def competing_processes() -> dict[str, int]:
    """Other dotnet/python processes right now, from `ps -A` (this process's own python excluded)."""
    out = subprocess.run(["ps", "-A", "-o", "comm="], capture_output=True, text=True, check=False).stdout
    counts = {name: 0 for name in COMPETING_NAMES}
    for line in out.splitlines():
        name = line.strip().rsplit("/", 1)[-1]
        if name in counts:
            counts[name] += 1
    counts["python3"] = max(0, counts["python3"] - 1)  # this process
    return counts


def _run_one_config(args: argparse.Namespace) -> None:
    from tokenizers import Tokenizer

    configs = {c["name"]: c for c in json.loads(args.configs.read_text())}
    config = configs[args.one_config]
    rows = json.loads(args.rows.read_text())["rows"]

    step = config.get("step", 64)
    buckets: tuple[int, ...] = ()
    if config["device"] == "coreml":
        tokenizer = Tokenizer.from_file(str(args.model_dir / "tokenizer.json"))
        max_len = max(len(tokenizer.encode(r["text"], add_special_tokens=True).ids) for r in rows)
        top = max(step, -(-max_len // step) * step)
        buckets = tuple(coreml.bucket_lengths(step, top))

    cache_dir = (args.coreml_cache_dir / config["name"]) if config["device"] == "coreml" else None
    engine = chunk_window.Engine(
        args.model_dir, config.get("threads", 1), config["device"],
        mlx_dir=Path(config["mlx_dir"]) if config.get("mlx_dir") else None,
        pad_to=step if config["device"] != "coreml" else 0, buckets=buckets,
        cache_limit_mib=config.get("cache_limit_mib"), compute_units=config.get("units"),
        max_sessions=config.get("max_sessions", 0), coreml_cache_dir=cache_dir,
    )

    for row in rows:  # untimed warm-up: compiles/builds every bucket the timed pass will touch
        engine.embed(row["text"])

    row_ms: list[float] = []
    cpu0 = time.process_time()
    for row in rows:
        _, _, wall_ms, _ = engine.embed(row["text"])
        row_ms.append(wall_ms)
    cpu_s = time.process_time() - cpu0
    mem = memory_kib()
    is_coreml = config["device"] == "coreml"

    args.out.write_text(json.dumps({
        "config": config["name"], "repeat": args.one_repeat, "rows": len(rows),
        "p50_ms": _percentile(row_ms, 50), "p95_ms": _percentile(row_ms, 95), "cpu_s": cpu_s,
        "phys_kib_peak": mem.footprint_peak, "neural_kib_peak": mem.neural_footprint_peak,
        "sessions_live_peak": engine.sessions.peak_live if is_coreml else 0,
        "evictions": engine.sessions.evictions if is_coreml else 0,
        "cache_disk_kib": chunk_window.dir_size_kib(cache_dir) if is_coreml else 0,
    }))
    sys.stdout.flush()
    os._exit(0)  # mirrors run_chunk_window_eval.py: MLX/CoreML teardown can abort at interpreter exit


def _orchestrate(args: argparse.Namespace) -> None:
    from tokenizers import Tokenizer

    configs = json.loads(args.configs.read_text())
    tokenizer = Tokenizer.from_file(str(args.model_dir / "tokenizer.json"))
    long_rows = build_row_set("memory", LONG_CHUNK_TOKENS, tokenizer, LONG_MIN_TOKENS, LONG_MAX_TOKENS,
                              args.long_n, args.seed)
    short_rows = build_row_set("code", SHORT_CHUNK_TOKENS, tokenizer, 0, SHORT_CHUNK_TOKENS,
                               args.short_n, args.seed + 1)
    rows = [{"set": "long", "text": t} for t in long_rows] + [{"set": "short", "text": t} for t in short_rows]

    args.out.parent.mkdir(parents=True, exist_ok=True)
    rows_path = args.out.parent / f"{args.out.stem}-rows.json"
    rows_path.write_text(json.dumps({"rows": rows}))

    schedule = coreml.interleave([c["name"] for c in configs], args.repeats)
    per_config: dict[str, list[dict]] = {c["name"]: [] for c in configs}
    for repeat, name in schedule:
        slot = {"loadavg": os.getloadavg()[0], "competing": competing_processes()}
        slot_out = args.out.parent / f"{args.out.stem}-{name}-r{repeat}.json"
        print(f"slot repeat={repeat} config={name}", flush=True)
        compiler_before = chunk_window.ane_compiler_cpu_s()
        subprocess.run([sys.executable, __file__, "--model-dir", str(args.model_dir),
                        "--out", str(slot_out), "--configs", str(args.configs),
                        "--one-config", name, "--one-repeat", str(repeat), "--rows", str(rows_path),
                        *(["--coreml-cache-dir", str(args.coreml_cache_dir)] if args.coreml_cache_dir else [])],
                       check=True)
        slot["compiler_cpu_s"] = chunk_window.ane_compiler_cpu_s() - compiler_before
        slot_result = json.loads(slot_out.read_text())
        slot_result.update(slot)
        per_config[name].append(slot_result)

    names = [c["name"] for c in configs]
    reference = args.reference or names[0]
    summary = coreml.summarize_ab(names, per_config, reference)

    args.out.write_text(json.dumps({
        "configs": configs, "repeats": args.repeats, "seed": args.seed,
        "long_n": len(long_rows), "short_n": len(short_rows),
        "per_config": per_config, "summary": summary,
    }, indent=1))
    print(f"results: {args.out}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--configs", type=Path, required=True)
    parser.add_argument("--coreml-cache-dir", type=Path)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--long-n", type=int, default=DEFAULT_SET_SIZE)
    parser.add_argument("--short-n", type=int, default=DEFAULT_SET_SIZE)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--reference", help="reference config name for beats/paired-ratio (default: first --configs entry)")
    parser.add_argument("--dry-run", type=int, help="override long/short set size to N/2 rows each")
    parser.add_argument("--one-config", help=argparse.SUPPRESS)
    parser.add_argument("--one-repeat", type=int, help=argparse.SUPPRESS)
    parser.add_argument("--rows", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()

    if args.one_config:
        _run_one_config(args)
        return

    if args.dry_run:
        args.long_n = args.dry_run // 2
        args.short_n = args.dry_run - args.long_n

    _orchestrate(args)


if __name__ == "__main__":
    main()
