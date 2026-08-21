"""Tuning report generator (plan §7, §8, §10, WP5, gate G4).

Reads the tuned-parameters JSON (tune.py), the matrix CSV (matrix.py), the
corpora, and optionally the Optuna study DB, and renders
docs/work/2026-08-21-parameter-tuning-report.md with:

1. defaults-vs-best eval table (nDCG@5 / MRR@5 / hit@3 / hit@1 + the
   file-targeted vs non-file bucket breakdown),
2. the per-query regression table — queries where the tuned config regresses
   vs defaults on nDCG@5 (REQUIRED section, plan §7.1/§10; >5 regressions
   flags the config for owner review),
3. test-set 3-level grade deltas (good / could-be-improved / just-wrong;
   tuned grades come from a live evaluate() over test-set-10 when
   --live-server is given, or from a --test-grades JSON),
4. the matrix-influence summary (per knob: metric movement across its ladder
   from the matrix CSV).

The G4 discrimination floor lives here: check_eval_floor() fails a ranking
whose mean nDCG@5 is below the floor — a REVERSED result list fails what the
normal ranking passes (unit-tested).

Usage:
    python scripts/src/retrieval_tuning/report.py --matrix <matrix.csv> \
        --tuned <tuned-parameters.json> --corpora scripts/retrieval_tuning/corpora \
        [--live-server <bank-dir>] [--study-db <study.db>] [--out <report.md>]
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path
from typing import Optional

from retrieval_tuning.corpus import load_corpus
from retrieval_tuning.settings import KNOB_DEFAULTS

# Gate G4: the eval floor a config must pass (plan §7.1 discrimination proof).
DEFAULT_EVAL_FLOOR = {"mean_ndcg5": 0.5}

GRADE_SCORE = {"good": 2, "could-be-improved": 1, "just-wrong": 0}
GRADE_ORDER = ("good", "could-be-improved", "just-wrong")


# ---------------------------------------------------------------------------
# Eval floor (G4 discrimination)
# ---------------------------------------------------------------------------

def check_eval_floor(metrics, floor: Optional[dict] = None) -> list[str]:
    """Problems if any floor metric is below its threshold (empty = pass).

    Accepts a Metrics object or its as_dict() form. The discrimination proof
    (gate G4): a ranking with the target at rank 1 passes the default floor,
    the same list reversed (target at rank 5) fails it.
    """
    floor = dict(DEFAULT_EVAL_FLOOR if floor is None else floor)
    get = metrics.get if isinstance(metrics, dict) else (lambda k: getattr(metrics, k, None))
    problems: list[str] = []
    for name, threshold in sorted(floor.items()):
        value = get(name)
        if value is None:
            problems.append(f"metrics carry no '{name}' — floor cannot be evaluated")
        elif float(value) < float(threshold):
            problems.append(f"{name}={float(value):.4f} below the floor {float(threshold):.4f}")
    return problems


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------

def read_matrix_csv(path) -> list[dict]:
    """All rows of the matrix CSV (strings, as written by matrix.py)."""
    with open(path, newline="") as fh:
        return list(csv.DictReader(fh))


def parse_tuned_json(path) -> dict:
    """Load + validate the tuned-parameters JSON (all 9 knobs, eval section present)."""
    data = json.loads(Path(path).read_text())
    tuned = data.get("tuned")
    if not isinstance(tuned, dict):
        raise ValueError(f"{path}: no 'tuned' object in tuned-parameters JSON")
    missing = [k for k in KNOB_DEFAULTS if k not in tuned]
    if missing:
        raise ValueError(f"{path}: tuned config missing knobs {sorted(missing)}")
    if not isinstance(data.get("eval"), dict):
        raise ValueError(f"{path}: no 'eval' section in tuned-parameters JSON")
    return data


# ---------------------------------------------------------------------------
# Regression table (plan §7.1 / §10)
# ---------------------------------------------------------------------------

def _get(record, key):
    """Read a key off a per-query record — dict or QueryScore object."""
    if isinstance(record, dict):
        return record.get(key)
    return getattr(record, key, None)


def find_regressions(defaults_per_query, tuned_per_query) -> list[dict]:
    """Queries where the tuned config's nDCG@5 is STRICTLY below defaults' (plan §7.1).

    Returns rows sorted by delta (worst first): entry_id, default_ndcg5,
    tuned_ndcg5, delta_ndcg5. Queries missing from either side are skipped.
    """
    tuned_by_id = {_get(q, "entry_id"): q for q in tuned_per_query}
    regressions: list[dict] = []
    for dq in defaults_per_query:
        entry_id = _get(dq, "entry_id")
        tq = tuned_by_id.get(entry_id)
        if tq is None:
            continue
        default_ndcg5 = _get(dq, "ndcg5")
        tuned_ndcg5 = _get(tq, "ndcg5")
        if default_ndcg5 is None or tuned_ndcg5 is None:
            continue
        delta = float(tuned_ndcg5) - float(default_ndcg5)
        if delta < 0:
            regressions.append({
                "entry_id": entry_id,
                "default_ndcg5": float(default_ndcg5),
                "tuned_ndcg5": float(tuned_ndcg5),
                "delta_ndcg5": delta,
            })
    regressions.sort(key=lambda r: r["delta_ndcg5"])
    return regressions


# ---------------------------------------------------------------------------
# Test-set 3-level grades (plan §7.2)
# ---------------------------------------------------------------------------

def grade_from_rank(rank) -> str:
    """Map a target's rank to the 3-level grade (plan §5.2): 1-2 good, 3-5
    could-be-improved, missing from top-5 just-wrong."""
    if rank is None:
        return "just-wrong"
    return "good" if int(rank) <= 2 else "could-be-improved"


def grade_from_metrics(per_query) -> dict:
    """entry_id -> grade from a live evaluation's per-query records."""
    return {_get(q, "entry_id"): grade_from_rank(_get(q, "first_relevant_rank")) for q in per_query}


