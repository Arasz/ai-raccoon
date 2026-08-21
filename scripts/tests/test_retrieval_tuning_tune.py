"""Unit tests for the Optuna study runner (plan §6.3, §8, §10, WP5, gate G5).

Covers: search-space builder (every knob suggested within the plan §6.3
bounds), params coercion (types + all-9-knobs completeness — the harness
never relies on inherited bank settings), defaults enqueued as trial 0,
the drift-check helper (identical defaults metrics at session start/end
pass; ANY change is reported), and the tuned-parameters JSON shape.

The live study (server + optuna + settings writes) is exercised by the
mini-study proof; these tests stay pure.
"""

import optuna
import pytest
from optuna.samplers import TPESampler

from retrieval_tuning import scoring, settings as settings_mod, tune

# Plan §6.3 search space (bounds as the plan table states them).
BOUNDS = {
    "rrfK": (5, 200),
    "ftsWeight": (0, 8),
    "vectorWeight": (0, 8),
    "sourceLambda": (0.0, 0.5),
    "consolidationThreshold": (0.0, 0.5),
    "docScoreFormula": ("max", "sum"),
    "candidateWindow": ("max3x100", "max5x50"),
    "structureAlpha": (0.0, 1.0),
    "fusion": (False, True),
}


def _trial():
    """A fresh Optuna trial (TPESampler as the plan mandates)."""
    study = optuna.create_study(sampler=TPESampler(seed=42, n_startup_trials=10))
    return study.ask()


def _nine(**overrides):
    """A full 9-knob params dict with the given overrides."""
    params = dict(settings_mod.KNOB_DEFAULTS)
    params.update(overrides)
    return params


def _results_for(gains):
    """Fake search results: the target chunk at the gain positions, noise elsewhere."""
    target = {"hash": "abc123def", "sourceFile": "docs/adr/0001-x.md"}
    noise = {"hash": "zzzzzzzz", "sourceFile": "docs/adr/other.md"}
    return [target if g else noise for g in gains]


def _entry(entry_id="q1"):
    return {
        "id": entry_id,
        "query": f"query {entry_id}",
        "expectedHash": "abc123",
        "category": "c",
        "targetProjectId": "p",
        "targetScope": "project",
        "searchLimit": 5,
    }


def _metrics_for(gains_lists):
    """A Metrics object from hand-written per-query gains (plan §7.1 scoring)."""
    return scoring.summarize(
        [scoring.score_query(_results_for(gains), _entry(f"q{i}")) for i, gains in enumerate(gains_lists)]
    )


class TestSearchSpace:
    def test_every_knob_is_suggested(self):
        params = tune.suggest_params(_trial())
        assert set(params) == set(BOUNDS)

    def test_bounds_and_types_over_trials(self):
        for _ in range(40):
            params = tune.suggest_params(_trial())
            for knob in ("rrfK", "ftsWeight", "vectorWeight"):
                assert isinstance(params[knob], int) and not isinstance(params[knob], bool)
                low, high = BOUNDS[knob]
                assert low <= params[knob] <= high, f"{knob}={params[knob]} outside [{low},{high}]"
            for knob in ("sourceLambda", "consolidationThreshold", "structureAlpha"):
                assert isinstance(params[knob], float), f"{knob} is {type(params[knob])}"
                low, high = BOUNDS[knob]
                assert low <= params[knob] <= high, f"{knob}={params[knob]} outside [{low},{high}]"
            assert params["docScoreFormula"] in ("max", "sum")
            assert params["candidateWindow"] in ("max3x100", "max5x50")
            assert isinstance(params["fusion"], bool)

    def test_rrfk_stays_within_bounds_including_default(self):
        params = tune.suggest_params(_trial())
        assert 5 <= params["rrfK"] <= 200


