"""P3 eval gates: singleton-F1 math, agreement-MCC incl. null-with-reason,
transport-failure pairwise exclusion, corpus-contract fail-loud, N x 2 shape."""

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
