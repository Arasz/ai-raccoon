"""P3 eval gates: singleton-F1 math, agreement-MCC incl. null-with-reason,
transport-failure pairwise exclusion, corpus-contract fail-loud, N x 2 shape."""

import json
import math
import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

os.environ.setdefault("ANONYMIZED_TELEMETRY", "False")

from llamaindex_harness import evaluate


def _entry(i, **over):
    base = {"id": f"E{i:03d}", "query": f"query {i}", "expectedHash": f"hash{i:03d}",
            "targetProjectId": "ai-raccoon", "targetScope": "project",
            "searchLimit": 5, "negativeTest": False}
    base.update(over)
    return base


def test_f1_hit_divides_by_served_set_size():
    # h=1, |R|=4 -> P=0.25, R=1, F1=2*0.25/1.25=0.4.
    hit, precision, recall, f1 = evaluate.f1_singleton("h", ["a", "b", "h", "c"])
    assert (hit, precision, recall) == (1, pytest.approx(0.25), 1.0)
    assert f1 == pytest.approx(0.4)


def test_f1_miss_and_empty_are_zero():
    assert evaluate.f1_singleton("h", ["a", "b"])[0] == 0
    assert evaluate.f1_singleton("h", ["a", "b"])[3] == 0.0
    assert evaluate.f1_singleton("h", []) == (0, 0.0, 0, 0.0)


def test_f1_match_is_exact_hash_not_prefix_or_source():
    # Fork from scoring.resolve_gain (prefix + source fallback): both systems
    # serve full content hashes, so a prefix must NOT count as a parity hit.
    assert evaluate.f1_singleton("abcdef", ["abc"])[0] == 0
    assert evaluate.f1_singleton("abcdef", ["abcdef"])[0] == 1


def test_mcc_agreement_hand_worked():
    # a=50 b=10 c=5 d=35: (1750-50)/sqrt(60*55*45*40).
    mcc, reason = evaluate.mcc_agreement([1] * 50 + [1] * 10 + [0] * 5 + [0] * 35,
                                         [1] * 50 + [0] * 10 + [1] * 5 + [0] * 35)
    assert reason is None
    assert mcc == pytest.approx(1700 / math.sqrt(60 * 55 * 45 * 40))


def test_mcc_null_with_reason_on_zero_denominator():
    # Both systems hit everything: single-system actual is constant, so the
    # denominator is zero — report hit-rate instead (plan: degenerate MCC).
    mcc, reason = evaluate.mcc_agreement([1, 1, 1], [1, 1, 1])
    assert mcc is None
    assert reason and "denominator" in reason


def test_transport_failures_excluded_pairwise():
    entries = [_entry(1), _entry(2), _entry(3)]
    harness = lambda e: {"hashes": [e["expectedHash"]], "error": None}  # noqa: E731
    def broken_server(e):  # noqa: E731
        if e["id"] == "E002":
            return {"hashes": [], "error": "MCP timeout"}
        return {"hashes": ["nope"], "error": None}
    out = evaluate.run_eval(entries, harness, broken_server)
    assert out["summary"]["n"] == 3
    assert out["summary"]["n_paired"] == 2
    cont = out["summary"]["contingency"]
    assert cont["a"] + cont["b"] + cont["c"] + cont["d"] == 2
    assert out["rows"][1]["airaccoon"]["error"] == "MCP timeout"
    assert out["summary"]["airaccoon"]["n"] == 2  # means over successes only


def test_negative_test_entry_fails_loud():
    with pytest.raises(ValueError, match="negativeTest"):
        evaluate.run_eval([_entry(1, negativeTest=True)],
                          lambda e: {"hashes": []}, lambda e: {"hashes": []})


def test_missing_expected_hash_fails_loud():
    with pytest.raises(ValueError, match="expectedHash"):
        evaluate.run_eval([_entry(1, expectedHash="")],
                          lambda e: {"hashes": []}, lambda e: {"hashes": []})


class _FakeMcpClient:
    """Seam-shaped stub: _call_tool returns a parsed tool payload, records args."""

    def __init__(self, payload):
        self.payload = payload
        self.calls = []

    def _call_tool(self, name, arguments):
        self.calls.append((name, arguments))
        return self.payload


class _FakeServer:
    def __init__(self, client):
        self.client = client