class TestParamsToSettings:
    def test_defaults_roundtrip(self):
        assert tune.params_to_settings(_nine()) == settings_mod.KNOB_DEFAULTS

    def test_coerces_types_from_jsonish_input(self):
        raw = _nine(rrfK="60", sourceLambda="0.1", structureAlpha="0.5")
        settings = tune.params_to_settings(raw)
        assert settings["rrfK"] == 60 and isinstance(settings["rrfK"], int)
        assert settings["sourceLambda"] == 0.1 and isinstance(settings["sourceLambda"], float)

    def test_coerces_fusion_variants(self):
        assert tune.params_to_settings(_nine(fusion="False"))["fusion"] is False
        assert tune.params_to_settings(_nine(fusion="true"))["fusion"] is True
        assert tune.params_to_settings(_nine(fusion=1))["fusion"] is True

    def test_rejects_missing_knob(self):
        raw = _nine()
        del raw["fusion"]
        with pytest.raises(ValueError, match="fusion"):
            tune.params_to_settings(raw)

    def test_rejects_unknown_categorical(self):
        with pytest.raises(ValueError, match="docScoreFormula"):
            tune.params_to_settings(_nine(docScoreFormula="avg"))


class TestEnqueuedDefaults:
    def test_defaults_are_trial_zero(self):
        study = optuna.create_study(sampler=TPESampler(seed=42, n_startup_trials=10))
        study.enqueue_trial(dict(settings_mod.KNOB_DEFAULTS))

        def objective(trial):
            tune.suggest_params(trial)
            return 0.0

        study.optimize(objective, n_trials=2)
        assert len(study.trials) == 2  # enqueued baseline + 1 sampled
        assert tune.params_to_settings(study.trials[0].params) == settings_mod.KNOB_DEFAULTS


class TestDriftCheck:
    def test_identical_metrics_pass(self):
        start = _metrics_for([[1, 0, 0, 0, 0]])
        end = _metrics_for([[1, 0, 0, 0, 0]])
        assert tune.drift_problems(start, end) == []

    def test_different_means_fail(self):
        start = _metrics_for([[1, 0, 0, 0, 0]])
        end = _metrics_for([[0, 1, 0, 0, 0]])
        problems = tune.drift_problems(start, end)
        assert problems
        assert any("ndcg5" in p for p in problems)

    def test_same_mean_but_shifted_rank_fails(self):
        # Same aggregate mean, different per-query outcome — the drift guard
        # compares per-query records, not just the headline number.
        start = _metrics_for([[1, 0, 0, 0, 0], [0, 1, 0, 0, 0]])
        end = _metrics_for([[0, 1, 0, 0, 0], [1, 0, 0, 0, 0]])
        assert start.mean_ndcg5 == end.mean_ndcg5
        assert tune.drift_problems(start, end)


class TestTunedOutput:
    def test_output_shape_and_values(self):
        defaults = _metrics_for([[1, 0, 0, 0, 0]]).as_dict()
        tuned = _metrics_for([[0, 0, 1, 0, 0]]).as_dict()
        out = tune.build_tuned_output(
            study_id="retrieval-tune-2026-08-21",
            run_date="2026-08-21",
            n_trials=4,
            corpus_name="sextant-6.json",
            corpus_size=6,
            dataset="sextant-bank",
            defaults_metrics=defaults,
            tuned_metrics=tuned,
            tuned_params=dict(settings_mod.KNOB_DEFAULTS),
            best_trial={"number": 2, "value": -0.5},
            drift={"passed": True, "problems": []},
        )
        assert set(out["tuned"]) == set(settings_mod.KNOB_DEFAULTS)
        assert set(out["defaults"]) == set(settings_mod.KNOB_DEFAULTS)
        assert out["study_id"] == "retrieval-tune-2026-08-21"
        assert out["run_date"] == "2026-08-21"
        assert out["n_trials"] == 4
        assert out["eval"]["defaults"]["mean_ndcg5"] == 1.0
        assert out["eval"]["tuned"]["mean_ndcg5"] == pytest.approx(0.5)  # rank 3 -> 1/log2(4)
        assert out["eval"]["improvement_ndcg5"] == pytest.approx(-0.5)
        assert out["drift"]["passed"] is True
        assert out["best_trial"]["number"] == 2
