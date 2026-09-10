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


def _results_c_cell_taxonomy():
    """E001 both-hit (none), E002 bank-only + fts window hit (fusion),
    E003 bank-only + no leg window (embedding)."""
    entries = [
        {"id": "E001", "query": "plain q1", "expectedHash": "h1",
         "targetProjectId": "ai-raccoon", "targetScope": "project"},
        {"id": "E002", "query": "plain q2", "expectedHash": "h2",
         "targetProjectId": "ai-raccoon", "targetScope": "shared"},
        {"id": "E003", "query": "plain q3", "expectedHash": "h3",
         "targetProjectId": "ai-raccoon", "targetScope": "project"},
    ]
    harness = {
        "E001": {"hashes": ["h1"], "fts_hit": 1, "vector_hit": 1, "error": None},
        "E002": {"hashes": [], "fts_hit": 1, "vector_hit": 0, "error": None},
        "E003": {"hashes": [], "fts_hit": 0, "vector_hit": 0, "error": None},
    }
    bank = {
        "E001": {"hashes": ["h1"], "error": None},
        "E002": {"hashes": ["h2"], "error": None},
        "E003": {"hashes": ["h3"], "error": None},
    }
    return evaluate.run_eval(entries, lambda e: dict(harness[e["id"]]),
                             lambda e: dict(bank[e["id"]]))


def test_report_renders_gap_counts_table():
    # P2 AC2: the single classified taxonomy REPLACES the P1 provisional counts
    # table — the provisional c_* names must not survive as a second taxonomy.
    text = report.render(_results_c_cell_taxonomy(), _context())
    assert "Classified gap taxonomy" in text
    assert "| fusion |" in text
    assert "embedding" in text and "unrecoverable" in text and "unknown" in text
    assert "c_fts_only" not in text
    assert "of 3 paired" in text
    # per-row evidence: every c-cell row carries its classification
    row_fusion = next(l for l in text.splitlines() if l.startswith("| E002 |"))
    row_embedding = next(l for l in text.splitlines() if l.startswith("| E003 |"))
    assert "fusion" in row_fusion
    assert "embedding" in row_embedding


def test_report_gap_taxonomy_conservation_and_oracle_labels():
    # C10 oracle: the shared-scope row (fts window hit) classifies fusion, and
    # the cells conserve: none+fusion+embedding+unrecoverable+unknown = paired.
    out = _results_c_cell_taxonomy()
    tax = evaluate.gap_taxonomy(out["rows"])
    assert sum(tax["cells"].values()) == tax["n_paired"] == 3
    assert tax["c_cell"] == 2
    text = report.render(out, _context())
    shared_line = next(l for l in text.splitlines() if l.startswith("| E002 |"))
    assert "shared" in shared_line and "fusion" in shared_line
    assert "0.0%" in text  # unknown share rendered in the taxonomy table


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
    # Full-100 shape: 99 scored + 1 stale null-filtered (unscored) = 100
    # accounted — the SUBSET disclaimer must NOT fire, and the C9 invariant
    # (staleAnchors ∩ scored = ∅) holds because the stale ids are unscored.
    out = _results()
    out["rows"] = [dict(r, id=f"E{i:03d}") for i, r in enumerate(out["rows"] * 50)][:99]
    out["summary"]["n"] = 99
    out["staleAnchors"] = ["C034", "E099"]
    text = report.render(out, _context())
    assert "SUBSET eval" not in text
    assert "C034" in text and "E099" in text


# --- C9 query-composition disclosure: signature-based strata + stale invariant ---

def test_stratification_is_signature_based_not_id_based():
    # C9: the debris/clean split is recomputed from the query text, never a
    # hardcoded id list (ids shift with corpus regeneration).
    rows = [
        {"id": "X1", "query": "see https://example.com/a for details",
         "harness": {"hit": 1, "f1": 0.5}, "airaccoon": {"hit": 0, "f1": 0.0}},
        {"id": "X2", "query": "plain prose query about dispatch contracts",
         "harness": {"hit": 0, "f1": 0.0}, "airaccoon": {"hit": 1, "f1": 0.5}},
    ]
    strata = report.stratify_rows(rows)
    assert [r["id"] for r in strata["debris"]] == ["X1"]
    assert [r["id"] for r in strata["clean"]] == ["X2"]
    stats = report.stratum_stats(strata["debris"])
    assert stats["n"] == 1
    assert stats["harness_hit_rate"] == 1.0
    assert stats["bank_hit_rate"] == 0.0
    stats = report.stratum_stats(strata["clean"])
    assert stats["n"] == 1
    assert stats["harness_hit_rate"] == 0.0
    assert stats["bank_hit_rate"] == 1.0


def test_report_renders_stratified_rates_and_composition_disclosure():
    # C9: the report must carry the recomputed stratified hit-rates and say
    # the relevance-flavoured readings are composition-sensitive while the
    # parity reading stands.
    out = _results()
    out["rows"][0]["query"] = 'JSON debris {"line": "x"} copied from a tool call'
    text = report.render(out, _context())
    assert "Query-composition stratification" in text
    assert "debris" in text and "clean" in text
    assert "composition-sensitive" in text
    assert "1.000" in text or "0.500" in text  # rendered rates, not just headers