def test_airaccoon_fn_sends_session_id_and_extracts_hashes():
    # The scripts/src seam predates the required sessionId argument: the fn
    # must send it explicitly or the server refuses with invalid-argument.
    client = _FakeMcpClient({"results": [{"hash": "abc123"}]})
    fn = evaluate.build_airaccoon_fn(_FakeServer(client), session_id="sess-1")
    out = fn({"query": "q", "targetProjectId": "ai-raccoon",
              "targetScope": "project", "expectedHash": "abc123"})
    assert out == {"hashes": ["abc123"], "error": None}
    name, args = client.calls[0]
    assert name == "memory_search"
    assert args["sessionId"] == "sess-1"
    assert args["limit"] == 8 and args["kind"] == "memory"
    # Prod leg serves the same post-floor shape as the harness leg (floor 0.6).
    assert args["minRelativeScore"] == pytest.approx(0.6)


def test_missing_anchors_listed():
    assert evaluate.missing_anchors([_entry(1), _entry(2)], {"hash001"}) == ["E002"]
    assert evaluate.missing_anchors([_entry(1)], {"hash001"}) == []


def test_run_eval_progress_heartbeat(capsys):
    # Long-run heartbeat: 100 sequential server searches run ~30 min silent.
    entries = [_entry(i) for i in range(1, 13)]
    evaluate.run_eval(entries,
                        lambda e: {"hashes": [e["expectedHash"]]},
                        lambda e: {"hashes": []})
    assert "10/12" in capsys.readouterr().out


def test_run_eval_shape_is_n_rows_by_two_systems():
    entries = [_entry(1), _entry(2)]
    out = evaluate.run_eval(entries,
                            lambda e: {"hashes": [e["expectedHash"]]},
                            lambda e: {"hashes": []})
    assert len(out["rows"]) == 2
    for row in out["rows"]:
        assert set(row) >= {"id", "harness", "airaccoon"}
        assert row["harness"]["hit"] == 1 and row["airaccoon"]["hit"] == 0
    assert out["summary"]["harness"]["hit_rate"] == pytest.approx(1.0)
    assert out["summary"]["airaccoon"]["hit_rate"] == pytest.approx(0.0)


def test_clean_eval_passes_the_gate():
    entries = [_entry(1)]
    out = evaluate.run_eval(entries,
                            lambda e: {"hashes": [e["expectedHash"]]},
                            lambda e: {"hashes": [e["expectedHash"]]})
    assert evaluate.eval_gate_failures(out) == []


def test_all_prod_errors_fail_the_gate():
    # Every ai-raccoon leg erroring must fail the gate: otherwise a run
    # with a dead prod leg would publish harness-only numbers as a comparison.
    entries = [_entry(1), _entry(2)]
    out = evaluate.run_eval(entries,
                            lambda e: {"hashes": [e["expectedHash"]]},
                            lambda e: {"hashes": [], "error": "ConnectionError: refused"})
    failures = evaluate.eval_gate_failures(out)
    assert any("ai-raccoon errors on 2 queries" in f for f in failures)
    assert any("zero paired rows" in f for f in failures)


def test_harness_errors_still_fail_the_gate():
    entries = [_entry(1)]
    out = evaluate.run_eval(entries,
                            lambda e: {"hashes": [], "error": "IndexError: boom"},
                            lambda e: {"hashes": [e["expectedHash"]]})
    failures = evaluate.eval_gate_failures(out)
    assert any("harness errors on 1 queries" in f for f in failures)
    # Error rows are excluded from pairing, so a harness error also empties
    # the contingency and trips the zero-paired gate.
    assert out["summary"]["contingency"] == {"a": 0, "b": 0, "c": 0, "d": 0}
    assert any("zero paired rows" in f for f in failures)


# --- P1 eval gates: custom-scope bank leg, null-anchor filter, gap aggregates ---

