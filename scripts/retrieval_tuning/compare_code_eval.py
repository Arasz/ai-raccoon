#!/usr/bin/env python3
"""Keep/drop verdict for one code-eval arm vs its baseline
(docs/work/2026-09-23-code-retrieval-eval-plan.md, "Keep/drop rule (fixed
before any measurement)" — the source of truth this module implements
verbatim):

1. Paired bootstrap over HELD-OUT per-query nDCG@5 differences (>=2000
   resamples of queries, fixed seed): the 95% CI lower bound is above 0 and
   above the noise band, and the mean gain is at least +0.02.
2. The target category improves on held-out (identifier-fragment for P2, the
   pooled distractor + test-intent set for P3, path/context for P4).
3. Floors on held-out: behaviour-NL drops by no more than 0.03, no language
   drops by more than 0.05, and test-intent drops by no more than 0.05.
4. The tuning split does not move in the opposite direction of an improving
   held-out result.

Input: two run_code_eval.py results-<arm>.json files (baseline first). Each
carries `metrics.per_query`, a list of row dicts with at least `entry_id` and
`ndcg5`, plus (when the corpus carries them) `category`, `split`, `language`.
Rows are paired by entry_id; unpaired ids are dropped silently (a query the
other arm's corpus snapshot did not carry).

Usage:
    python3 compare_code_eval.py <baseline.json> <arm.json> \\
        --target-category identifier-fragment [--noise-band 0.01]

Exit: 0 on KEEP, 1 on DROP.
"""

from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path
from typing import Optional

DEFAULT_RESAMPLES = 2000
DEFAULT_SEED = 20260923
DEFAULT_CONFIDENCE = 0.95

MEAN_GAIN_FLOOR = 0.02
BEHAVIOUR_NL_CATEGORY = "behaviour-nl"
BEHAVIOUR_NL_FLOOR = -0.03
LANGUAGE_FLOOR = -0.05
TEST_INTENT_CATEGORY = "test-intent"
TEST_INTENT_FLOOR = -0.05

Pair = tuple[dict, dict]


def load_per_query(results_path) -> list[dict]:
    """Read a run_code_eval.py results-<arm>.json's per-query rows."""
    data = json.loads(Path(results_path).read_text())
    metrics = data.get("metrics", data)  # tolerate a bare Metrics.as_dict() too
    return list(metrics.get("per_query") or [])


def pair_by_id(baseline_rows: list[dict], arm_rows: list[dict]) -> list[Pair]:
    """(baseline, arm) pairs sharing an entry_id; an id present on only one side is dropped."""
    arm_by_id = {row["entry_id"]: row for row in arm_rows}
    return [(row, arm_by_id[row["entry_id"]]) for row in baseline_rows if row["entry_id"] in arm_by_id]


def _held_out(pairs: list[Pair]) -> list[Pair]:
    """Held-out pairs; when no row carries a 'split' at all, every pair counts as held-out
    (a fixture/corpus with no tuning/held-out split still gets a verdict)."""
    if not any(b.get("split") or a.get("split") for b, a in pairs):
        return pairs
    return [(b, a) for b, a in pairs if b.get("split") == "held-out" or a.get("split") == "held-out"]


def ndcg5_diffs(pairs: list[Pair]) -> list[float]:
    return [float(a["ndcg5"]) - float(b["ndcg5"]) for b, a in pairs]


def paired_bootstrap_ci(
    diffs: list[float],
    *,
    resamples: int = DEFAULT_RESAMPLES,
    seed: int = DEFAULT_SEED,
    confidence: float = DEFAULT_CONFIDENCE,
) -> dict:
    """Percentile bootstrap CI of the mean paired nDCG@5 difference (deterministic, fixed seed)."""
    n = len(diffs)
    if n == 0:
        return {"mean": 0.0, "ciLow": 0.0, "ciHigh": 0.0, "n": 0, "resamples": resamples}
    rng = random.Random(seed)
    resample_means = []
    for _ in range(resamples):
        resample_means.append(sum(diffs[rng.randrange(n)] for _ in range(n)) / n)
    resample_means.sort()
    alpha = (1.0 - confidence) / 2.0
    lo_idx = max(0, min(resamples - 1, int(alpha * resamples)))
    hi_idx = max(0, min(resamples - 1, int((1.0 - alpha) * resamples) - 1))
    return {
        "mean": sum(diffs) / n,
        "ciLow": resample_means[lo_idx],
        "ciHigh": resample_means[hi_idx],
        "n": n,
        "resamples": resamples,
    }


def _mean_delta(pairs: list[Pair], *, category: Optional[str] = None, language: Optional[str] = None):
    subset = pairs
    if category is not None:
        subset = [(b, a) for b, a in subset if b.get("category") == category]
    if language is not None:
        subset = [(b, a) for b, a in subset if b.get("language") == language]
    if not subset:
        return None
    return sum(float(a["ndcg5"]) - float(b["ndcg5"]) for b, a in subset) / len(subset)