def grade_deltas(default_grades: dict, tuned_grades: dict) -> list[dict]:
    """Per-query grade rows: default grade, tuned grade, numeric delta (2/1/0 scale)."""
    rows = []
    for entry_id, tuned_grade in sorted(tuned_grades.items()):
        default_grade = default_grades.get(entry_id, "n/a")
        delta = None
        if default_grade in GRADE_SCORE and tuned_grade in GRADE_SCORE:
            delta = GRADE_SCORE[tuned_grade] - GRADE_SCORE[default_grade]
        rows.append({"entry_id": entry_id, "default": default_grade,
                     "tuned": tuned_grade, "delta": delta})
    return rows


def grade_delta_summary(rows: list[dict]) -> dict:
    """Counts of improved / worsened / unchanged rows (rows with n/a skipped)."""
    summary = {"improved": 0, "worsened": 0, "unchanged": 0}
    for row in rows:
        delta = row["delta"]
        if delta is None:
            continue
        if delta > 0:
            summary["improved"] += 1
        elif delta < 0:
            summary["worsened"] += 1
        else:
            summary["unchanged"] += 1
    return summary


# ---------------------------------------------------------------------------
# Bucket breakdown (file-targeted vs non-file, plan §7.1)
# ---------------------------------------------------------------------------

def bucket_breakdown(per_query, entries: list[dict]) -> dict:
    """Aggregate per-query records into the plan's two buckets.

    Bucket assignment comes from the corpus entries' nonFileTarget flag
    (unknown entry ids are skipped). Returns {bucket: {count, means, rates}}.
    """
    non_file_ids = {e.get("id") for e in entries if e.get("nonFileTarget")}
    known_ids = {e.get("id") for e in entries}
    buckets = {"file": [], "non-file": []}
    for q in per_query:
        entry_id = _get(q, "entry_id")
        if entry_id in non_file_ids:
            buckets["non-file"].append(q)
        elif entry_id in known_ids:
            buckets["file"].append(q)
    out: dict = {}
    for name, rows in buckets.items():
        if not rows:
            continue
        out[name] = {
            "count": len(rows),
            "mean_ndcg5": sum(float(_get(q, "ndcg5")) for q in rows) / len(rows),
            "mean_mrr5": sum(float(_get(q, "mrr5")) for q in rows) / len(rows),
            "hit3_rate": sum(float(_get(q, "hit3")) for q in rows) / len(rows),
            "hit1_rate": sum(float(_get(q, "hit1")) for q in rows) / len(rows),
        }
    return out


