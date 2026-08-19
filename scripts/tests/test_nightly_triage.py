"""Tests for nightly-triage.py's pure classification logic.

The script's dotnet invocations are not exercised here — the acceptance for the
verdict paths is witnessed with synthetic trx files, one per path:
flake (failed full, passed isolated), regression (failed both), known (ledgered),
and the parse/ledger/filter helpers every path shares.
"""

import importlib.util
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parent.parent / "nightly-triage.py"


@pytest.fixture(scope="module")
def triage():
    spec = importlib.util.spec_from_file_location("nightly_triage", SCRIPT)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def trx(failed: list[str], passed: int = 10, skipped: int = 2,
        start: str = "2026-08-19T02:00:00.0000000+00:00",
        finish: str = "2026-08-19T02:16:49.0000000+00:00") -> str:
    results = "".join(
        f'<UnitTestResult testName="{name}" outcome="Failed" />' for name in failed)
    return f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="{start}" finish="{finish}" />
  <ResultSummary><Counters total="{passed + len(failed) + skipped}"
    executed="{passed + len(failed)}" passed="{passed}" failed="{len(failed)}"
    skipped="{skipped}" /></ResultSummary>
  <Results>{results}</Results>
</TestRun>"""


def test_parse_trx_counts_and_failures(triage, tmp_path):
    path = tmp_path / "nightly.trx"
    path.write_text(trx(["A.Tests.T1", "A.Tests.T2"], passed=10, skipped=2))
    parsed = triage.parse_trx(path)
    assert parsed["total"] == 14
    assert parsed["passed"] == 10
    assert parsed["failed"] == 2
    assert parsed["skipped"] == 2
    assert parsed["failed_tests"] == ["A.Tests.T1", "A.Tests.T2"]
    assert parsed["started_at"] == "2026-08-19T02:00:00.0000000+00:00"


def test_trx_duration_s(triage, tmp_path):
    path = tmp_path / "nightly.trx"
    path.write_text(trx([]))
    parsed = triage.parse_trx(path)
    assert triage.trx_duration_s(parsed) == 60 * 16 + 49


def test_load_ledger_missing_and_malformed(triage, tmp_path, monkeypatch):
    monkeypatch.setattr(triage, "LEDGER_PATH", tmp_path / "absent.json")
    assert triage.load_ledger() == {}
    path = tmp_path / "broken.json"
    path.write_text("not json")
    monkeypatch.setattr(triage, "LEDGER_PATH", path)
    assert triage.load_ledger() == {}


def test_load_ledger_keyed_by_exact_fqn(triage, tmp_path, monkeypatch):
    path = tmp_path / "known-flakes.json"
    path.write_text('[{"test": "A.Tests.T1", "issue": 1, "reason": "Class C", "since": "2026-08-19"}]')
    monkeypatch.setattr(triage, "LEDGER_PATH", path)
    ledger = triage.load_ledger()
    assert ledger == {"A.Tests.T1": {"test": "A.Tests.T1", "issue": 1,
                                     "reason": "Class C", "since": "2026-08-19"}}


def test_classify_flake_regression_known(triage):
    ledger = {"A.Tests.Known": {"test": "A.Tests.Known"}}
    classes = triage.classify(
        ["A.Tests.Flake", "A.Tests.Regression", "A.Tests.Known"],
        ledger, rerun_failed={"A.Tests.Regression"})
    assert classes == {
        "A.Tests.Flake": "flake",
        "A.Tests.Regression": "regression",
        "A.Tests.Known": "known",
    }


def test_build_filter_joins_fqns(triage):
    assert triage.build_filter(["A.Tests.T1", "A.Tests.T2"]) == (
        "FullyQualifiedName~A.Tests.T1|FullyQualifiedName~A.Tests.T2")


def test_summarize_green_and_red(triage, tmp_path):
    path = tmp_path / "nightly.trx"
    path.write_text(trx([]))
    green = triage.summarize("2026-08-19", "green", triage.parse_trx(path), {})
    assert "3620" not in green  # counts come from the trx, not hardcoded
    assert "10 passed / 0 failed / 2 skipped" in green
    assert "16m49s" in green
    path = tmp_path / "rerun.trx"
    path.write_text(trx(["A.Tests.Flake"], start="2026-08-19T02:20:00.0000000+00:00",
                        finish="2026-08-19T02:20:30.0000000+00:00"))
    parsed = triage.parse_trx(path)
    classes = triage.classify(parsed["failed_tests"], {}, set())
    red = triage.summarize("2026-08-19", "red", parsed, classes)
    assert "flake candidates: A.Tests.Flake" in red


def test_write_classification_verdict(triage, tmp_path, monkeypatch):
    monkeypatch.setattr(triage, "TEST_RESULTS", tmp_path)
    path = tmp_path / "nightly.trx"
    path.write_text(trx([]))
    parsed = triage.parse_trx(path)
    parsed["failed"] = 1  # simulate the known-flake case: the run WAS red, the ledger owns it
    parsed["failed_tests"] = ["A.Tests.Known"]
    triage.write_classification("green(known flakes only)",
                                {"A.Tests.Known": "known"}, parsed)
    payload = __import__("json").loads((tmp_path / "classification.json").read_text())
    assert payload["verdict"] == "green(known flakes only)"
    assert payload["all_ledgered"] is True
