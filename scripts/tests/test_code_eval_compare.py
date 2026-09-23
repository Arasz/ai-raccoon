"""compare_code_eval: the Keep/drop rule, exactly as
docs/work/2026-09-23-code-retrieval-eval-plan.md states it (RED-first, no
implementation consulted):

1. Paired bootstrap (>=2000 resamples, fixed seed) over held-out per-query
   nDCG@5 diffs: CI lower bound > max(0, noise band) AND mean gain >= +0.02.
2. The target category improves on held-out.
3. Floors on held-out: behaviour-NL >= -0.03, every language >= -0.05,
   test-intent >= -0.05.
4. The tuning split does not move in the opposite direction of an improving
   held-out result.

Discrimination proof (plan): a REVERSED ranking must fail rule 1 (DROP);
identical arms must DROP (zero mean gain); a clear, uniform gain with no floor
breach must KEEP; a floor breach must DROP even when the headline gain is
large.
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
COMPARE_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "compare_code_eval.py"
sys.path.insert(0, str(REPO_ROOT / "scripts" / "src"))


def _load():
    spec = importlib.util.spec_from_file_location("compare_code_eval", COMPARE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


compare = _load()

TARGET_CATEGORY = "identifier-fragment"


def _row(entry_id, ndcg5, *, category="identifier-fragment", split="held-out", language="python"):
    return {
        "entry_id": entry_id,
        "category": category,
        "split": split,
        "language": language,
        "ndcg5": ndcg5,
    }


def _uniform_rows(n, base_score, *, category="identifier-fragment", split="held-out", language="python", prefix="q"):
    return [_row(f"{prefix}{i}", base_score, category=category, split=split, language=language) for i in range(n)]


class TestPairedBootstrapCi:
    def test_zero_diffs_give_a_zero_mean_and_a_zero_width_ci(self):
        result = compare.paired_bootstrap_ci([0.0] * 30, resamples=2000, seed=42)
        assert result["mean"] == pytest.approx(0.0)
        assert result["ciLow"] == pytest.approx(0.0)
        assert result["ciHigh"] == pytest.approx(0.0)
        assert result["n"] == 30

    def test_uniform_positive_diffs_give_a_ci_strictly_above_zero(self):
        result = compare.paired_bootstrap_ci([0.1] * 40, resamples=2000, seed=42)
        assert result["ciLow"] > 0.0
        assert result["mean"] == pytest.approx(0.1)

    def test_is_deterministic_under_a_fixed_seed(self):
        diffs = [0.05, -0.02, 0.1, 0.0, 0.2, -0.01, 0.15]
        a = compare.paired_bootstrap_ci(diffs, resamples=2000, seed=7)
        b = compare.paired_bootstrap_ci(diffs, resamples=2000, seed=7)
        assert a == b


class TestKeepDropDiscrimination:
    """The four scenarios the plan's discrimination proof calls out by name."""

    def test_reversed_ranking_drops(self):
        # Baseline: perfect (rank-1 hit, ndcg5=1.0) on every held-out query.
        # Arm: the SAME queries but reversed to a rank-5 hit (ndcg5 << 1.0) —
        # a strictly worse arm must never pass the bootstrap floor.
        baseline_rows = _uniform_rows(30, 1.0)
        reversed_score = 1.0 / __import__("math").log2(6)  # nDCG when the hit lands at rank 5
        arm_rows = _uniform_rows(30, reversed_score)
        result = compare.evaluate_keep_drop(
            baseline_rows, arm_rows, target_category=TARGET_CATEGORY, noise_band=0.0
        )
        assert result["verdict"] == "DROP"
        assert result["bootstrap"]["mean"] < 0

    def test_identical_arms_drop(self):
        rows = _uniform_rows(30, 0.6)
        result = compare.evaluate_keep_drop(rows, rows, target_category=TARGET_CATEGORY, noise_band=0.0)
        assert result["verdict"] == "DROP"
        assert result["bootstrap"]["mean"] == pytest.approx(0.0)

    def test_clear_uniform_gain_keeps(self):
        baseline_rows = _uniform_rows(40, 0.5)
        arm_rows = _uniform_rows(40, 0.65)  # +0.15 uniform gain, well past the +0.02 floor
        result = compare.evaluate_keep_drop(
            baseline_rows, arm_rows, target_category=TARGET_CATEGORY, noise_band=0.0
        )
        assert result["verdict"] == "KEEP"
        assert result["bootstrap"]["mean"] == pytest.approx(0.15)
        assert result["bootstrap"]["ciLow"] > 0.0

    def test_floor_breach_drops_even_with_a_large_overall_gain(self):
        # Target-category queries gain a lot; a DIFFERENT language regresses hard
        # (below the -0.05 floor). Rule 1/2 would pass on their own; rule 3 must
        # still veto the keep.
        good_baseline = _uniform_rows(30, 0.5, language="python", prefix="p")
        good_arm = _uniform_rows(30, 0.9, language="python", prefix="p")
        bad_baseline = _uniform_rows(15, 0.6, language="rust", prefix="r")
        bad_arm = _uniform_rows(15, 0.4, language="rust", prefix="r")  # -0.2, below -0.05 floor
        baseline_rows = good_baseline + bad_baseline
        arm_rows = good_arm + bad_arm
        result = compare.evaluate_keep_drop(
            baseline_rows, arm_rows, target_category=TARGET_CATEGORY, noise_band=0.0
        )
        assert result["verdict"] == "DROP"
        assert any("rust" in reason for reason in result["reasons"])
        # Confirm this would have KEPT if not for the floor breach (headline gain is real).
        assert result["bootstrap"]["mean"] > 0.02