def test_airaccoon_fn_maps_custom_scope_to_project():
    # Bank leg of the three-legged mapping: corpus scope=custom is sent as
    # scope=project (bank scope=project covers custom labels per
    # SearchContexts.cs; the bank refuses scope=custom as invalid-params).
    client = _FakeMcpClient({"results": [{"hash": "abc123"}]})
    fn = evaluate.build_airaccoon_fn(_FakeServer(client), session_id="sess-1")
    out = fn({"query": "q", "targetProjectId": "ai-badger",
              "targetScope": "custom", "expectedHash": "abc123"})
    assert out == {"hashes": ["abc123"], "error": None}
    assert client.calls[0][1]["scope"] == "project"
    for scope, want in (("project", "project"), ("shared", "shared"), ("all", "all")):
        client2 = _FakeMcpClient({"results": []})
        evaluate.build_airaccoon_fn(_FakeServer(client2), session_id="s")(
            {"query": "q", "targetProjectId": "p", "targetScope": scope,
             "expectedHash": "h"})
        assert client2.calls[0][1]["scope"] == want


def test_null_expected_hash_fails_loud_in_run_eval():
    # Contract pin: run_eval still refuses null anchors — so main() must filter
    # them pre-run_eval (C034), never let them reach this raise.
    import pytest as _pytest
    with _pytest.raises(ValueError, match="expectedHash"):
        evaluate.run_eval(
            [_entry(1, expectedHash=None)],
            lambda e: {"hashes": []}, lambda e: {"hashes": []})


def test_partition_null_anchors():
    entries = [_entry(1), _entry(2, expectedHash=None, id="C034"),
               _entry(3, expectedHash="")]
    scorable, null_ids = evaluate.partition_null_anchors(entries)
    assert [e["id"] for e in scorable] == ["E001"]
    assert null_ids == ["C034", "E003"]


def test_aggregate_gaps_counts_sum_to_c_cell():
    # Provisional P1 counts (P2 refines into the taxonomy reusing these numbers):
    # every c-cell row (bank-hit/harness-miss) lands in exactly one bucket.
    rows = [
        {"id": "a", "harness": {"hit": 0, "fts_hit": 1, "vector_hit": 0},
         "airaccoon": {"hit": 1}},  # legs split: embedding-gap evidence
        {"id": "b", "harness": {"hit": 0, "fts_hit": 0, "vector_hit": 1},
         "airaccoon": {"hit": 1}},  # legs had it, fusion lost it
        {"id": "c", "harness": {"hit": 0, "fts_hit": 1, "vector_hit": 1},
         "airaccoon": {"hit": 1}},  # both legs hit, fusion lost it
        {"id": "d", "harness": {"hit": 0, "fts_hit": 0, "vector_hit": 0},
         "airaccoon": {"hit": 1}},  # neither leg: unrecoverable by fusion
        {"id": "e", "harness": {"hit": 0}, "airaccoon": {"hit": 1}},  # no leg cols
        {"id": "f", "harness": {"hit": 1, "fts_hit": 1, "vector_hit": 1},
         "airaccoon": {"hit": 1}},  # agreement: not a gap
        {"id": "g", "harness": {"hit": 0, "error": "boom"},
         "airaccoon": {"hit": 1}},  # unpaired: excluded, not a gap
    ]
    gaps = evaluate.aggregate_gaps(rows)
    assert gaps["c_cell"] == 5
    assert (gaps["c_fts_only"] + gaps["c_vec_only"] + gaps["c_both_legs"]
            + gaps["c_neither_leg"] + gaps["c_unknown"]) == gaps["c_cell"]
    assert (gaps["c_fts_only"], gaps["c_vec_only"], gaps["c_both_legs"],
            gaps["c_neither_leg"], gaps["c_unknown"]) == (1, 1, 1, 1, 1)


# --- C3 provenance gates (F4/F5) ---


def test_model_revision_check_fails_loud_on_mismatch_and_empty():
    # The eval must embed with the store's recorded revision, never whatever
    # the cache happens to hold; a missing/empty record or zero bytes is loud.
    assert evaluate.model_revision_check("cb950dc8", "cb950dc8", 869254400) == {
        "modelRevision": "cb950dc8", "modelBytes": 869254400}
    with pytest.raises(ValueError, match="embedded revision"):
        evaluate.model_revision_check("cb950dc8", "different", 869254400)
    with pytest.raises(ValueError, match="no modelRevision"):
        evaluate.model_revision_check(None, "cb950dc8", 869254400)
    with pytest.raises(ValueError, match="resolve empty"):
        evaluate.model_revision_check("cb950dc8", "cb950dc8", 0)