# ---------------------------------------------------------------------------
# Matrix influence summary (plan §3.3)
# ---------------------------------------------------------------------------

def matrix_influence_from_rows(rows: list[dict]) -> dict:
    """(dataset, knob) -> {ladder value: {mean_ndcg5, mean_mrr5, hit3_rate, hit1_rate}}.

    Baseline rows are excluded; per-knob sweeps hold every other knob at the
    explicit defaults (matrix.py contract).
    """
    influence: dict = {}
    for row in rows:
        if row.get("knob") == "baseline":
            continue
        key = (row.get("dataset", ""), row.get("knob", ""))
        point = influence.setdefault(key, {})
        point[row.get("value", "")] = {
            "mean_ndcg5": float(row["mean_ndcg5"]),
            "mean_mrr5": float(row["mean_mrr5"]),
            "hit3_rate": float(row["hit3_rate"]),
            "hit1_rate": float(row["hit1_rate"]),
        }
    return influence


def matrix_influence(csv_path) -> dict:
    """matrix_influence_from_rows over a matrix CSV file."""
    return matrix_influence_from_rows(read_matrix_csv(csv_path))


def _value_key(value):
    """Sort ladder values numerically when possible, else lexically."""
    try:
        return (0, float(value))
    except (TypeError, ValueError):
        return (1, str(value))


def matrix_influence_summary_from_rows(rows: list[dict]) -> list[dict]:
    """Per (dataset, knob): ladder values, ndcg5 movement, best ladder value."""
    influence = matrix_influence_from_rows(rows)
    summary: list[dict] = []
    for (dataset, knob), points in sorted(influence.items()):
        ordered = sorted(points.items(), key=lambda kv: _value_key(kv[0]))
        values = [value for value, _ in ordered]
        ndcg5 = [p["mean_ndcg5"] for _, p in ordered]
        mrr5 = [p["mean_mrr5"] for _, p in ordered]
        hit3 = [p["hit3_rate"] for _, p in ordered]
        hit1 = [p["hit1_rate"] for _, p in ordered]
        best_idx = max(range(len(ndcg5)), key=lambda i: ndcg5[i])
        summary.append({
            "dataset": dataset,
            "knob": knob,
            "values": values,
            "ndcg5_by_value": ndcg5,
            "mrr5_by_value": mrr5,
            "hit3_by_value": hit3,
            "hit1_by_value": hit1,
            "ndcg5_min": min(ndcg5),
            "ndcg5_max": max(ndcg5),
            "ndcg5_movement": max(ndcg5) - min(ndcg5),
            "mrr5_movement": max(mrr5) - min(mrr5),
            "hit3_movement": max(hit3) - min(hit3),
            "hit1_movement": max(hit1) - min(hit1),
            "ndcg5_best_value": values[best_idx],
        })
    return summary


def matrix_influence_summary(csv_path) -> list[dict]:
    """matrix_influence_summary_from_rows over a matrix CSV file."""
    return matrix_influence_summary_from_rows(read_matrix_csv(csv_path))


# ---------------------------------------------------------------------------
# Optuna study summary (optional section)
# ---------------------------------------------------------------------------