def evaluate_keep_drop(
    baseline_rows: list[dict],
    arm_rows: list[dict],
    *,
    target_category: str,
    noise_band: float = 0.0,
    resamples: int = DEFAULT_RESAMPLES,
    seed: int = DEFAULT_SEED,
) -> dict:
    """The Keep/drop rule's four gates over one baseline/arm results pair."""
    pairs = pair_by_id(baseline_rows, arm_rows)
    held_out_pairs = _held_out(pairs)
    reasons: list[str] = []

    diffs = ndcg5_diffs(held_out_pairs)
    bootstrap = paired_bootstrap_ci(diffs, resamples=resamples, seed=seed)
    ci_floor = max(0.0, noise_band)
    rule1 = bootstrap["ciLow"] > ci_floor and bootstrap["mean"] >= MEAN_GAIN_FLOOR
    if not rule1:
        reasons.append(
            f"rule1 bootstrap: ciLow={bootstrap['ciLow']:.4f} (need > {ci_floor:.4f}), "
            f"mean={bootstrap['mean']:.4f} (need >= {MEAN_GAIN_FLOOR})"
        )

    target_delta = _mean_delta(held_out_pairs, category=target_category)
    rule2 = target_delta is not None and target_delta > 0
    if not rule2:
        reasons.append(
            f"rule2 target category {target_category!r} held-out delta={target_delta} (need >0, N>=1)"
        )

    behaviour_nl_delta = _mean_delta(held_out_pairs, category=BEHAVIOUR_NL_CATEGORY)
    if behaviour_nl_delta is not None and behaviour_nl_delta < BEHAVIOUR_NL_FLOOR:
        reasons.append(f"rule3 floor: behaviour-NL delta {behaviour_nl_delta:.4f} < {BEHAVIOUR_NL_FLOOR}")

    languages = sorted({b.get("language") for b, _ in held_out_pairs if b.get("language")})
    language_deltas: dict[str, float] = {}
    for language in languages:
        delta = _mean_delta(held_out_pairs, language=language)
        language_deltas[language] = delta if delta is not None else 0.0
        if delta is not None and delta < LANGUAGE_FLOOR:
            reasons.append(f"rule3 floor: language {language!r} delta {delta:.4f} < {LANGUAGE_FLOOR}")

    test_intent_delta = _mean_delta(held_out_pairs, category=TEST_INTENT_CATEGORY)
    if test_intent_delta is not None and test_intent_delta < TEST_INTENT_FLOOR:
        reasons.append(f"rule3 floor: test-intent delta {test_intent_delta:.4f} < {TEST_INTENT_FLOOR}")
    rule3 = not any(r.startswith("rule3") for r in reasons)

    tuning_pairs = [(b, a) for b, a in pairs if b.get("split") == "tuning"]
    tuning_delta = _mean_delta(tuning_pairs) if tuning_pairs else None
    rule4 = True
    if tuning_delta is not None and bootstrap["mean"] > 0 and tuning_delta < 0:
        rule4 = False
        reasons.append(
            f"rule4 tuning split moved opposite: tuning delta={tuning_delta:.4f} "
            f"while held-out mean={bootstrap['mean']:.4f}"
        )

    verdict = "KEEP" if (rule1 and rule2 and rule3 and rule4) else "DROP"
    return {
        "verdict": verdict,
        "bootstrap": bootstrap,
        "targetCategory": target_category,
        "targetDelta": target_delta,
        "floors": {
            "behaviourNlDelta": behaviour_nl_delta,
            "testIntentDelta": test_intent_delta,
            "languageDeltas": language_deltas,
        },
        "tuningDelta": tuning_delta,
        "noiseBand": noise_band,
        "heldOutN": len(held_out_pairs),
        "reasons": reasons,
    }


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("baseline", type=Path, help="baseline results-<arm>.json")
    parser.add_argument("arm", type=Path, help="candidate arm results-<arm>.json")
    parser.add_argument("--target-category", required=True, help="the category the arm targets (rule 2)")
    parser.add_argument("--noise-band", type=float, default=0.0, help="largest mean-nDCG@5 spread across B0 runs")
    parser.add_argument("--resamples", type=int, default=DEFAULT_RESAMPLES)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    args = parser.parse_args(argv)

    baseline_rows = load_per_query(args.baseline)
    arm_rows = load_per_query(args.arm)
    result = evaluate_keep_drop(
        baseline_rows,
        arm_rows,
        target_category=args.target_category,
        noise_band=args.noise_band,
        resamples=args.resamples,
        seed=args.seed,
    )

    boot = result["bootstrap"]
    print(f"baseline={args.baseline}")
    print(f"arm     ={args.arm}")
    print(f"held-out N={result['heldOutN']} noise-band={result['noiseBand']}")
    print(
        f"bootstrap: mean={boot['mean']:.4f} ci=[{boot['ciLow']:.4f}, {boot['ciHigh']:.4f}] "
        f"n={boot['n']} resamples={boot['resamples']}"
    )
    print(f"target category {result['targetCategory']!r}: delta={result['targetDelta']}")
    print(f"floors: {result['floors']}")
    print(f"tuning split delta: {result['tuningDelta']}")
    print(result["verdict"])
    if result["reasons"]:
        print("reasons:")
        for reason in result["reasons"]:
            print(f"  - {reason}")
    return 0 if result["verdict"] == "KEEP" else 1


if __name__ == "__main__":
    sys.exit(main())
