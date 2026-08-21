"""Unit tests for the retrieval-tuning matrix driver (plan §3.1, §8).

Covers the pure parts only: ladder definitions, baseline, config enumeration,
CSV shape, and the sweep-validation rules that make matrix.py exit non-zero
when a knob's sweep is empty or the baseline row is missing. The live sweep
needs the harness (retrieval_tuning.evaluate) from another lane and is not
exercised here.
"""

import csv
import io
import json

import pytest

from retrieval_tuning import matrix


EXPECTED_DEFAULTS = {
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

EXPECTED_LADDERS = {
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

EXPECTED_CONFIG_COUNT = 1 + 6 + 6 + 6 + 6 + 6 + 2 + 2 + 5 + 2  # baseline + ladder points


class TestDefaults:
    def test_baseline_has_exactly_the_nine_knobs(self):
        assert set(matrix.DEFAULTS) == set(EXPECTED_DEFAULTS)

    def test_baseline_values_match_plan(self):
        assert matrix.DEFAULTS == EXPECTED_DEFAULTS


class TestLadders:
    def test_ladders_match_plan_section_3_1(self):
        assert matrix.LADDERS == EXPECTED_LADDERS

    def test_default_is_a_ladder_point_for_every_knob(self):
        for knob, default in matrix.DEFAULTS.items():
            assert default in matrix.LADDERS[knob], f"{knob} default {default} not in its ladder"

    def test_ladder_values_keep_their_types(self):
        # Integer knobs are ints; float knobs are int-or-float (the plan writes the
        # ladder point '0' without a decimal — numerically identical to 0.0, and
        # str() renders both to a double the settings verb accepts). Bools excluded.
        for knob in ("rrfK", "ftsWeight", "vectorWeight"):
            assert all(isinstance(v, int) and not isinstance(v, bool) for v in matrix.LADDERS[knob])
        for knob in ("sourceLambda", "consolidationThreshold", "structureAlpha"):
            assert all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in matrix.LADDERS[knob])
            assert all(float(v) == v for v in matrix.LADDERS[knob])


class TestBuildConfigs:
    def test_config_count_is_42(self):
        configs = matrix.build_configs()
        assert len(configs) == EXPECTED_CONFIG_COUNT == 42

    def test_first_config_is_the_baseline(self):
        configs = matrix.build_configs()
        assert configs[0]["id"] == "baseline"
        assert configs[0]["knob"] == "baseline"
        assert configs[0]["settings"] == matrix.DEFAULTS

    def test_every_config_holds_others_at_baseline(self):
        configs = matrix.build_configs()
        for cfg in configs[1:]:
            knob, value = cfg["knob"], cfg["value"]
            assert knob in matrix.LADDERS
            assert value in matrix.LADDERS[knob]
            settings = cfg["settings"]
            assert settings[knob] == value
            for other in matrix.DEFAULTS:
                if other != knob:
                    assert settings[other] == matrix.DEFAULTS[other], (
                        f"{cfg['id']} drifted {other}: {settings[other]} != {matrix.DEFAULTS[other]}"
                    )

    def test_config_ids_are_unique(self):
        ids = [cfg["id"] for cfg in matrix.build_configs()]
        assert len(ids) == len(set(ids))


class TestCsvShape:
    EXPECTED_COLUMNS = [
        "config_id", "knob", "value", "dataset", "corpus_size",
        "mean_ndcg5", "mean_mrr5", "hit3_rate", "hit1_rate",
        "per_query_json", "per_category_json",
    ]

    def _fake_rows(self):
        return [
            {
                "config_id": "baseline", "knob": "baseline", "value": "default",
                "dataset": "sextant", "corpus_size": 9,
                "mean_ndcg5": 0.5, "mean_mrr5": 0.25, "hit3_rate": 0.75, "hit1_rate": 0.5,
                "per_query_json": json.dumps([{"id": "q1", "ndcg5": 1.0, "mrr5": 1.0}]),
                "per_category_json": json.dumps({"file-targeted": {"hit1_rate": 0.5}}),
            },
            {
                "config_id": "rrfK=1", "knob": "rrfK", "value": 1,
                "dataset": "memory", "corpus_size": 22509,
                "mean_ndcg5": 0.4, "mean_mrr5": 0.2, "hit3_rate": 0.6, "hit1_rate": 0.4,
                "per_query_json": json.dumps([{"id": "q1", "ndcg5": 0.5}]),
                "per_category_json": json.dumps({"non-file": {"hit1_rate": 0.0}}),
            },
        ]

    def test_write_then_read_roundtrip(self, tmp_path):
        out = tmp_path / "matrix.csv"
        matrix.write_matrix_csv(self._fake_rows(), str(out))
        with open(out, newline="") as fh:
            rows = list(csv.DictReader(fh))
        assert rows[0]["config_id"] == "baseline"
        assert rows[1]["knob"] == "rrfK"
        assert rows[1]["value"] == "1"
        assert json.loads(rows[1]["per_query_json"]) == [{"id": "q1", "ndcg5": 0.5}]

    def test_header_is_the_documented_column_set(self, tmp_path):
        out = tmp_path / "matrix.csv"
        matrix.write_matrix_csv(self._fake_rows(), str(out))
        with open(out, newline="") as fh:
            reader = csv.reader(fh)
            header = next(reader)
        assert header == self.EXPECTED_COLUMNS


def _rows_for(configs, dataset="sextant", corpus_size=9):
    """Turn enumerated configs into sweep rows (as the live driver would)."""
    rows = []
    for cfg in configs:
        rows.append({
            "config_id": cfg["id"], "knob": cfg["knob"], "value": cfg["value"],
            "dataset": dataset, "corpus_size": corpus_size,
            "mean_ndcg5": 0.5, "mean_mrr5": 0.25, "hit3_rate": 0.75, "hit1_rate": 0.5,
            "per_query_json": json.dumps([]),
            "per_category_json": json.dumps({}),
        })
    return rows


class TestValidateSweep:
    def test_complete_sweep_is_clean(self):
        rows = _rows_for(matrix.build_configs())
        assert matrix.validate_sweep(rows, matrix.LADDERS) == []

    def test_baseline_row_missing_is_a_problem(self):
        rows = _rows_for(matrix.build_configs()[1:])  # drop baseline
        problems = matrix.validate_sweep(rows, matrix.LADDERS)
        assert any("baseline" in p for p in problems)

    def test_empty_knob_sweep_is_a_problem(self):
        rows = [r for r in _rows_for(matrix.build_configs()) if r["knob"] != "rrfK"]
        problems = matrix.validate_sweep(rows, matrix.LADDERS)
        assert any("rrfK" in p and "empty" in p for p in problems)

    def test_missing_ladder_point_is_a_problem(self):
        rows = [r for r in _rows_for(matrix.build_configs()) if r["value"] != 5 or r["knob"] != "rrfK"]
        problems = matrix.validate_sweep(rows, matrix.LADDERS)
        assert any("rrfK" in p and "5" in p for p in problems)

    def test_validation_returns_problems_instead_of_raising(self):
        problems = matrix.validate_sweep([], matrix.LADDERS)
        assert isinstance(problems, list)
        assert problems  # empty rows -> baseline missing + every sweep empty


class TestMetricsAdapter:
    def test_adapter_reads_the_documented_metrics_attributes(self):
        class FakeQueryScore:
            def as_dict(self):
                return {"id": "q1", "ndcg5": 1.0, "mrr5": 1.0, "hit3": 1, "hit1": 1}

        class FakeMetrics:
            mean_ndcg5 = 0.6
            mean_mrr5 = 0.3
            hit3_rate = 0.8
            hit1_rate = 0.5
            per_query = [FakeQueryScore()]
            per_category = {"file-targeted": {"hit1_rate": 0.5}}

        row = matrix.metrics_to_row(FakeMetrics())
        assert row["mean_ndcg5"] == 0.6
        assert row["hit1_rate"] == 0.5
        assert json.loads(row["per_query_json"]) == [{"id": "q1", "ndcg5": 1.0, "mrr5": 1.0, "hit3": 1, "hit1": 1}]

    def test_adapter_fails_loud_on_missing_attribute(self):
        class SparseMetrics:
            mean_ndcg5 = 0.6

        with pytest.raises(KeyError, match="mean_mrr5"):
            matrix.metrics_to_row(SparseMetrics())