def study_summary(storage_url: str, study_name: Optional[str] = None) -> Optional[dict]:
    """The study's headline numbers from its sqlite storage (or None).

    With no study_name, the study with the most recorded trials wins. Any
    storage problem yields None — the report still renders without it.
    """
    import optuna  # noqa: PLC0415 — optional dependency for this section

    try:
        names = optuna.get_all_study_names(storage_url)
    except Exception:  # noqa: BLE001 — unreadable storage is not fatal for the report
        return None
    best_info = None
    for name in names:
        try:
            study = optuna.load_study(study_name=name, storage=storage_url)
        except Exception:  # noqa: BLE001
            continue
        trials = study.trials
        info = {
            "study_name": name,
            "n_trials": len(trials),
            "n_completed": sum(1 for t in trials if t.state == optuna.trial.TrialState.COMPLETE),
            "best_value": float(study.best_value) if study.best_trials else None,
            "sampler": type(study.sampler).__name__,
            "datetime_start": str(trials[0].datetime_start) if trials else None,
            "datetime_complete": str(trials[-1].datetime_complete) if trials else None,
        }
        if best_info is None or info["n_trials"] > best_info["n_trials"]:
            best_info = info
    return best_info


# ---------------------------------------------------------------------------
# Markdown render
# ---------------------------------------------------------------------------

