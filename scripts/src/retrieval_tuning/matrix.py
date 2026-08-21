"""One-parameter-at-a-time retrieval sweep driver (plan §3.1, §8, WP1).

For each of the 9 retrieval knobs, sweeps a fixed ladder of values with every
other knob held at the explicit defaults, evaluates every config over both
datasets (sextant bank, memory-db copy), and writes a matrix CSV with
per-config mean nDCG@5 / MRR@5 / hit@3 / hit@1, per-query records and the
per-category breakdown.

The baseline is an EXPLICIT write of all 9 defaults to the scratch server's
settings — never whatever the bank copy inherited (plan §1 settings leak).

Harness interface (plan §8, written by another lane):

    retrieval_tuning.evaluate(server, settings_dict, corpus_entries, ...) -> Metrics
    retrieval_tuning.server.start_server(data_root, binary=...) -> ScratchServer
    retrieval_tuning.corpus.load_corpus(path) -> list[dict]

Metrics carries mean_ndcg5, mean_mrr5, hit3_rate, hit1_rate, per_query,
per_category (see scoring.py). evaluate applies the settings itself via the
server's port, so a trial is exactly one evaluate() call.

Exit codes: 0 = sweep complete and valid; 1 = any knob's sweep is empty or the
baseline row is missing (gate G3); 2 = usage error.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import sys

# ---------------------------------------------------------------------------
# Ladder definitions (plan §3.1 — default marked bold in the plan; the default
# value is a ladder point for every knob).
# ---------------------------------------------------------------------------

DEFAULTS: dict = {
    "rrfK": 60,
    "ftsWeight": 1,
    "vectorWeight": 1,
    "sourceLambda": 0.1,
    "consolidationThreshold": 0.1,
    "docScoreFormula": "max",
    "candidateWindow": "max3x100",
    "structureAlpha": 0.5,
    "fusion": False,
}

LADDERS: dict = {
    "rrfK": [1, 5, 15, 60, 120, 200],
    "ftsWeight": [0, 1, 2, 3, 5, 10],
    "vectorWeight": [0, 1, 2, 3, 5, 10],
    "sourceLambda": [0, 0.05, 0.1, 0.2, 0.3, 0.5],
    "consolidationThreshold": [0, 0.05, 0.1, 0.2, 0.5, 1.0],
    "docScoreFormula": ["max", "sum"],
    "candidateWindow": ["max3x100", "max5x50"],
    "structureAlpha": [0, 0.25, 0.5, 0.75, 1.0],
    "fusion": [False, True],
}

CSV_COLUMNS = [
    "config_id", "knob", "value", "dataset", "corpus_size",
    "mean_ndcg5", "mean_mrr5", "hit3_rate", "hit1_rate",
    "per_query_json", "per_category_json",
]


def build_configs() -> list[dict]:
    """Enumerate the baseline + one config per ladder point (42 configs).

    Each config is a full settings dict; non-swept knobs are always the
    explicit defaults, so a config never inherits bank state.
    """
    configs: list[dict] = [{
        "id": "baseline",
        "knob": "baseline",
        "value": "default",
        "settings": dict(DEFAULTS),
    }]
    for knob, ladder in LADDERS.items():
        for value in ladder:
            settings = dict(DEFAULTS)
            settings[knob] = value
            configs.append({
                "id": f"{knob}={value}",
                "knob": knob,
                "value": value,
                "settings": settings,
            })
    return configs


def metrics_to_row(metrics) -> dict:
    """Normalize a harness Metrics object into one matrix CSV row.

    Fails loud (KeyError naming the attribute) rather than silently writing
    empty metrics — a sweep row without a metric is a corrupted matrix.
    """
    for name in ("mean_ndcg5", "mean_mrr5", "hit3_rate", "hit1_rate",
                 "per_query", "per_category"):
        if not hasattr(metrics, name):
            raise KeyError(f"Metrics object lacks '{name}' (harness interface drift?)")
    return {
        "mean_ndcg5": metrics.mean_ndcg5,
        "mean_mrr5": metrics.mean_mrr5,
        "hit3_rate": metrics.hit3_rate,
        "hit1_rate": metrics.hit1_rate,
        "per_query_json": json.dumps(
            [q.as_dict() if hasattr(q, "as_dict") else q for q in metrics.per_query],
            default=str,
        ),
        "per_category_json": json.dumps(metrics.per_category, default=str),
    }


def write_matrix_csv(rows: list[dict], path: str) -> None:
    """Write sweep rows (config_id..per_category_json) to a CSV."""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=CSV_COLUMNS)
        writer.writeheader()
        for row in rows:
            writer.writerow({col: row[col] for col in CSV_COLUMNS})


def validate_sweep(rows: list[dict], ladders: dict) -> list[str]:
    """Return the problems that must make the driver exit non-zero (gate G3).

    Problems: baseline row missing; a knob with no recorded rows (empty
    sweep); a ladder point with no recorded row for its knob.
    """
    problems: list[str] = []
    if not any(r["knob"] == "baseline" for r in rows):
        problems.append("baseline row missing from the sweep output")
    for knob, ladder in ladders.items():
        knob_rows = [r for r in rows if r["knob"] == knob]
        if not knob_rows:
            problems.append(f"empty sweep for knob '{knob}'")
            continue
        recorded = {r["value"] for r in knob_rows}
        for value in ladder:
            if value not in recorded:
                problems.append(f"knob '{knob}' missing ladder point {value!r}")
    return problems


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="One-parameter-at-a-time retrieval sweep over both datasets.",
    )
    parser.add_argument("--datasets", nargs="+", default=None,
                        help="Bank dirs to evaluate over (sextant bank, memory-db copy)")
    parser.add_argument("--corpora", nargs="+", default=None,
                        help="Corpus JSON per dataset (parallel to --datasets)")
    parser.add_argument("--out", default=None, help="Matrix CSV output path")
    parser.add_argument("--binary", default="ai-raccoon",
                        help="ai-raccoon binary for scratch servers (must expose the 9 settings verbs)")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print the config list and exit without touching a server")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)
    if args.dry_run:
        for cfg in build_configs():
            print(cfg["id"], cfg["settings"])
        return 0
    missing = [flag for flag, value in
               (("--datasets", args.datasets), ("--corpora", args.corpora), ("--out", args.out))
               if value is None]
    if missing:
        print(f"usage error: missing required arguments: {', '.join(missing)}", file=sys.stderr)
        return 2
    return run_sweep(args)


def run_sweep(args: argparse.Namespace) -> int:
    """Live sweep over both datasets. Imports the harness lazily (plan §8)."""
    from retrieval_tuning.corpus import load_corpus  # noqa: PLC0415
    from retrieval_tuning.evaluate import evaluate  # noqa: PLC0415
    from retrieval_tuning.server import start_server  # noqa: PLC0415

    if len(args.datasets) != len(args.corpora):
        print("--datasets and --corpora must list the same number of entries", file=sys.stderr)
        return 2

    configs = build_configs()
    rows: list[dict] = []
    for dataset, corpus_path in zip(args.datasets, args.corpora):
        entries = load_corpus(corpus_path)
        print(f"dataset {dataset}: {len(entries)} corpus entries")
        with start_server(dataset, binary=args.binary) as srv:
            for cfg in configs:
                metrics = evaluate(srv, cfg["settings"], entries, binary=args.binary)
                row = {
                    "config_id": cfg["id"],
                    "knob": cfg["knob"],
                    "value": cfg["value"],
                    "dataset": os.path.basename(dataset),
                    "corpus_size": len(entries),
                    **metrics_to_row(metrics),
                }
                rows.append(row)
                print(f"{cfg['id']:>14} {os.path.basename(dataset):>10} "
                      f"ndcg5={metrics.mean_ndcg5:.4f} mrr5={metrics.mean_mrr5:.4f} "
                      f"hit3={metrics.hit3_rate:.4f} hit1={metrics.hit1_rate:.4f}", flush=True)

    write_matrix_csv(rows, args.out)
    problems = validate_sweep(rows, LADDERS)
    if problems:
        for p in problems:
            print(f"SWEEP INVALID: {p}", file=sys.stderr)
        return 1
    print(f"matrix written to {args.out}: {len(rows)} rows, all knobs swept, baseline present")
    return 0


if __name__ == "__main__":
    sys.exit(main())