def test_scratch_copy_check_row_drift_fails_sha_only_warns(tmp_path):
    # The bank leg reads scratch-data-root/memory.db; row drift against the
    # store's copy would compare two universes -> fail. The scratch copy is
    # mutated by access bumps across repeat runs -> SHA-only change warns.
    import hashlib as _hashlib
    import sqlite3 as _sqlite3

    db = tmp_path / "memory.db"
    conn = _sqlite3.connect(db)
    conn.execute("CREATE TABLE entries (id INTEGER PRIMARY KEY, value TEXT)")
    conn.executemany("INSERT INTO entries (value) VALUES (?)", [("a",), ("b",)])
    conn.commit()
    conn.close()
    sha = _hashlib.sha256(db.read_bytes()).hexdigest()

    provenance, failures, warnings = evaluate.scratch_copy_check(db, sha, 2)
    assert failures == [] and warnings == []
    assert provenance["scratchRows"] == 2
    assert provenance["scratchSnapshotSha256"] == sha

    conn = _sqlite3.connect(db)
    conn.execute("UPDATE entries SET value='a-bumped' WHERE id=1")
    conn.commit()
    conn.close()
    provenance, failures, warnings = evaluate.scratch_copy_check(db, sha, 2)
    assert failures == [] and warnings and "sha" in warnings[0]

    conn = _sqlite3.connect(db)
    conn.execute("INSERT INTO entries (value) VALUES ('c')")
    conn.commit()
    conn.close()
    _, failures, _ = evaluate.scratch_copy_check(db, sha, 2)
    assert failures and "rows" in failures[0]

    _, failures, _ = evaluate.scratch_copy_check(tmp_path / "absent.db", sha, 2)
    assert failures


# --- P2 AC3: pure anchor verdict + stale list always on the write path ---

def test_anchor_verdict_refuse_warn_clean():
    # P2 AC3: the anchor gate is a pure verdict. refuse = no anchor resolves
    # (wrong store/copy); warn = some stale (re-chunked upstream); clean = none.
    assert evaluate.anchor_verdict(3, []) == "clean"
    assert evaluate.anchor_verdict(3, ["C001"]) == "warn"
    assert evaluate.anchor_verdict(3, ["C001", "C002", "C003"]) == "refuse"
    assert evaluate.anchor_verdict(1, ["C001"]) == "refuse"
    assert evaluate.anchor_verdict(0, []) == "clean"


def _run_main_with_fakes(monkeypatch, tmp_path, *, entries, stale,
                         harness_hashes=None, bank_hashes=None, extra_argv=()):
    """Patch main()'s heavy seams (provenance, scratch, server, retriever).

    Returns (argv, out_path); the caller calls evaluate.main(argv). Only the
    write path and the anchor verdict are real — everything expensive is fake."""
    store = tmp_path / "store"
    store.mkdir()
    (store / "params.json").write_text(json.dumps({
        "model": "Salesforce/SFR-Embedding-Code-400M_R",
        "modelRevision": "rev-cb950dc8",
        "counts": {"copyEntries": 1},
        "copyPath": "/tmp/p1-live-copy.db",
        "copySnapshotSha256": "e" * 64,
    }))
    corpus = tmp_path / "corpus.json"
    corpus.write_text(json.dumps(entries))
    scratch = tmp_path / "scratch"
    scratch.mkdir()
    (scratch / "memory.db").write_bytes(b"")
    out = tmp_path / "results.json"

    from llamaindex_harness import ingest as ingest_mod  # noqa: PLC0415
    monkeypatch.setattr(ingest_mod, "model_weights_info",
                        lambda model: ("rev-cb950dc8", 5))
    monkeypatch.setattr(ingest_mod, "check_pinned_revision", lambda revision: None)
    monkeypatch.setattr(
        evaluate, "scratch_copy_check",
        lambda *a, **k: ({"scratchSnapshotSha256": "e" * 64, "scratchRows": 1},
                         [], []))
    monkeypatch.setattr(evaluate, "check_anchors_resolve", lambda *a, **k: list(stale))

    import retrieval_tuning.server as server_mod  # noqa: PLC0415

    class _FakeServer:
        port = 50001
        client = None

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    monkeypatch.setattr(server_mod, "start_server", lambda *a, **k: _FakeServer())
    monkeypatch.setattr(
        evaluate, "build_airaccoon_fn",
        lambda server, session_id: (lambda e: {
            "hashes": list(bank_hashes or []), "error": None}))
    harness_fn = lambda e: {"hashes": list(harness_hashes or []), "error": None}
    harness_fn.close = lambda: None
    monkeypatch.setattr(evaluate, "build_harness_fn",
                        lambda store_dir, offline=False: harness_fn)
    argv = ["--corpus", str(corpus), "--store-dir", str(store),
            "--scratch-data-root", str(scratch), "--out", str(out), *extra_argv]
    return argv, out