def test_report_asserts_stale_anchors_are_never_scored():
    # C9: staleAnchors ∩ scored == ∅ is asserted, not merely asserted-in-prose.
    out = _results()
    out["staleAnchors"] = ["E001"]  # E001 is a scored row: the invariant is broken
    with pytest.raises(ValueError, match="stale"):
        report.render(out, _context())
    out["staleAnchors"] = ["E009"]
    text = report.render(out, _context())
    assert "staleAnchors" in text and "scored" in text


def test_report_marks_shared_scope_rows_as_fusion_drop():
    # C10: the shared-scope rows are fusion-drop (a leg held the anchor in the
    # window; the Take(8) limit dropped it once the leg scores diverged) and
    # must never be labelled "embedding-gap evidence".
    out = _results()  # E002 is targetScope=shared
    text = report.render(out, _context())
    c10 = next(line for line in text.splitlines() if line.startswith("C10 trace note"))
    assert "E002" in c10 and "fusion-drop" in c10
    assert "embedding-gap evidence" not in text.replace(c10, "")  # P1 label gone elsewhere


# --- P2 AC2 report gates: cap + frozen-golden oracle ---

def test_report_rejects_unknown_share_above_cap():
    # Bar: unknown share <= 5%. With no leg diagnostics on c-cell rows the
    # classifier cannot speak, so publishing must fail loud, not guess.
    out = _results_c_cell_taxonomy()
    for row in out["rows"]:
        row["harness"].pop("fts_hit", None)
        row["harness"].pop("vector_hit", None)
    with pytest.raises(ValueError, match="unknown share"):
        report.render(out, _context())


def _results_with_repeats():
    out = _results_c_cell_taxonomy()
    out["repeats"] = {
        "n": 3,
        "modelRevision": "cb950dc80d677c6fdc00f56c8ddd20ca2642c59e",
        "modelBytes": 869254400,
        "metrics": {
            "harness_hit_rate": {"mean": 0.6667, "min": 0.6667, "max": 0.6667},
            "airaccoon_hit_rate": {"mean": 0.6667, "min": 0.6667, "max": 0.6667},
            "harness_mean_f1": {"mean": 0.3333, "min": 0.3333, "max": 0.3333},
            "airaccoon_mean_f1": {"mean": 0.3333, "min": 0.3333, "max": 0.3333},
            "mcc": {"mean": 0.25, "min": 0.2, "max": 0.3, "nullCount": 0},
        },
        "unstable": {"harness": [], "airaccoon": []},
    }
    return out


def test_report_renders_repeat_spread_mean_min_max():
    # P2 AC1: the report renders mean [min-max] per metric and the unstable-id
    # list, so a reader sees the campaign spread, not a single number.
    text = report.render(_results_with_repeats(), _context())
    assert "Repeat-run spread" in text
    assert "0.6667 [0.6667\u20130.6667]" in text
    assert "0.2500 [0.2000\u20130.3000]" in text
    assert "Unstable served-set query ids" in text
    assert "harness=none" in text


def test_report_states_what_repeats_guard():
    # The gate must say which variance repeats guard: weight drift, server
    # nondeterminism, bank drift — never an unexplained spread.
    text = report.render(_results_with_repeats(), _context())
    assert "Variance guarded" in text
    assert "weight" in text.lower()
    assert "server nondeterminism" in text
    assert "bank drift" in text


def test_report_renders_unstable_ids_when_present():
    out = _results_with_repeats()
    out["repeats"]["unstable"] = {"harness": ["C045"], "airaccoon": ["C066"]}
    text = report.render(out, _context())
    assert "C045" in text and "C066" in text


def test_frozen_golden_c_cell_is_fusion_with_shared_oracle_rows():
    # P2 AC2 oracle against the frozen golden (not a synthetic fixture): every
    # c-cell row classifies `fusion` (a leg window held it; the rows are the
    # Known fusion/limit drops), no row is labelled "embedding gap", and the
    # targetScope=shared rows — the C10 oracle — are all fusion. Ids are
    # selected by targetScope, never hardcoded (they shift on regeneration).
    golden = (Path(__file__).resolve().parents[2] / "docs" / "work"
              / "results-f1.json")
    if not golden.exists():
        pytest.skip("frozen golden not present in this checkout")
    results = json.loads(golden.read_text())
    labels = evaluate.gap_columns(results["rows"])
    tax = evaluate.gap_taxonomy(results["rows"])
    gaps = results["summary"]["gaps"]
    assert tax["c_cell"] == gaps["c_cell"]
    assert tax["cells"]["fusion"] == gaps["c_cell"]
    assert tax["cells"]["embedding"] == 0
    assert tax["cells"]["unrecoverable"] == 0
    assert tax["cells"]["unknown"] == 0
    assert sum(tax["cells"].values()) == tax["n_paired"]
    shared = [r["id"] for r in results["rows"]
              if r.get("targetScope") == "shared"]
    assert shared, "frozen pair must carry shared-scope rows (C10 oracle)"
    for qid in shared:
        assert labels[qid] == "fusion"
