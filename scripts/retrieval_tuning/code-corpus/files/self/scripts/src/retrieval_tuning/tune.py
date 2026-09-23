"""Optuna study runner over the 9 retrieval knobs (plan §6.3, §8, §10, WP5, gate G5).

Objective: mean nDCG@5 over the eval corpus (maximize; Optuna minimizes ->
negate). TPESampler(seed=42, n_startup_trials=10); the defaults config is
enqueued as trial 0 so every run measures the baseline in-study. The study
persists to a sqlite storage (crash-safe resume, plan §6.2) under
/tmp/continue-testing-algorithm/runs/<date>/study.db.

A drift check (plan §8.2, gate G5) re-runs the defaults config at session
start and end and asserts identical metrics — a change means the corpus
drifted mid-study and the run is invalid.

Every trial writes ALL NINE knobs explicitly through settings.py — the
harness never relies on inherited bank settings rows (the memory-db copy
inherits fusion=true + structureAlpha=0.5 from the live bank, plan §1).

Output: docs/work/2026-08-21-tuned-parameters.json (all 9 tuned values +
eval metrics + study id + run date); the full study stays in scratch.

Usage:
    python scripts/src/retrieval_tuning/tune.py --data-root <bank-dir> \
        --corpus scripts/retrieval_tuning/corpora/eval-set-100.json \
        --trials 50 --binary <bin>
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime
from pathlib import Path
from typing import Optional

import optuna
from optuna.samplers import TPESampler

from retrieval_tuning.report import find_regressions
from retrieval_tuning.settings import KNOB_DEFAULTS

DEFAULTS = dict(KNOB_DEFAULTS)

# Plan §6.3 search space, as the plan table states it.
SEARCH_SPACE: dict = {
    "rrfK": {"kind": "int", "low": 5, "high": 200, "log": True},
    "ftsWeight": {"kind": "int", "low": 0, "high": 8},
    "vectorWeight": {"kind": "int", "low": 0, "high": 8},
    "sourceLambda": {"kind": "float", "low": 0.0, "high": 0.5},
    "consolidationThreshold": {"kind": "float", "low": 0.0, "high": 0.5},
    "docScoreFormula": {"kind": "categorical", "choices": ["max", "sum"]},
    "candidateWindow": {"kind": "categorical", "choices": ["max3x100", "max5x50"]},
    "structureAlpha": {"kind": "float", "low": 0.0, "high": 1.0},
    "fusion": {"kind": "categorical", "choices": [False, True]},
}

INT_KNOBS = ("rrfK", "ftsWeight", "vectorWeight")
FLOAT_KNOBS = ("sourceLambda", "consolidationThreshold", "structureAlpha")
_CATEGORICAL_CHOICES = {
    "docScoreFormula": ("max", "sum"),
    "candidateWindow": ("max3x100", "max5x50"),
    "fusion": (False, True),
}


def suggest_params(trial) -> dict:
    """Define-by-run: suggest all 9 knobs for one Optuna trial (plan §6.3)."""
    params: dict = {}
    for knob, spec in SEARCH_SPACE.items():
        kind = spec["kind"]
        if kind == "int":
            params[knob] = trial.suggest_int(knob, spec["low"], spec["high"], log=bool(spec.get("log", False)))
        elif kind == "float":
            params[knob] = trial.suggest_float(knob, spec["low"], spec["high"])
        else:
            params[knob] = trial.suggest_categorical(knob, spec["choices"])
    return params


def _coerce_bool(value) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() in {"true", "1", "yes", "on"}
    if isinstance(value, (int, float)):
        return bool(value)
    raise ValueError(f"cannot coerce {value!r} to bool for the fusion knob")


def params_to_settings(params: dict) -> dict:
    """Coerce a trial's params into the settings dict evaluate() expects.

    All 9 knobs must be present — a missing knob would silently fall back to
    inherited bank state (the settings leak), so it is a hard error. Types
    are normalized (Optuna reloads from sqlite storage may yield strings or
    ints where bools/floats are needed).
    """
    missing = [k for k in SEARCH_SPACE if k not in params]
    if missing:
        raise ValueError(
            f"trial params missing knobs {sorted(missing)} — every knob must be written explicitly"
        )
    settings: dict = {}
    for knob in INT_KNOBS:
        settings[knob] = int(round(float(params[knob])))
    for knob in FLOAT_KNOBS:
        settings[knob] = float(params[knob])
    for knob, choices in _CATEGORICAL_CHOICES.items():
        value = params[knob]
        if knob == "fusion":
            settings[knob] = _coerce_bool(value)
            continue
        value = str(value)
        if value not in choices:
            raise ValueError(f"knob {knob}: unknown value {value!r} (expected {list(choices)})")
        settings[knob] = value
    return settings


def metrics_snapshot(metrics) -> tuple:
    """The deterministic fingerprint of one evaluation (drift comparison, plan §8.2)."""
    return (
        round(float(metrics.mean_ndcg5), 9),
        round(float(metrics.mean_mrr5), 9),
        round(float(metrics.hit3_rate), 9),
        round(float(metrics.hit1_rate), 9),
        tuple(
            (q.entry_id, round(float(q.ndcg5), 9), q.first_relevant_rank)
            for q in metrics.per_query
        ),
    )


def drift_problems(start, end) -> list[str]:
    """Problems when the session-start and session-end defaults runs differ (gate G5).

    Empty list = pass. The comparison covers the aggregate means AND the
    per-query outcomes, so a reshuffle that preserves the mean still fails.
    """
    if metrics_snapshot(start) == metrics_snapshot(end):
        return []
    problems: list[str] = []
    start_means = (start.mean_ndcg5, start.mean_mrr5, start.hit3_rate, start.hit1_rate)
    end_means = (end.mean_ndcg5, end.mean_mrr5, end.hit3_rate, end.hit1_rate)
    if start_means != end_means:
        problems.append(
            f"defaults metrics drifted: start ndcg5={start.mean_ndcg5:.4f} -> end {end.mean_ndcg5:.4f}"
        )
    start_pq = {(q.entry_id, round(float(q.ndcg5), 9), q.first_relevant_rank) for q in start.per_query}
    end_pq = {(q.entry_id, round(float(q.ndcg5), 9), q.first_relevant_rank) for q in end.per_query}
    if start_pq != end_pq:
        problems.append("defaults per-query outcomes drifted between session start and end")
    return problems


def build_tuned_output(
    study_id: str,
    run_date: str,
    n_trials: int,
    corpus_name: str,
    corpus_size: int,
    dataset: str,
    defaults_metrics: dict,
    tuned_metrics: dict,
    tuned_params: dict,
    n_regressed_queries: int,
    best_trial: dict,
    drift: dict,
) -> dict:
    """The tuned-parameters JSON payload (plan §10: 9 values + eval metrics + study id + date)."""
    improvement = float(tuned_metrics["mean_ndcg5"]) - float(defaults_metrics["mean_ndcg5"])
    return {
        "study_id": study_id,
        "run_date": run_date,
        "n_trials": n_trials,
        "corpus": corpus_name,
        "corpus_size": corpus_size,
        "dataset": dataset,
        "defaults": dict(DEFAULTS),
        "tuned": tuned_params,
        "eval": {
            "defaults": defaults_metrics,
            "tuned": tuned_metrics,
            "improvement_ndcg5": round(improvement, 9),
        },
        "n_regressed_queries": n_regressed_queries,
        "drift": drift,
        "best_trial": best_trial,
    }


def storage_db_path(storage_url: str) -> str:
    """The filesystem path of a sqlite:/// storage URL ('' when not sqlite)."""
    prefix = "sqlite:///"
    if not storage_url.startswith(prefix):
        return ""
    return storage_url[len(prefix):]


