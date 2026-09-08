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
    assert out["summary"]["contingency"] == {"a": 0, "b": 2, "c": 0, "d": 0}
