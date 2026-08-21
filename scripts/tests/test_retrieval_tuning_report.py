"""Unit tests for the tuning report generator (plan §7, §8, §10, WP5, gate G4).

Covers the G4 discrimination floor (a REVERSED ranking fails the eval floor
the normal ranking passes — scoring.py's data feeding the floor), the
per-query regression table, the 3-level test-set grade deltas, the
file/non-file bucket breakdown over fixture corpora, the matrix-influence
summary over a fixture CSV (hand-computed numbers), tuned-JSON parsing, and
the markdown render's required sections.
"""

import csv
import json

import pytest

from retrieval_tuning import report, scoring, settings as settings_mod

TARGET_RESULT = {"hash": "abc123def", "sourceFile": "docs/adr/0001-x.md"}
NOISE_RESULT = {"hash": "zzzzzzzz", "sourceFile": "docs/adr/other.md"}
ENTRY = {
    "id": "q1",
    "query": "query q1",
    "expectedHash": "abc123",
    "category": "c",
    "targetProjectId": "p",
    "targetScope": "project",
    "searchLimit": 5,
}


def _results_for(gains):
    return [TARGET_RESULT if g else NOISE_RESULT for g in gains]


def _metrics_for(gains_lists):
    return scoring.summarize(
        [scoring.score_query(_results_for(gains), ENTRY) for gains in gains_lists]
    )


def _tuned_json_defaults(**overrides):
    """A valid tuned-parameters JSON dict (as tune.py writes it)."""
    defaults = _metrics_for([[1, 0, 0, 0, 0]]).as_dict()
    tuned = _metrics_for([[0, 1, 0, 0, 0]]).as_dict()
    data = {
        "study_id": "retrieval-tune-2026-08-21",
        "run_date": "2026-08-21",
        "n_trials": 4,
        "corpus": "eval-set-100.json",
        "corpus_size": 100,
        "dataset": "memory-copy",
        "defaults": dict(settings_mod.KNOB_DEFAULTS),
        "tuned": dict(settings_mod.KNOB_DEFAULTS),
        "eval": {"defaults": defaults, "tuned": tuned, "improvement_ndcg5": -0.5},
        "drift": {"passed": True, "problems": []},
        "best_trial": {"number": 2, "value": -0.4},
    }
    data.update(overrides)
    return data


class TestEvalFloorDiscrimination:
    """G4: a REVERSED result list must fail the eval floor the normal ranking passes."""

    def test_normal_ranking_passes_and_reversed_fails(self):
        normal = _metrics_for([[1, 0, 0, 0, 0]] * 5)
        reversed_ = _metrics_for([[0, 0, 0, 0, 1]] * 5)  # same list, flipped
        floor = {"mean_ndcg5": 0.5}
        assert report.check_eval_floor(normal.as_dict(), floor) == []
        problems = report.check_eval_floor(reversed_.as_dict(), floor)
        assert problems
        assert "mean_ndcg5" in problems[0]

    def test_floor_math_is_the_scoring_math(self):
        # rank 1 -> ndcg5 = 1.0; rank 5 -> 1/log2(6) ~= 0.3869
        normal = _metrics_for([[1, 0, 0, 0, 0]])
        reversed_ = _metrics_for([[0, 0, 0, 0, 1]])
        assert normal.mean_ndcg5 == pytest.approx(1.0)
        assert reversed_.mean_ndcg5 == pytest.approx(1 / 2.584962500721156)

    def test_missing_metric_is_a_problem(self):
        problems = report.check_eval_floor({"mean_ndcg5": None}, {"mean_ndcg5": 0.5})
        assert problems

    def test_default_floor_is_mean_ndcg5(self):
        assert report.DEFAULT_EVAL_FLOOR == {"mean_ndcg5": 0.5}


