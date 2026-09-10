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


# --- P1 report gates: data-driven buckets, gaps, exclusions, recompute path ---

def _results_with_gaps():
    out = _results()
    out["summary"]["gaps"] = {"n_paired": 2, "c_cell": 1, "c_fts_only": 1,
                              "c_vec_only": 0, "c_both_legs": 0,
                              "c_neither_leg": 0, "c_unknown": 0}
    out["excludedProjects"] = [
        {"projectId": "aib", "canonicalId": "ai-badger", "embeddedRows": 1,
         "reason": "raw entries.project_id 'aib' folds to canonical 'ai-badger'"}]
    return out


def test_report_bucket_prose_is_data_driven():
    # Today report.py hardcodes the 2-bucket rule in prose; the buckets must
    # come from the results/context or a third bucket silently vanishes.
    out = _results()
    out["resolvedBuckets"] = ["jsaa", "ai-raccoon"]
    ctx = _context()
    ctx["buckets"] = ["jsaa/project", "ai-raccoon/project", "ai-raccoon/shared"]
    text = report.render(out, ctx)
    assert "jsaa" in text
    assert "hermes-default" not in text


def test_report_separates_resolved_buckets_from_shared_spellings():
    # F3: resolvedBuckets are the ingest rule's inputs; observed aib/shared and
    # job-search-ai-assistant/shared are extra spellings whose rows are global
    # and servable. They must not read as "project buckets".
    out = _results()
    out["resolvedBuckets"] = ["ai-raccoon", "jsaa"]
    ctx = _context()
    ctx["bucket_counts"] = {
        "ai-raccoon/project": 100,
        "aib/shared": 1,
        "job-search-ai-assistant/shared": 8,
    }
    text = report.render(out, ctx)
    assert "Resolved project buckets (2)" in text
    assert "ai-raccoon" in text and "jsaa" in text
    assert "Additional shared-tier spellings" in text
    assert "aib/shared (1)" in text
    assert "job-search-ai-assistant/shared (8)" in text


def test_report_exclusion_discloses_committed_vs_shared_counts():
    # F2: the manifest split must be rendered — committed rows are unservable,
    # the shared rows are ingested and served by shared/all.
    out = _results_with_gaps()
    out["excludedProjects"] = [
        {"projectId": "job-search-ai-assistant", "canonicalId": "jsaa",
         "embeddedRows": 114, "committedRows": 106, "sharedRows": 8,
         "reason": "committed project/custom rows can never be served; "
                   "scope='shared' rows are served by shared/all"}]
    text = report.render(out, _context())
    assert "committed=106" in text and "shared=8" in text
    assert "job-search-ai-assistant" in text and "jsaa" in text


def test_report_renders_copy_and_model_provenance():
    # C3: the golden must state which weights and which copy it was measured
    # against, so a later drift is visible, not silent.
    out = _results()
    out["modelRevision"] = "cb950dc80d677c6fdc00f56c8ddd20ca2642c59e"
    out["modelBytes"] = 869254400
    out["copyPath"] = "/tmp/p1-live-copy.db"
    out["copySnapshotSha256"] = "e0434a7214ac4caf1dbbef56147f582515bd0f5ebda665ad8cb06687296a55f6"
    text = report.render(out, _context())
    assert "cb950dc8" in text
    assert "e0434a72" in text
    assert "869254400" in text


def test_report_renders_gap_counts_table():
    text = report.render(_results_with_gaps(), _context())
    assert "c_fts_only" in text and "c_cell" in text
    assert "1" in text  # the counted values are rendered, not just headers


def test_report_discloses_exclusions():
    text = report.render(_results_with_gaps(), _context())
    assert "aib" in text and "ai-badger" in text
    assert "excluded" in text.lower()


def test_report_carries_recompute_path_line():
    # The hash-preserving golden allows post-hoc relevance metrics; the report
    # must say how a rerun recomputes from results.json without new retrieval.
    text = report.render(_results(), _context())
    assert "recompute" in text.lower()


def test_report_no_restriction_line_when_stale_accounts_the_gap():
    # Full-100 shape: 99 scored + C034 null-filtered (stale, unscored) = 100
    # accounted — the SUBSET disclaimer must NOT fire. C026 is scored (into d)
    # AND stale-flagged, so only unscored stale ids close the gap.
    out = _results()
    out["rows"] = [dict(r, id=f"E{i:03d}") for i, r in enumerate(out["rows"] * 50)][:99]
    out["rows"][0]["id"] = "C026"
    out["summary"]["n"] = 99
    out["staleAnchors"] = ["C026", "C034"]
    text = report.render(out, _context())
    assert "SUBSET eval" not in text
    assert "C026" in text and "C034" in text
