"""P3 report gates: the five required sections exist and carry the numbers."""

import json
import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

os.environ.setdefault("ANONYMIZED_TELEMETRY", "False")

from llamaindex_harness import evaluate, report


def _results():
    entries = [
        {"id": "E001", "query": "q1", "expectedHash": "h1",
         "targetProjectId": "ai-raccoon", "targetScope": "project",
         "searchLimit": 5, "negativeTest": False},
        {"id": "E002", "query": "q2", "expectedHash": "h2",
         "targetProjectId": "ai-raccoon", "targetScope": "shared",
         "searchLimit": 5, "negativeTest": False},
    ]
    out = evaluate.run_eval(
        entries,
        lambda e: {"hashes": [e["expectedHash"]], "fts_hit": 1, "vector_hit": 0},
        lambda e: {"hashes": []} if e["id"] == "E002" else {"hashes": [e["expectedHash"]]})
    return out


def _context():
    return {"date": "2026-09-08", "corpus": "eval-set-100.json", "corpus_size": 100,
            "copy_entries": 53740, "store_rows": 11800, "content": 11800,
            "structure": 9000, "fts": 11800, "headed": 9000,
            "buckets": ["ai-raccoon/custom", "ai-raccoon/project"],
            "model": "Salesforce/SFR-Embedding-Code-400M_R", "model_bytes": "1.6G",
            "store_bytes": "99M", "structure_alpha": 0.5}


def test_report_lists_stale_anchors():
    out = _results()
    out["staleAnchors"] = ["E009"]
    text = report.render(out, _context())
    assert "Stale anchors (1" in text and "E009" in text


def test_report_has_all_five_required_sections():
    text = report.render(_results(), _context())
    for header in ("## Scope and routing", "## Method", "## Per-query results",
                   "## Aggregates", "## Parity-gap discussion"):
        assert header in text


def test_report_carries_the_numbers_and_the_restriction():
    text = report.render(_results(), _context())
    assert "E001" in text and "E002" in text
    assert "0.500" in text  # ai-raccoon hit-rate 1/2
    assert "restriction" in text.lower()  # n=2 of 100: the subset must be stated
    assert "uniform limit 8" in text


def test_report_states_null_mcc_with_reason():
    out = _results()
    out["summary"]["mcc"] = None
    out["summary"]["mcc_reason"] = "zero denominator (a=1,b=1,c=0,d=0)"
    text = report.render(out, _context())
    assert "zero denominator" in text


def test_main_writes_markdown(tmp_path):
    results_path = tmp_path / "results.json"
    results_path.write_text(json.dumps(_results()))
    out_path = tmp_path / "report.md"
    assert report.main(["--results", str(results_path), "--out", str(out_path),
                        "--context", json.dumps(_context())]) == 0
    text = out_path.read_text()
    assert text.startswith("# LlamaIndex fusion harness")
    assert "## Aggregates" in text