class TestFindRegressions:
    def test_regressions_are_strict_and_sorted(self):
        defaults = [
            {"entry_id": "A", "ndcg5": 1.0},
            {"entry_id": "B", "ndcg5": 0.7},
            {"entry_id": "C", "ndcg5": 0.3},
        ]
        tuned = [
            {"entry_id": "A", "ndcg5": 0.9},
            {"entry_id": "B", "ndcg5": 0.8},
            {"entry_id": "C", "ndcg5": 0.3},
        ]
        regs = report.find_regressions(defaults, tuned)
        assert [r["entry_id"] for r in regs] == ["A"]
        assert regs[0]["default_ndcg5"] == pytest.approx(1.0)
        assert regs[0]["tuned_ndcg5"] == pytest.approx(0.9)
        assert regs[0]["delta_ndcg5"] == pytest.approx(-0.1)

    def test_equal_scores_are_not_regressions(self):
        defaults = [{"entry_id": "A", "ndcg5": 0.5}]
        tuned = [{"entry_id": "A", "ndcg5": 0.5}]
        assert report.find_regressions(defaults, tuned) == []

    def test_missing_query_in_tuned_is_skipped(self):
        defaults = [{"entry_id": "A", "ndcg5": 1.0}, {"entry_id": "B", "ndcg5": 0.5}]
        tuned = [{"entry_id": "A", "ndcg5": 0.9}]
        assert [r["entry_id"] for r in report.find_regressions(defaults, tuned)] == ["A"]


class TestGrades:
    def test_grade_from_rank_mapping(self):
        assert report.grade_from_rank(1) == "good"
        assert report.grade_from_rank(2) == "good"
        assert report.grade_from_rank(3) == "could-be-improved"
        assert report.grade_from_rank(5) == "could-be-improved"
        assert report.grade_from_rank(None) == "just-wrong"

    def test_grade_from_metrics(self):
        per_query = [
            {"entry_id": "a", "first_relevant_rank": 1},
            {"entry_id": "b", "first_relevant_rank": 4},
            {"entry_id": "c", "first_relevant_rank": None},
        ]
        assert report.grade_from_metrics(per_query) == {
            "a": "good", "b": "could-be-improved", "c": "just-wrong",
        }

    def test_grade_deltas_hand_computed(self):
        default = {"TS-01": "good", "TS-02": "could-be-improved", "TS-03": "just-wrong"}
        tuned = {"TS-01": "good", "TS-02": "good", "TS-03": "could-be-improved"}
        deltas = report.grade_deltas(default, tuned)
        by_id = {d["entry_id"]: d for d in deltas}
        assert by_id["TS-01"]["delta"] == 0
        assert by_id["TS-02"]["delta"] == 1
        assert by_id["TS-03"]["delta"] == 1
        assert report.grade_delta_summary(deltas) == {"improved": 2, "worsened": 0, "unchanged": 1}

    def test_grade_deltas_worsening(self):
        default = {"TS-01": "good"}
        tuned = {"TS-01": "just-wrong"}
        deltas = report.grade_deltas(default, tuned)
        assert deltas[0]["delta"] == -2
        assert report.grade_delta_summary(deltas) == {"improved": 0, "worsened": 1, "unchanged": 0}


FIXTURE_CORPUS = [
    {"id": "F1", "query": "file q1", "nonFileTarget": False},
    {"id": "F2", "query": "file q2", "nonFileTarget": False},
    {"id": "N1", "query": "non-file q1", "nonFileTarget": True},
]


class TestBucketBreakdown:
    def test_file_vs_non_file_means_hand_computed(self):
        per_query = [
            {"entry_id": "F1", "ndcg5": 1.0, "mrr5": 1.0, "hit3": 1, "hit1": 1},
            {"entry_id": "F2", "ndcg5": 0.5, "mrr5": 0.25, "hit3": 1, "hit1": 0},
            {"entry_id": "N1", "ndcg5": 0.25, "mrr5": 0.2, "hit3": 0, "hit1": 0},
        ]
        buckets = report.bucket_breakdown(per_query, FIXTURE_CORPUS)
        assert set(buckets) == {"file", "non-file"}
        assert buckets["file"]["count"] == 2
        assert buckets["file"]["mean_ndcg5"] == pytest.approx(0.75)
        assert buckets["file"]["mean_mrr5"] == pytest.approx(0.625)
        assert buckets["file"]["hit3_rate"] == pytest.approx(1.0)
        assert buckets["file"]["hit1_rate"] == pytest.approx(0.5)
        assert buckets["non-file"]["mean_ndcg5"] == pytest.approx(0.25)
        assert buckets["non-file"]["hit3_rate"] == pytest.approx(0.0)

    def test_unknown_entry_id_is_skipped(self):
        per_query = [{"entry_id": "GHOST", "ndcg5": 1.0, "mrr5": 1.0, "hit3": 1, "hit1": 1}]
        buckets = report.bucket_breakdown(per_query, FIXTURE_CORPUS)
        assert buckets == {}