def test_stale_anchors_always_recorded_even_when_empty(monkeypatch, tmp_path, capsys):
    # P2 AC3: the write path always carries staleAnchors (empty list included);
    # the one-line summary repeats the count.
    argv, out = _run_main_with_fakes(
        monkeypatch, tmp_path, entries=[_entry(1)], stale=[],
        harness_hashes=["hash001"], bank_hashes=["hash001"])
    assert evaluate.main(argv) == 0
    assert "stale=0" in capsys.readouterr().out
    assert json.loads(out.read_text())["staleAnchors"] == []


def test_stale_anchors_recorded_when_stale(monkeypatch, tmp_path, capsys):
    argv, out = _run_main_with_fakes(
        monkeypatch, tmp_path, entries=[_entry(1), _entry(2)], stale=["C035"],
        harness_hashes=["hash001"], bank_hashes=["hash001"])
    assert evaluate.main(argv) == 0
    out_text = capsys.readouterr().out
    assert "stale=1" in out_text and "C035" in out_text
    assert json.loads(out.read_text())["staleAnchors"] == ["C035"]


def test_refuse_verdict_writes_no_results_file(monkeypatch, tmp_path, capsys):
    # Pinned choice (P2 AC3): failure paths never write the artifact — a
    # results.json only ever exists for a run that passed its gates, so every
    # written artifact carries staleAnchors by construction.
    argv, out = _run_main_with_fakes(
        monkeypatch, tmp_path, entries=[_entry(1)], stale=["E001"],
        harness_hashes=[], bank_hashes=[])
    assert evaluate.main(argv) == 1
    assert not out.exists()
    assert "FAIL" in capsys.readouterr().out


# --- P2 AC2: gap taxonomy over the candidate window (existing columns only) ---

def _gap_row(qid, *, harness_hit, fts, vec, bank_hit=1, query="plain prose query",
             **over):
    harness = {"hit": harness_hit}
    if fts is not None:
        harness["fts_hit"] = fts
    if vec is not None:
        harness["vector_hit"] = vec
    row = {"id": qid, "query": query, "targetScope": "project",
           "harness": harness, "airaccoon": {"hit": bank_hit}}
    row.update(over)
    return row


def test_gap_columns_classification_table():
    # Labels are computed over fts_hit/vector_hit AS WINDOW HITS (the eval
    # computes them from the full candidate-window leg lists, max(limit*3,100)):
    # a leg window hit that the fused pipeline did not serve is FUSION, never
    # an embedding-miss label. "embedding"/"unrecoverable" need BOTH windows to
    # miss; the clean-vs-debris composition (C9) separates them.
    rows = [
        _gap_row("A", harness_hit=0, fts=1, vec=0),          # leg held it in-window
        _gap_row("B", harness_hit=0, fts=0, vec=1),
        _gap_row("C", harness_hit=0, fts=1, vec=1),
        _gap_row("D", harness_hit=0, fts=0, vec=0),          # no leg, clean prose
        _gap_row("E", harness_hit=0, fts=0, vec=0,
                 query='JSON debris {"line": "x"} copied from a tool call'),
        _gap_row("F", harness_hit=0, fts=None, vec=None),    # leg columns absent
        _gap_row("G", harness_hit=1, fts=1, vec=1),          # agreement, not a gap
        _gap_row("H", harness_hit=0, fts=1, vec=1, bank_hit=0),  # bank missed too
    ]
    assert evaluate.gap_columns(rows) == {
        "A": "fusion", "B": "fusion", "C": "fusion", "D": "embedding",
        "E": "unrecoverable", "F": "unknown", "G": "none", "H": "none"}