class TestNoiseBand:
    def test_a_gain_smaller_than_the_noise_band_drops(self):
        baseline_rows = _uniform_rows(30, 0.5)
        arm_rows = _uniform_rows(30, 0.55)  # +0.05 gain
        result = compare.evaluate_keep_drop(
            baseline_rows, arm_rows, target_category=TARGET_CATEGORY, noise_band=0.1
        )
        assert result["verdict"] == "DROP"


class TestTuningSplitOppositeDirection:
    def test_tuning_split_moving_opposite_the_held_out_gain_drops(self):
        held_out_baseline = _uniform_rows(30, 0.5, split="held-out")
        held_out_arm = _uniform_rows(30, 0.65, split="held-out")
        tuning_baseline = _uniform_rows(10, 0.6, split="tuning", prefix="t")
        tuning_arm = _uniform_rows(10, 0.3, split="tuning", prefix="t")  # moves opposite
        result = compare.evaluate_keep_drop(
            held_out_baseline + tuning_baseline,
            held_out_arm + tuning_arm,
            target_category=TARGET_CATEGORY,
            noise_band=0.0,
        )
        assert result["verdict"] == "DROP"


class TestTargetCategoryMustImprove:
    def test_target_category_regressing_drops_even_with_overall_gain(self):
        # Overall/held-out gain is positive, but the TARGET category itself regresses.
        target_baseline = _row("t1", 0.8, category=TARGET_CATEGORY)
        target_arm = _row("t1", 0.6, category=TARGET_CATEGORY)  # target regresses
        other_baseline = _uniform_rows(30, 0.4, category="behaviour-nl", prefix="o")
        other_arm = _uniform_rows(30, 0.7, category="behaviour-nl", prefix="o")  # drives the mean up
        result = compare.evaluate_keep_drop(
            [target_baseline] + other_baseline,
            [target_arm] + other_arm,
            target_category=TARGET_CATEGORY,
            noise_band=0.0,
        )
        assert result["verdict"] == "DROP"
        assert result["targetDelta"] is not None and result["targetDelta"] < 0