def _fixture_matrix_csv(tmp_path):
    """A small hand-written matrix CSV: baseline + 2 knobs x short ladders."""
    path = tmp_path / "matrix.csv"
    rows = [
        {"config_id": "baseline", "knob": "baseline", "value": "default",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.6", "mean_mrr5": "0.3", "hit3_rate": "0.8", "hit1_rate": "0.5",
         "per_query_json": "[]", "per_category_json": "{}"},
        {"config_id": "rrfK=5", "knob": "rrfK", "value": "5",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.5", "mean_mrr5": "0.25", "hit3_rate": "0.7", "hit1_rate": "0.4",
         "per_query_json": "[]", "per_category_json": "{}"},
        {"config_id": "rrfK=60", "knob": "rrfK", "value": "60",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.6", "mean_mrr5": "0.3", "hit3_rate": "0.8", "hit1_rate": "0.5",
         "per_query_json": "[]", "per_category_json": "{}"},
        {"config_id": "rrfK=200", "knob": "rrfK", "value": "200",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.55", "mean_mrr5": "0.28", "hit3_rate": "0.75", "hit1_rate": "0.45",
         "per_query_json": "[]", "per_category_json": "{}"},
        {"config_id": "fusion=False", "knob": "fusion", "value": "False",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.6", "mean_mrr5": "0.3", "hit3_rate": "0.8", "hit1_rate": "0.5",
         "per_query_json": "[]", "per_category_json": "{}"},
        {"config_id": "fusion=True", "knob": "fusion", "value": "True",
         "dataset": "mem", "corpus_size": "100",
         "mean_ndcg5": "0.7", "mean_mrr5": "0.4", "hit3_rate": "0.9", "hit1_rate": "0.6",
         "per_query_json": "[]", "per_category_json": "{}"},
    ]
    with open(path, "w", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    return path


class TestMatrixInfluence:
    def test_influence_groups_by_dataset_and_knob(self, tmp_path):
        influence = report.matrix_influence(_fixture_matrix_csv(tmp_path))
        assert ("mem", "rrfK") in influence
        assert ("mem", "fusion") in influence
        assert influence[("mem", "rrfK")]["5"]["mean_ndcg5"] == pytest.approx(0.5)
        assert influence[("mem", "rrfK")]["60"]["mean_ndcg5"] == pytest.approx(0.6)
        assert influence[("mem", "fusion")]["True"]["mean_ndcg5"] == pytest.approx(0.7)

    def test_baseline_rows_are_excluded(self, tmp_path):
        influence = report.matrix_influence(_fixture_matrix_csv(tmp_path))
        for (dataset, knob), points in influence.items():
            assert knob != "baseline"
            assert "default" not in points

    def test_summary_movement_hand_computed(self, tmp_path):
        summary = report.matrix_influence_summary(_fixture_matrix_csv(tmp_path))
        rrfk = [s for s in summary if s["knob"] == "rrfK"][0]
        assert rrfk["dataset"] == "mem"
        assert rrfk["ndcg5_min"] == pytest.approx(0.5)
        assert rrfk["ndcg5_max"] == pytest.approx(0.6)
        assert rrfk["ndcg5_movement"] == pytest.approx(0.1)
        assert rrfk["ndcg5_best_value"] == "60"
        assert rrfk["mrr5_movement"] == pytest.approx(0.05)
        fusion = [s for s in summary if s["knob"] == "fusion"][0]
        assert fusion["ndcg5_movement"] == pytest.approx(0.1)
        assert fusion["ndcg5_best_value"] == "True"


class TestParseTunedJson:
    def test_valid_json_parses(self, tmp_path):
        path = tmp_path / "tuned.json"
        path.write_text(json.dumps(_tuned_json_defaults()))
        parsed = report.parse_tuned_json(path)
        assert parsed["tuned"] == settings_mod.KNOB_DEFAULTS
        assert parsed["eval"]["defaults"]["mean_ndcg5"] == pytest.approx(1.0)

    def test_missing_knob_rejected(self, tmp_path):
        data = _tuned_json_defaults()
        del data["tuned"]["fusion"]
        path = tmp_path / "tuned.json"
        path.write_text(json.dumps(data))
        with pytest.raises(ValueError, match="fusion"):
            report.parse_tuned_json(path)

    def test_missing_eval_section_rejected(self, tmp_path):
        data = _tuned_json_defaults()
        del data["eval"]
        path = tmp_path / "tuned.json"
        path.write_text(json.dumps(data))
        with pytest.raises(ValueError, match="eval"):
            report.parse_tuned_json(path)


class TestStudySummary:
    def test_summary_reads_a_real_optuna_study(self, tmp_path):
        import optuna
        from optuna.samplers import TPESampler

        storage = f"sqlite:///{tmp_path / 'study.db'}"
        study = optuna.create_study(
            study_name="retrieval-tune-test", storage=storage,
            sampler=TPESampler(seed=42, n_startup_trials=10),
        )

        def objective(trial):
            trial.suggest_int("rrfK", 5, 200)
            return -0.5

        study.optimize(objective, n_trials=2)
        info = report.study_summary(storage, "retrieval-tune-test")
        assert info is not None
        assert info["study_name"] == "retrieval-tune-test"
        assert info["n_trials"] == 2
        assert info["n_completed"] == 2
        assert info["best_value"] == pytest.approx(-0.5)
        assert info["sampler"] == "TPESampler"

    def test_unreadable_storage_yields_none(self, tmp_path):
        assert report.study_summary(f"sqlite:///{tmp_path / 'missing.db'}") is None


class TestRenderReport:
    def _tuned_with_regressions(self, n_regressions=1):
        defaults_pq = []
        tuned_pq = []
        for i in range(10):
            defaults_pq.append({"entry_id": f"E{i:03d}", "ndcg5": 0.8, "mrr5": 0.5,
                                "hit3": 1, "hit1": 0, "first_relevant_rank": 2})
            tuned_pq.append({"entry_id": f"E{i:03d}", "ndcg5": 0.7, "mrr5": 0.5,
                             "hit3": 1, "hit1": 0, "first_relevant_rank": 3})
        # only the first n_regressions queries regress
        for i in range(n_regressions, 10):
            tuned_pq[i]["ndcg5"] = 0.9
        return _tuned_json_defaults(
            eval={
                "defaults": {"mean_ndcg5": 0.8, "mean_mrr5": 0.5, "hit3_rate": 1.0,
                             "hit1_rate": 0.0, "per_query": defaults_pq,
                             "per_category": {}},
                "tuned": {"mean_ndcg5": 0.88, "mean_mrr5": 0.5, "hit3_rate": 1.0,
                          "hit1_rate": 0.0, "per_query": tuned_pq,
                          "per_category": {}},
                "improvement_ndcg5": 0.08,
            }
        )

    def test_report_contains_required_sections(self, tmp_path):
        rows = report.read_matrix_csv(_fixture_matrix_csv(tmp_path))
        tuned = self._tuned_with_regressions(1)
        test_grades = {"defaults": {"TS-01": "good"}, "tuned": {"TS-01": "could-be-improved"}}
        md = report.render_report(
            tuned,
            rows,
            eval_entries=FIXTURE_CORPUS,
            test_entries=[{"id": "TS-01", "query": "t", "grade": "good", "gradeRationale": "r"}],
            test_grades=test_grades,
            study={"study_name": "s", "n_trials": 4, "best_value": -0.88},
        )
        assert "Defaults vs best" in md
        assert "Per-query regression" in md
        assert "Test-set grade deltas" in md
        assert "Matrix influence" in md
        assert "E000" in md  # the regression row
        assert "TS-01" in md

    def test_more_than_five_regressions_flagged(self, tmp_path):
        rows = report.read_matrix_csv(_fixture_matrix_csv(tmp_path))
        tuned = self._tuned_with_regressions(6)
        md = report.render_report(tuned, rows)
        assert "owner review" in md.lower()

    def test_few_regressions_not_flagged(self, tmp_path):
        rows = report.read_matrix_csv(_fixture_matrix_csv(tmp_path))
        tuned = self._tuned_with_regressions(1)
        md = report.render_report(tuned, rows)
        assert "owner review" not in md.lower()

    def test_study_section_present_when_summary_given(self, tmp_path):
        rows = report.read_matrix_csv(_fixture_matrix_csv(tmp_path))
        md = report.render_report(_tuned_json_defaults(), rows,
                                  study={"study_name": "s", "n_trials": 4, "best_value": -0.88})
        assert "Study summary" in md
        assert "-0.88" in md
