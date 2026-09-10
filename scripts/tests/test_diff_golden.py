"""F3 gate: the golden tolerance diff is an in-tree, testable artifact.

The P3 lane's diff lived in a /tmp transcript. This module pins the diff's
contract: exact hashes/hits/contingency/MCC, mean-F1 +/-1e-9, sessionId and
provenance pins allow-listed, staleAnchors compared, and the additive P2
`repeats` block accepted (its means must agree with the summary it decorates).
"""

import copy
import importlib.util
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DIFF_PATH = REPO / "scripts" / "retrieval_tuning" / "diff_golden.py"
GOLDEN = REPO / "docs" / "work" / "results-f1.json"
P2_REPEATS = REPO / "docs" / "work" / "results-f1-repeats.json"


def _load():
    spec = importlib.util.spec_from_file_location("diff_golden", DIFF_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _artifact() -> dict:
    return {
        "summary": {
            "n": 1, "n_paired": 1,
            "harness": {"n": 1, "hit_rate": 0.5, "mean_f1": 0.5},
            "airaccoon": {"n": 1, "hit_rate": 1.0, "mean_f1": 1.0},
            "contingency": {"a": 1, "b": 0, "c": 0, "d": 0},
            "mcc": 0.5,
            "gaps": {"n_paired": 1, "c_cell": 0, "c_fts_only": 0,
                     "c_vec_only": 0, "c_both_legs": 0, "c_neither_leg": 0,
                     "c_unknown": 0},
        },
        "rows": [{
            "id": "C001", "query": "q", "targetProjectId": "p",
            "targetScope": "project", "expectedHash": "h",
            "harness": {"hashes": ["h"], "hit": 1, "precision": 1.0,
                        "recall": 1.0, "f1": 1.0, "fts_hit": 1, "vector_hit": 1},
            "airaccoon": {"hashes": ["h"], "hit": 1, "precision": 1.0,
                          "recall": 1.0, "f1": 1.0, "error": None},
        }],
        "sessionId": "run-a",
        "staleAnchors": ["C035"],
        "modelRevision": "rev", "modelBytes": 1,
        "copyPath": "/tmp/copy.db", "copySnapshotSha256": "s",
        "corpusSnapshotSha256": "s", "excludedProjects": [],
        "resolvedBuckets": ["p"],
    }


def test_identical_artifact_is_clean():
    mod = _load()
    assert mod.diff(_artifact(), _artifact()) == []


def test_session_id_and_provenance_pins_are_allow_listed():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["sessionId"] = "run-b"
    cand["modelRevision"] = "other-rev"
    cand["modelBytes"] = 99
    cand["copyPath"] = "/elsewhere"
    cand["copySnapshotSha256"] = "other"
    cand["corpusSnapshotSha256"] = "other"
    cand["excludedProjects"] = [{"projectId": "x"}]
    cand["resolvedBuckets"] = ["p", "q"]
    assert mod.diff(golden, cand) == []


def test_a_moved_hash_fails():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["rows"][0]["harness"]["hashes"] = ["other"]
    problems = mod.diff(golden, cand)
    assert any("hashes" in p for p in problems)


def test_stale_anchors_are_compared():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["staleAnchors"] = []
    assert any("staleAnchors" in p for p in mod.diff(golden, cand))


def test_mean_f1_tolerance_is_one_e_minus_nine():
    mod = _load()
    golden = _artifact()
    inside = copy.deepcopy(golden)
    inside["summary"]["harness"]["mean_f1"] = 0.5 + 5e-10
    assert mod.diff(golden, inside) == []
    outside = copy.deepcopy(golden)
    outside["summary"]["harness"]["mean_f1"] = 0.5 + 2e-9
    assert any("mean_f1" in p for p in mod.diff(golden, outside))


def test_contingency_and_mcc_are_exact():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["summary"]["contingency"] = {"a": 0, "b": 1, "c": 0, "d": 0}
    assert any("contingency" in p for p in mod.diff(golden, cand))
    cand = copy.deepcopy(golden)
    cand["summary"]["mcc"] = 0.5000000001
    assert any("mcc" in p for p in mod.diff(golden, cand))


def test_unknown_top_level_key_fails():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["surprise"] = 1
    assert any("surprise" in p for p in mod.diff(golden, cand))


def test_additive_taxonomy_and_repeats_blocks_are_accepted_when_consistent():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["summary"]["gaps"]["taxonomy"] = {"n_paired": 1, "c_cell": 0,
                                           "cells": {"none": 1}, "unknownShare": 0.0}
    cand["repeats"] = {
        "n": 3,
        "metrics": {
            "harness_hit_rate": {"mean": 0.5, "min": 0.5, "max": 0.5},
            "airaccoon_hit_rate": {"mean": 1.0, "min": 1.0, "max": 1.0},
            "harness_mean_f1": {"mean": 0.5, "min": 0.5, "max": 0.5},
            "airaccoon_mean_f1": {"mean": 1.0, "min": 1.0, "max": 1.0},
            "mcc": {"mean": 0.5, "min": 0.5, "max": 0.5, "nullCount": 0},
        },
        "unstable": {"harness": [], "airaccoon": []},
    }
    assert mod.diff(golden, cand) == []


def test_repeats_means_must_agree_with_the_summary_and_unstable_must_be_empty():
    mod = _load()
    golden = _artifact()
    cand = copy.deepcopy(golden)
    cand["repeats"] = {
        "n": 3,
        "metrics": {
            "harness_hit_rate": {"mean": 0.9, "min": 0.9, "max": 0.9},
            "airaccoon_hit_rate": {"mean": 1.0, "min": 1.0, "max": 1.0},
            "harness_mean_f1": {"mean": 0.5, "min": 0.5, "max": 0.5},
            "airaccoon_mean_f1": {"mean": 1.0, "min": 1.0, "max": 1.0},
            "mcc": {"mean": 0.5, "min": 0.5, "max": 0.5, "nullCount": 0},
        },
        "unstable": {"harness": ["C001"], "airaccoon": []},
    }
    problems = mod.diff(golden, cand)
    assert any("harness_hit_rate" in p for p in problems)
    assert any("unstable.harness" in p for p in problems)


def test_the_committed_p2_repeats_artifact_passes_against_golden_c():
    """The real shape, not a fixture: golden C vs the P2 repeats artifact."""
    mod = _load()
    golden = json.loads(GOLDEN.read_text())
    candidate = json.loads(P2_REPEATS.read_text())
    assert mod.diff(golden, candidate) == []