def run_study(
    data_root: str,
    corpus_path: str,
    n_trials: int,
    storage: str,
    study_name: str,
    binary: str,
    out_path: str,
    limit: Optional[int] = None,
) -> int:
    """Run the Optuna study end-to-end on a scratch server (plan §10)."""
    from retrieval_tuning.corpus import load_corpus  # noqa: PLC0415
    from retrieval_tuning.evaluate import evaluate  # noqa: PLC0415
    from retrieval_tuning.server import start_server  # noqa: PLC0415

    entries = load_corpus(corpus_path)
    if limit is not None:
        entries = entries[:limit]
    corpus_name = os.path.basename(corpus_path)
    print(f"[tune] corpus {corpus_name}: {len(entries)} entries; data-root {data_root}; "
          f"trials {n_trials}; study {study_name}", flush=True)

    db_path = storage_db_path(storage)
    if db_path:
        os.makedirs(os.path.dirname(os.path.abspath(db_path)), exist_ok=True)

    with start_server(data_root, binary=binary) as srv:
        print(f"[tune] scratch server bound to port {srv.port} (never 7721)", flush=True)
        drift_start = evaluate(srv, DEFAULTS, entries, binary=binary)
        print(f"[tune] drift check start: defaults ndcg5={drift_start.mean_ndcg5:.4f} "
              f"mrr5={drift_start.mean_mrr5:.4f}", flush=True)

        def objective(trial):
            suggest_params(trial)
            params = params_to_settings(trial.params)
            metrics = evaluate(srv, params, entries, binary=binary)
            trial.set_user_attr("mean_ndcg5", float(metrics.mean_ndcg5))
            trial.set_user_attr("mean_mrr5", float(metrics.mean_mrr5))
            trial.set_user_attr("hit3_rate", float(metrics.hit3_rate))
            trial.set_user_attr("hit1_rate", float(metrics.hit1_rate))
            compact = " ".join(f"{k}={v}" for k, v in sorted(params.items()))
            print(
                f"[tune] trial {trial.number:>3} ndcg5={metrics.mean_ndcg5:.4f} "
                f"mrr5={metrics.mean_mrr5:.4f} hit3={metrics.hit3_rate:.4f} "
                f"hit1={metrics.hit1_rate:.4f}  {compact}",
                flush=True,
            )
            return -float(metrics.mean_ndcg5)

        study = optuna.create_study(
            study_name=study_name,
            storage=storage,
            sampler=TPESampler(seed=42, n_startup_trials=10),
            load_if_exists=True,
        )
        if not study.trials:
            study.enqueue_trial(DEFAULTS)
            print("[tune] defaults enqueued as trial 0", flush=True)
        study.optimize(objective, n_trials=n_trials)

        best = study.best_trial
        tuned_params = params_to_settings(best.params)
        # Full metrics for the tuned config (per-query + per-category feed the report).
        tuned_metrics = evaluate(srv, tuned_params, entries, binary=binary)
        # Drift check end: the LAST evaluation is the defaults config again.
        drift_end = evaluate(srv, DEFAULTS, entries, binary=binary)
        drift_problems_ = drift_problems(drift_start, drift_end)

        regressions = find_regressions(
            [q.as_dict() for q in drift_start.per_query],
            [q.as_dict() for q in tuned_metrics.per_query],
        )

        out = build_tuned_output(
            study_id=study_name,
            run_date=datetime.now().strftime("%Y-%m-%d"),
            n_trials=len(study.trials),
            corpus_name=corpus_name,
            corpus_size=len(entries),
            dataset=os.path.basename(os.path.normpath(data_root)),
            defaults_metrics=drift_start.as_dict(),
            tuned_metrics=tuned_metrics.as_dict(),
            tuned_params=tuned_params,
            n_regressed_queries=len(regressions),
            best_trial={"number": best.number, "value": float(best.value)},
            drift={"passed": not drift_problems_, "problems": drift_problems_},
        )
        os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
        with open(out_path, "w") as fh:
            json.dump(out, fh, indent=2, default=str)
            fh.write("\n")

        print(f"[tune] study complete: {len(study.trials)} trials; "
              f"best trial {best.number} (value {best.value:.4f})", flush=True)
        print(f"[tune] tuned params: {tuned_params}", flush=True)
        print(f"[tune] eval defaults ndcg5={drift_start.mean_ndcg5:.4f} -> "
              f"tuned {tuned_metrics.mean_ndcg5:.4f} "
              f"(improvement {tuned_metrics.mean_ndcg5 - drift_start.mean_ndcg5:+.4f})", flush=True)
        if regressions:
            print(f"[tune] warning: {len(regressions)} eval queries regress on nDCG@5 vs defaults", flush=True)
        if len(regressions) > 5:
            print("[tune] FLAG: >5 regressions — owner review of the regression table "
                  "required before shipping (plan §10)", flush=True)
        if drift_problems_:
            print(f"[tune] DRIFT CHECK FAILED: {'; '.join(drift_problems_)}", flush=True)
            return 1
        print("[tune] drift check PASS: defaults metrics identical at session start and end", flush=True)
        print(f"[tune] tuned parameters written to {out_path}", flush=True)
        return 0


