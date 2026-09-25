#!/usr/bin/env python3
"""Chunk size vs attention window eval: one fresh process per (corpus, chunk budget) arm.

Usage:
    python3 run_chunk_window_eval.py --model-dir <granite dir with model_fp16.onnx_data> \\
        --out <dir> [--memory 128,254,510,1022] [--code 128,254,510,1022] [--threads N] \\
        [--device cpu|mlx|coreml --mlx-dir <folder with libonnxruntime_mlx_ep.dylib>] \\
        [--pad-to N | --buckets a,b,..] [--cache-limit-mib N] \\
        [--compute-units CPUAndNeuralEngine --top-bucket 1024 --max-sessions N \\
         --coreml-cache-dir <dir> --profile-compute-plan --specialization FastPrediction]

Each arm runs in its own process so its peak RSS is its own. Results land in
<out>/<corpus>-<tokens>.json; `--report` prints the comparison tables from those files.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from retrieval_tuning import chunk_window, coreml  # noqa: E402

REPO = Path(__file__).resolve().parents[2]


def _sizes(value: str) -> list[int]:
    return [int(v) for v in value.split(",") if v]


def _run_one(args: argparse.Namespace) -> None:
    buckets = coreml.bucket_lengths(args.pad_to, args.top_bucket) if args.device == "coreml" else args.buckets
    result = chunk_window.run_arm(REPO, args.model_dir, args.one_corpus, args.one_tokens, args.threads,
                                  args.out / "banks", args.device, args.mlx_dir, args.pad_to, buckets,
                                  args.cache_limit_mib, args.compute_units, args.max_sessions,
                                  args.coreml_cache_dir, args.profile_compute_plan, args.specialization)
    (args.out / f"{args.one_corpus}-{args.one_tokens}.json").write_text(chunk_window.to_json(result))
    for bank in (args.out / "banks").glob(f"{args.one_corpus}-{args.one_tokens}.db*"):
        bank.unlink()


def _report(out: Path) -> None:
    rows = {p.stem: json.loads(p.read_text()) for p in sorted(out.glob("*-*.json"))}
    for corpus, baseline in (("memory", "memory-254"), ("code", "code-510")):
        arms = sorted((r for r in rows.values() if r["corpus"] == corpus), key=lambda r: r["chunk_tokens"])
        if not arms:
            continue
        print(f"\n## {corpus}")
        print("| tokens | chunks | >128 | file R@5 | file MRR@10 | span R@5 | hybrid file R@5 | "
              "ingest s | ingest CPU s | embed ms/chunk | ms/1k tok | peak RSS MiB | peak footprint MiB | disk KiB | query ms |")
        for r in arms:
            v, h = r["vector"], r["hybrid"]
            print(f"| {r['chunk_tokens']} | {r['chunks']} | {r['chunks_over_local_window']:.0%} | "
                  f"{v['file_recall5']:.3f} | {v['file_mrr10']:.3f} | {v['span_recall5']:.3f} | "
                  f"{h['file_recall5']:.3f} | {r['ingest_wall_s']:.0f} | {r['ingest_cpu_s']:.0f} | {r['embed_ms']['mean']:.0f} | "
                  f"{r['embed_ms_per_1k_tokens']:.0f} | {r['rss_kib_peak_total'] / 1024:.0f} | "
                  f"{r.get('footprint_kib_peak_total', r['rss_kib_peak_total']) / 1024:.0f} | "
                  f"{r['disk_bytes'] / 1024:.0f} | {r['query_ms']['mean']:.1f} |")
        base = rows.get(baseline)
        if base is None:
            continue
        for r in arms:
            if r is base:
                continue
            for metric in ("file_mrr10", "span_mrr10"):
                ci = chunk_window.paired_bootstrap([q["vector"][metric] for q in base["per_query"]],
                                                   [q["vector"][metric] for q in r["per_query"]])
                print(f"{corpus} {r['chunk_tokens']} vs {base['chunk_tokens']} {metric}: "
                      f"{ci[0]:+.3f}..{ci[1]:+.3f}..{ci[2]:+.3f}")
        for r in arms:
            if r["gold_position"]:
                print(r["chunk_tokens"], chunk_window.position_buckets(r["gold_position"]))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--memory", type=_sizes, default=[128, 254, 510, 1022])
    parser.add_argument("--code", type=_sizes, default=[128, 254, 510, 1022])
    parser.add_argument("--threads", type=int, default=max(1, (os.cpu_count() or 2) // 2))
    parser.add_argument("--device", choices=("cpu", "mlx", "coreml"), default="cpu")
    parser.add_argument("--mlx-dir", type=Path)
    parser.add_argument("--buckets", type=_sizes, default=[], help="pad each row to the smallest listed length that fits")
    parser.add_argument("--cache-limit-mib", type=int, help="MLX free-buffer cache limit (mlx_set_cache_limit)")
    parser.add_argument("--pad-to", type=int, default=0,
                        help="pad each row to a multiple of N tokens (0: no padding, as the product); "
                             "for --device coreml this is the bucket step and must be > 0")
    parser.add_argument("--compute-units", help="CoreML MLComputeUnits, e.g. CPUAndNeuralEngine")
    parser.add_argument("--top-bucket", type=int, default=1024, help="CoreML: largest length bucket; rows above it raise")
    parser.add_argument("--max-sessions", type=int, default=0, help="CoreML: live per-bucket session cap (0: unbounded)")
    parser.add_argument("--coreml-cache-dir", type=Path, help="CoreML: root for the per-bucket compiled-model cache")
    parser.add_argument("--profile-compute-plan", action="store_true", help="CoreML: log per-device op counts (ProfileComputePlan)")
    parser.add_argument("--specialization", help="CoreML: SpecializationStrategy provider option, e.g. FastPrediction")
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--one-corpus", help=argparse.SUPPRESS)
    parser.add_argument("--one-tokens", type=int, help=argparse.SUPPRESS)
    args = parser.parse_args()

    if args.report:
        _report(args.out)
        return
    if args.model_dir is None:
        parser.error("--model-dir is required to run arms")
    if args.device == "coreml":
        if not args.pad_to:
            parser.error("--device coreml needs --pad-to > 0 (the bucket step)")
        if not args.compute_units:
            parser.error("--device coreml needs --compute-units")
        if args.coreml_cache_dir is None:
            parser.error("--device coreml needs --coreml-cache-dir")
    args.out.mkdir(parents=True, exist_ok=True)
    if args.one_corpus:
        _run_one(args)
        # MLX's static destructors can abort at interpreter teardown (recursive_mutex); the result is already written.
        sys.stdout.flush()
        os._exit(0)
    for corpus, sizes in (("memory", args.memory), ("code", args.code)):
        for tokens in sizes:
            print(f"arm {corpus}-{tokens}", flush=True)
            subprocess.run([sys.executable, __file__, "--model-dir", str(args.model_dir), "--out", str(args.out),
                            "--threads", str(args.threads), "--one-corpus", corpus, "--one-tokens", str(tokens),
                            "--device", args.device, "--pad-to", str(args.pad_to),
                            "--top-bucket", str(args.top_bucket), "--max-sessions", str(args.max_sessions),
                            *(["--buckets", ",".join(map(str, args.buckets))] if args.buckets else []),
                            *(["--cache-limit-mib", str(args.cache_limit_mib)] if args.cache_limit_mib is not None else []),
                            *(["--mlx-dir", str(args.mlx_dir)] if args.mlx_dir else []),
                            *(["--compute-units", args.compute_units] if args.compute_units else []),
                            *(["--coreml-cache-dir", str(args.coreml_cache_dir)] if args.coreml_cache_dir else []),
                            *(["--profile-compute-plan"] if args.profile_compute_plan else []),
                            *(["--specialization", args.specialization] if args.specialization else [])],
                           check=True)
    _report(args.out)


if __name__ == "__main__":
    main()