def render_report(
    tuned: dict,
    matrix_rows: list[dict],
    eval_entries: Optional[list[dict]] = None,
    test_entries: Optional[list[dict]] = None,
    test_grades: Optional[dict] = None,
    study: Optional[dict] = None,
    floor: Optional[dict] = None,
) -> str:
    """Assemble the full tuning report markdown (plan §7/§10 sections)."""
    floor = dict(DEFAULT_EVAL_FLOOR if floor is None else floor)
    defaults_metrics = tuned["eval"]["defaults"]
    tuned_metrics = tuned["eval"]["tuned"]
    regressions = find_regressions(
        defaults_metrics.get("per_query", []), tuned_metrics.get("per_query", [])
    )
    lines: list[str] = []
    ap = lines.append

    ap("# Retrieval parameter tuning report")
    ap("")
    ap(f"- Run date: {tuned.get('run_date', 'n/a')}")
    ap(f"- Study: {tuned.get('study_id', 'n/a')} "
       f"(best trial {tuned.get('best_trial', {}).get('number', 'n/a')})")
    ap(f"- Eval corpus: {tuned.get('corpus', 'n/a')} "
       f"({tuned.get('corpus_size', '?')} queries), dataset {tuned.get('dataset', 'n/a')}")
    ap(f"- Drift check: {'PASS' if tuned.get('drift', {}).get('passed') else 'FAIL'}")

    ap("")
    ap("## 1. Defaults vs best (eval set)")
    ap("")
    ap("| metric | defaults | tuned | delta |")
    ap("|---|---|---|---|")
    for label, key in (("mean nDCG@5", "mean_ndcg5"), ("mean MRR@5", "mean_mrr5"),
                       ("hit@3 rate", "hit3_rate"), ("hit@1 rate", "hit1_rate")):
        d = float(defaults_metrics[key])
        t = float(tuned_metrics[key])
        ap(f"| {label} | {d:.4f} | {t:.4f} | {t - d:+.4f} |")
    if eval_entries:
        ap("")
        ap("Per-category breakdown (file-targeted vs non-file):")
        ap("")
        ap("| bucket | config | count | mean nDCG@5 | mean MRR@5 | hit@3 rate | hit@1 rate |")
        ap("|---|---|---|---|---|---|---|")
        for config_name, metrics in (("defaults", defaults_metrics), ("tuned", tuned_metrics)):
            buckets = bucket_breakdown(metrics.get("per_query", []), eval_entries)
            for bucket in ("file", "non-file"):
                b = buckets.get(bucket)
                if b is None:
                    continue
                ap(f"| {bucket} | {config_name} | {b['count']} | {b['mean_ndcg5']:.4f} "
                   f"| {b['mean_mrr5']:.4f} | {b['hit3_rate']:.4f} | {b['hit1_rate']:.4f} |")

    ap("")
    ap("## 2. Per-query regression table (tuned vs defaults, nDCG@5)")
    ap("")
    ap(f"{len(regressions)} of {len(defaults_metrics.get('per_query', []))} eval queries "
       f"regress at the tuned config.")
    query_text = {e.get("id"): e.get("query", "") for e in (eval_entries or [])}
    if not regressions:
        ap("")
        ap("_No query regresses — the tuned config is at least as good per query on nDCG@5._")
    else:
        ap("")
        ap("| entry_id | query | defaults nDCG@5 | tuned nDCG@5 | delta |")
        ap("|---|---|---|---|---|")
        for r in regressions:
            ap(f"| {r['entry_id']} | {query_text.get(r['entry_id'], '')} "
               f"| {r['default_ndcg5']:.4f} | {r['tuned_ndcg5']:.4f} | {r['delta_ndcg5']:+.4f} |")
        if len(regressions) > 5:
            ap("")
            ap("> **FLAG**: more than 5 eval queries regress on nDCG@5 vs defaults — "
               "owner review of this table is required before shipping (plan §10).")

    ap("")
    ap("## 3. Test-set grade deltas (3-level: good / could-be-improved / just-wrong)")
    ap("")
    if not test_grades:
        ap("_Tuned-config grades not evaluated — run report.py with --live-server "
           "(or --test-grades <json>) to fill this section._")
    else:
        curated = {e.get("id"): e for e in (test_entries or [])}
        default_grades = {eid: e.get("grade") for eid, e in curated.items() if e.get("grade")}
        default_grades.update(test_grades.get("defaults", {}))
        rows = grade_deltas(default_grades, test_grades.get("tuned", {}))
        summary = grade_delta_summary(rows)
        ap("| entry_id | query | default grade | tuned grade | delta |")
        ap("|---|---|---|---|---|")
        for r in rows:
            delta = f"{r['delta']:+d}" if r["delta"] is not None else "n/a"
            ap(f"| {r['entry_id']} | {curated.get(r['entry_id'], {}).get('query', '')} "
               f"| {r['default']} | {r['tuned']} | {delta} |")
        ap("")
        ap(f"Summary: **{summary['improved']} improved**, **{summary['worsened']} worsened**, "
           f"**{summary['unchanged']} unchanged**.")
        ap("")
        ap("> Caveat: curated default grades were recorded at the copy's INHERITED settings "
           "(fusion=true, structureAlpha=0.5 leak, plan §1); tuned grades are observed live "
           "at the explicit tuned config.")

    ap("")
    ap("## 4. Matrix influence summary (per knob, from the matrix CSV)")
    ap("")
    summary_rows = matrix_influence_summary_from_rows(matrix_rows)
    if not summary_rows:
        ap("_No matrix rows — nothing to summarize._")
    else:
        ap("Per dataset: each knob's mean nDCG@5 across its ladder, Δ (max − min) and the "
           "best ladder value (other knobs held at the explicit defaults).")
        current_dataset = None
        for row in summary_rows:
            if row["dataset"] != current_dataset:
                current_dataset = row["dataset"]
                ap("")
                ap(f"### Dataset: {current_dataset}")
                ap("")
                ap("| knob | ladder values | nDCG@5 by value | ΔnDCG5 | best value |")
                ap("|---|---|---|---|---|")
            values = ", ".join(row["values"])
            ndcg5 = ", ".join(f"{v:.3f}" for v in row["ndcg5_by_value"])
            ap(f"| {row['knob']} | {values} | {ndcg5} | {row['ndcg5_movement']:.4f} "
               f"| {row['ndcg5_best_value']} |")

    if study:
        ap("")
        ap("## 5. Study summary (optuna storage)")
        ap("")
        ap(f"- Study: {study.get('study_name', 'n/a')}; trials: {study.get('n_trials', '?')} "
           f"({study.get('n_completed', '?')} complete); sampler: {study.get('sampler', 'n/a')}")
        if study.get("best_value") is not None:
            ap(f"- Best objective value: {study['best_value']:.4f} "
               f"(mean nDCG@5 = {-study['best_value']:.4f})")
        if study.get("datetime_start"):
            ap(f"- Started: {study['datetime_start']}")
        if study.get("datetime_complete"):
            ap(f"- Completed: {study['datetime_complete']}")

    ap("")
    ap("## 6. Eval floor gate (G4 discrimination)")
    ap("")
    ap("- Floor: " + ", ".join(f"{k} >= {v:g}" for k, v in sorted(floor.items())))
    for label, metrics in (("Defaults config", defaults_metrics), ("Tuned config", tuned_metrics)):
        problems = check_eval_floor(metrics, floor)
        verdict = "PASS" if not problems else "FAIL (" + "; ".join(problems) + ")"
        ap(f"- {label}: {verdict}")

    ap("")
    ap("Sources: matrix CSV, tuned-parameters JSON, optuna study DB, corpora.")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _default_corpora_dir() -> str:
    return str(Path(__file__).resolve().parents[2] / "retrieval_tuning" / "corpora")


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Tuning report generator (plan §7/§10): defaults-vs-best, "
                    "regression table, test-set grade deltas, matrix influence.",
    )
    parser.add_argument("--matrix", required=True, help="matrix.csv from matrix.py")
    parser.add_argument("--tuned", required=True, help="tuned-parameters.json from tune.py")
    parser.add_argument("--corpora", default=_default_corpora_dir(),
                        help="Dir holding eval-set-100.json and test-set-10.json")
    parser.add_argument("--out", default="docs/work/2026-08-21-parameter-tuning-report.md",
                        help="Report markdown output path")
    parser.add_argument("--live-server", default=None,
                        help="Bank dir: evaluate tuned + defaults over test-set-10 for the "
                             "grade deltas (scratch copy, never the live bank)")
    parser.add_argument("--test-grades", default=None,
                        help="JSON {'defaults': {id: grade}, 'tuned': {id: grade}} — alternative "
                             "to --live-server")
    parser.add_argument("--study-db", default=None,
                        help="Optuna sqlite study DB for the study summary section")
    parser.add_argument("--binary", default="ai-raccoon",
                        help="ai-raccoon binary for the --live-server scratch server")
    parser.add_argument("--eval-floor", type=float, default=DEFAULT_EVAL_FLOOR["mean_ndcg5"],
                        help="Eval floor: tuned/defaults mean nDCG@5 must be >= this (G4)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)

    tuned = parse_tuned_json(args.tuned)
    matrix_rows = read_matrix_csv(args.matrix)

    eval_entries: Optional[list[dict]] = None
    test_entries: Optional[list[dict]] = None
    corpora = Path(args.corpora)
    eval_path = corpora / "eval-set-100.json"
    test_path = corpora / "test-set-10.json"
    if eval_path.exists():
        eval_entries = load_corpus(eval_path)
    if test_path.exists():
        test_entries = load_corpus(test_path)

    test_grades = None
    if args.test_grades:
        test_grades = json.loads(Path(args.test_grades).read_text())
    elif args.live_server:
        from retrieval_tuning.evaluate import evaluate  # noqa: PLC0415
        from retrieval_tuning.server import start_server  # noqa: PLC0415

        if not test_entries:
            print("--live-server needs test-set-10.json under --corpora", file=sys.stderr)
            return 2
        with start_server(args.live_server, binary=args.binary) as srv:
            print(f"[report] scratch server on port {srv.port} — evaluating test-set-10 "
                  f"({len(test_entries)} queries) at defaults and tuned configs", flush=True)
            defaults_metrics = evaluate(srv, tuned["defaults"], test_entries, binary=args.binary)
            tuned_metrics = evaluate(srv, tuned["tuned"], test_entries, binary=args.binary)
        test_grades = {
            "defaults": grade_from_metrics(defaults_metrics.per_query),
            "tuned": grade_from_metrics(tuned_metrics.per_query),
        }
        print(f"[report] live grades — defaults: {test_grades['defaults']}", flush=True)
        print(f"[report] live grades — tuned:   {test_grades['tuned']}", flush=True)

    study = study_summary(args.study_db, tuned.get("study_id")) if args.study_db else None
    md = render_report(
        tuned,
        matrix_rows,
        eval_entries=eval_entries,
        test_entries=test_entries,
        test_grades=test_grades,
        study=study,
        floor={"mean_ndcg5": args.eval_floor},
    )
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(md)
    print(f"[report] written to {out}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