def _default_corpus_path() -> str:
    """scripts/retrieval_tuning/corpora/eval-set-100.json (the plan's objective corpus)."""
    return str(Path(__file__).resolve().parents[2] / "retrieval_tuning" / "corpora" / "eval-set-100.json")


def _parse_args(argv: list[str]) -> argparse.Namespace:
    run_date = datetime.now().strftime("%Y-%m-%d")
    parser = argparse.ArgumentParser(
        description="Optuna TPE study over the 9 retrieval knobs (plan §6.3/§10).",
    )
    parser.add_argument("--data-root", required=True,
                        help="Bank dir to serve (a scratch copy — never the live bank)")
    parser.add_argument("--corpus", default=_default_corpus_path(),
                        help="Objective corpus JSON (default: eval-set-100.json)")
    parser.add_argument("--trials", type=int, default=50,
                        help="Total trials including the enqueued defaults trial (default 50)")
    parser.add_argument("--storage",
                        default=f"sqlite:////tmp/continue-testing-algorithm/runs/{run_date}/study.db",
                        help="Optuna sqlite storage URL (resumable)")
    parser.add_argument("--study-name", default=f"retrieval-tune-{run_date}",
                        help="Optuna study name")
    parser.add_argument("--binary", default="ai-raccoon",
                        help="ai-raccoon binary for the scratch server")
    parser.add_argument("--out", default="docs/work/2026-08-21-tuned-parameters.json",
                        help="Tuned-parameters JSON output path")
    parser.add_argument("--limit", type=int, default=None,
                        help="Top-k eval-subset for dry runs (plan §11)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)
    if args.trials < 1:
        print("--trials must be >= 1", file=sys.stderr)
        return 2
    return run_study(
        data_root=args.data_root,
        corpus_path=args.corpus,
        n_trials=args.trials,
        storage=args.storage,
        study_name=args.study_name,
        binary=args.binary,
        out_path=args.out,
        limit=args.limit,
    )


if __name__ == "__main__":
    sys.exit(main())