def test_gap_columns_shared_scope_rows_are_fusion_drop():
    # C10 oracle (refreshed-pair ids C019/C065/C081): each shared-scope row had
    # the anchor in the FTS window (rank 1) with vector 0/0/1 and was traced to
    # a Take(8) drop — they must classify as fusion, never "embedding gap".
    rows = [
        _gap_row("C019", harness_hit=0, fts=1, vec=0, targetScope="shared"),
        _gap_row("C065", harness_hit=0, fts=1, vec=0, targetScope="shared"),
        _gap_row("C081", harness_hit=0, fts=1, vec=1, targetScope="shared"),
    ]
    labels = evaluate.gap_columns(rows)
    assert labels == {"C019": "fusion", "C065": "fusion", "C081": "fusion"}


def test_gap_taxonomy_conservation_and_unknown_cap():
    # Conservation: the five cells partition the PAIRED rows (none = the c-cell
    # complement); c_cell is the four deficit labels; unknown share is capped.
    rows = [
        _gap_row("A", harness_hit=0, fts=1, vec=0),                 # fusion
        _gap_row("B", harness_hit=0, fts=0, vec=0),                 # embedding
        _gap_row("C", harness_hit=1, fts=1, vec=1),                 # none
        _gap_row("D", harness_hit=0, fts=None, vec=None),           # unknown
        _gap_row("E", harness_hit=0, fts=0, vec=1, bank_hit=0),     # bank miss -> none
        {"id": "F", "query": "q", "harness": {"hit": 0, "error": "boom"},
         "airaccoon": {"hit": 1}},                                  # unpaired, excluded
    ]
    tax = evaluate.gap_taxonomy(rows)
    assert tax["n_paired"] == 5
    assert tax["c_cell"] == 3
    assert tax["cells"] == {"none": 2, "fusion": 1, "embedding": 1,
                             "unrecoverable": 0, "unknown": 1}
    assert sum(tax["cells"].values()) == tax["n_paired"]
    assert tax["unknownShare"] == pytest.approx(1 / 5)


def test_unknown_share_over_cap_fails_the_eval_gate():
    # Bar: unknown share <= 5%, else the classifier (or the data) failed, not
    # the retrieval. Pure function reports the share; the gate fails loud.
    rows = [_gap_row("A", harness_hit=0, fts=None, vec=None)]
    out = {"rows": rows, "summary": {"n_paired": 1, "gaps": evaluate.aggregate_gaps(rows)}}
    failures = evaluate.eval_gate_failures(out)
    assert any("unknown" in f.lower() for f in failures)
    rows_ok = [_gap_row("A", harness_hit=1, fts=1, vec=1)]
    out_ok = {"rows": rows_ok,
              "summary": {"n_paired": 1, "gaps": evaluate.aggregate_gaps(rows_ok)}}
    assert evaluate.eval_gate_failures(out_ok) == []


def test_leg_diagnostics_use_candidate_window_not_top8(monkeypatch, tmp_path):
    # AC2 honesty precondition: build_harness_fn computes fts_hit/vector_hit
    # over the FULL leg lists (candidate window 100), never a top-8 slice — an
    # anchor at window index 49 must count as a window hit. If this ever
    # regresses, the taxonomy would label fusion rows as embedding-misses.
    from llamaindex_harness import ingest as ingest_mod  # noqa: PLC0415
    from llamaindex_harness import retrieve as retrieve_mod  # noqa: PLC0415

    class _FakeHandle:
        def close(self):
            pass

    class _FakeModel:
        def get_query_embedding(self, text):
            return [0.0]

    class _FakeRetriever:
        def __init__(self, handle, query_embed, project_id, scope, default_limit):
            pass

        def fts_leg(self, query, limit):
            # anchor at 1-based rank 50: beyond ANY top-8 shape
            rows = [(f"other{i}", 1.0) for i in range(49)]
            return rows + [("anchor", 1.0)], object()

        def vector_leg(self, query, limit):
            return [("other", 1.0)]

        def retrieve(self, query, limit=None):
            return []

    monkeypatch.setattr(retrieve_mod, "FusionRetriever", _FakeRetriever)
    monkeypatch.setattr(ingest_mod, "open_store", lambda path: _FakeHandle())
    monkeypatch.setattr(ingest_mod, "create_embedding_model",
                        lambda offline=False: _FakeModel())
    fn = evaluate.build_harness_fn(tmp_path)
    try:
        out = fn({"query": "q", "expectedHash": "anchor",
                  "targetProjectId": "p", "targetScope": "project"})
    finally:
        fn.close()
    assert out["fts_hit"] == 1
    assert out["vector_hit"] == 0
