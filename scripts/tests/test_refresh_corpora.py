"""P3 AC3 gates: refresh script exit-code contract, determinism, real registry.

Fixture banks only for the contract (no heavy generators); one pinned-copy
gate (skipped when the copy is absent) proves the real registry reproduces
`project-corpus-100.json` byte-for-byte and reports the eval-set artifact's
provenance gap instead of hiding it.
"""

import json
import os
import re
import sqlite3
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from retrieval_tuning import refresh_corpora  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
WRAPPER = REPO / "scripts" / "refresh-retrieval-corpora.py"
COMMITTED_CORPORA = REPO / "scripts" / "retrieval_tuning" / "corpora"
PINNED_COPY = Path(os.environ.get("AI_RACCOON_EVAL_COPY", "/tmp/p1-live-copy.db"))


def _serialize(payload) -> str:
    return json.dumps(payload, indent=2, sort_keys=True) + "\n"


def _fixture_copy(tmp_path, rows=None):
    """A bank copy with the columns both anchor styles touch."""
    db = tmp_path / "copy.db"
    conn = sqlite3.connect(db)
    conn.execute(
        "CREATE TABLE entries (id INTEGER PRIMARY KEY, hash TEXT, value TEXT, "
        "scope TEXT, project_id TEXT, source_file TEXT, section TEXT, "
        "heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER)")
    rows = rows if rows is not None else [
        ("abc123def456", "ai-raccoon observation alpha", "project", "ai-raccoon",
         "docs/adr/0042-widget.md", "Decision", "Overview > Decision", 0, 1),
    ]
    conn.executemany(
        "INSERT INTO entries (hash, value, scope, project_id, source_file, section,"
        " heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?)", rows)
    conn.commit()
    conn.close()
    return db


def _writer(payload):
    def generate(copy_path, out_path):
        out_path.write_text(_serialize(payload))
        return payload
    return generate


def _spec(payload, *, name="corpus.json", style="hash-only", expected_count=None):
    return refresh_corpora.CorpusSpec(name=name, generate=_writer(payload),
                                      anchor_style=style,
                                      expected_count=expected_count)


def _query(**overrides):
    base = {"id": "C001", "query": "what about alpha?",
            "expectedHash": "abc123def456", "contentMarker": None,
            "targetProjectId": "ai-raccoon", "targetScope": "project"}
    base.update(overrides)
    return base


def _refresh_fixture(tmp_path, committed_payload, specs, *, rows=None, copy=None):
    copy = copy or _fixture_copy(tmp_path, rows)
    committed = tmp_path / "committed"
    out = tmp_path / "out"
    committed.mkdir()
    for spec in specs:
        (committed / spec.name).write_text(_serialize(
            committed_payload.get(spec.name, committed_payload["_default"])))
    report = refresh_corpora.refresh(copy, committed, out, specs=specs)
    return report, out


class TestExitCodeContract:
    def test_clean_zero_and_report_written(self, tmp_path):
        payload = {"queries": [_query()]}
        report, out = _refresh_fixture(tmp_path, {"_default": payload}, [_spec(payload)])
        assert report["exitCode"] == 0
        assert report["status"] == "clean"
        record = report["corpora"][0]
        assert record["status"] == "clean"
        assert record["matchesCommitted"] is True
        assert record["queryCount"] == 1
        assert (out / "refresh-report.json").exists()

    def test_anchor_drift_one(self, tmp_path):
        payload = {"queries": [_query(expectedHash="deadbeef0000")]}
        report, _ = _refresh_fixture(tmp_path, {"_default": payload}, [_spec(payload)])
        assert report["exitCode"] == 1
        assert report["corpora"][0]["status"] == "anchor-drift"
        assert any("expectedHash" in p for p in report["corpora"][0]["problems"])

    def test_content_marker_drift_one(self, tmp_path):
        payload = {"queries": [_query(expectedHash=None, contentMarker="no-such-span")]}
        report, _ = _refresh_fixture(tmp_path, {"_default": payload}, [_spec(payload)])
        assert report["exitCode"] == 1
        assert any("contentMarker" in p for p in report["corpora"][0]["problems"])

    def test_generator_refusal_is_anchor_drift(self, tmp_path):
        def refuse(copy_path, out_path):
            raise RuntimeError("anchor hash x is not unique in the copy (2 rows)")

        spec = refresh_corpora.CorpusSpec(name="corpus.json", generate=refuse,
                                          anchor_style="hash-only")
        copy = _fixture_copy(tmp_path)
        committed = tmp_path / "committed"
        out = tmp_path / "out"
        committed.mkdir()
        (committed / "corpus.json").write_text(_serialize({"queries": [_query()]}))
        report = refresh_corpora.refresh(copy, committed, out, specs=[spec])
        assert report["exitCode"] == 1
        assert "refused" in report["corpora"][0]["problems"][0]

    def test_snapshot_mismatch_two_and_generator_not_run(self, tmp_path):
        called = {"n": 0}

        def generate(copy_path, out_path):
            called["n"] += 1
            out_path.write_text("{}")
            return {"queries": []}

        committed_payload = {"header": {"snapshotSha256": "0" * 64},
                             "queries": [_query()]}
        spec = refresh_corpora.CorpusSpec(name="corpus.json", generate=generate,
                                          anchor_style="hash-only")
        copy = _fixture_copy(tmp_path)
        committed = tmp_path / "committed"
        out = tmp_path / "out"
        committed.mkdir()
        (committed / "corpus.json").write_text(_serialize(committed_payload))
        report = refresh_corpora.refresh(copy, committed, out, specs=[spec])
        assert report["exitCode"] == 2
        assert report["corpora"][0]["status"] == "snapshot-mismatch"
        assert called["n"] == 0
        assert not (out / "corpus.json").exists()

    def test_stale_committed_bytes_are_smoke_regression_three(self, tmp_path):
        committed_payload = {"queries": [_query(query="old text")]}
        generated_payload = {"queries": [_query(query="new text")]}
        report, out = _refresh_fixture(
            tmp_path, {"_default": committed_payload}, [_spec(generated_payload)])
        assert report["exitCode"] == 3
        record = report["corpora"][0]
        assert record["status"] == "smoke-regression"
        assert record["matchesCommitted"] is False
        assert any("differ from the committed" in p for p in record["problems"])
        assert json.loads((out / "corpus.json").read_text()) == generated_payload

    def test_shape_smoke_failure_is_three(self, tmp_path):
        payload = {"queries": [_query()]}
        spec = _spec(payload, expected_count=100)
        report, _ = _refresh_fixture(tmp_path, {"_default": payload}, [spec])
        assert report["exitCode"] == 3
        assert any("expected 100" in p for p in report["corpora"][0]["problems"])

    def test_overall_status_takes_the_least_healthy_record(self, tmp_path):
        clean_payload = {"queries": [_query()]}
        drift_payload = {"queries": [_query(expectedHash="deadbeef0000")]}
        specs = [_spec(clean_payload, name="a.json"),
                 _spec(drift_payload, name="b.json")]
        report, _ = _refresh_fixture(tmp_path, {"_default": clean_payload}, specs)
        assert report["exitCode"] == 1  # anchor drift outranks the clean sibling


class TestAnchorStyles:
    def test_eval_style_verifies_expected_source(self, tmp_path):
        payload = {"queries": [{
            "id": "E001", "query": "what does adr 42 decide?",
            "expectedSource": "docs:adr:0042-*.md#decision",
            "expectedHash": "abc123def456",
            "targetProjectId": "ai-raccoon", "targetScope": "project",
        }]}
        report, _ = _refresh_fixture(tmp_path, {"_default": payload},
                                     [_spec(payload, style="eval")])
        assert report["exitCode"] == 0

    def test_eval_style_reports_an_unresolved_source(self, tmp_path):
        payload = {"queries": [{
            "id": "E001", "query": "what does adr 99 decide?",
            "expectedSource": "docs:adr:9999-*.md#decision",
            "expectedHash": "abc123def456",
            "targetProjectId": "ai-raccoon", "targetScope": "project",
        }]}
        report, _ = _refresh_fixture(tmp_path, {"_default": payload},
                                     [_spec(payload, style="eval")])
        assert report["exitCode"] == 1
        assert report["corpora"][0]["status"] == "anchor-drift"


class TestDeterminism:
    def test_report_is_byte_stable_for_the_same_inputs(self, tmp_path):
        payload = {"queries": [_query()]}
        copy = _fixture_copy(tmp_path)
        committed = tmp_path / "committed"
        committed.mkdir()
        (committed / "corpus.json").write_text(_serialize(payload))
        spec = _spec(payload)
        out = tmp_path / "out"
        first = refresh_corpora.refresh(copy, committed, out, specs=[spec])
        first_text = (out / "refresh-report.json").read_text()
        second = refresh_corpora.refresh(copy, committed, out, specs=[spec])
        second_text = (out / "refresh-report.json").read_text()
        assert first_text == second_text
        assert "time" not in json.loads(first_text) and "date" not in json.loads(first_text)
        assert first["corpora"][0]["regeneratedSha256"] == \
            second["corpora"][0]["regeneratedSha256"]


class TestWrapper:
    def test_help_exits_zero(self):
        proc = subprocess.run([sys.executable, str(WRAPPER), "--help"],
                              capture_output=True, text=True)
        assert proc.returncode == 0
        assert "snapshot mismatch" in proc.stdout

    def test_usage_error_without_copy(self):
        env = {k: v for k, v in os.environ.items() if k != "AI_RACCOON_EVAL_COPY"}
        proc = subprocess.run([sys.executable, str(WRAPPER)],
                              capture_output=True, text=True, env=env)
        assert proc.returncode == 2
        assert "--copy" in proc.stderr

    def test_wrapper_runs_the_fixture_contract(self, tmp_path):
        # Registry-named specs against a bank the generators refuse: the wrapper
        # must surface the contract (anchor drift) and still write its report —
        # proving the thin import path and error handling, not just --help.
        copy = tmp_path / "copy.db"
        conn = sqlite3.connect(copy)
        conn.executescript(
            "CREATE TABLE entries (id INTEGER PRIMARY KEY, hash TEXT, value TEXT,"
            " scope TEXT, project_id TEXT, source_file TEXT, section TEXT,"
            " heading_path TEXT, chunk_index INTEGER, total_chunks INTEGER);"
            "CREATE TABLE project_id_aliases (alias TEXT, winner TEXT, kind TEXT);"
            "CREATE TABLE repair_requests (kind TEXT, map_json TEXT);")
        conn.commit()
        conn.close()
        committed = tmp_path / "committed"
        out = tmp_path / "out"
        committed.mkdir()
        env = {k: v for k, v in os.environ.items() if k != "AI_RACCOON_EVAL_COPY"}
        proc = subprocess.run(
            [sys.executable, str(WRAPPER), "--copy", str(copy),
             "--corpora-dir", str(committed), "--out-dir", str(out)],
            capture_output=True, text=True, env=env)
        assert proc.returncode == 1, proc.stderr
        assert (out / "refresh-report.json").exists()
        report = json.loads((out / "refresh-report.json").read_text())
        assert {r["name"] for r in report["corpora"]} == {
            "project-corpus-100.json", "eval-set-100.json"}
        assert all(r["status"] == "anchor-drift" for r in report["corpora"])


class TestRegistry:
    def test_default_specs_are_the_two_committed_corpora(self):
        specs = refresh_corpora.default_specs()
        assert [s.name for s in specs] == ["project-corpus-100.json", "eval-set-100.json"]
        assert [s.anchor_style for s in specs] == ["hash-only", "eval"]
        assert all(s.expected_count == 100 for s in specs)

    def test_committed_corpora_carry_snapshot_pins(self):
        """Every committed corpus pins its source copy (C1/C3) — the refresh
        contract's snapshot check is dead for any corpus without a header."""
        for name in ("project-corpus-100.json", "eval-set-100.json"):
            payload = json.loads((COMMITTED_CORPORA / name).read_text())
            assert isinstance(payload, dict), f"{name}: root must be {{header, queries}}"
            header = payload.get("header")
            assert isinstance(header, dict), f"{name}: header missing"
            assert header.get("generator"), f"{name}: header.generator missing"
            assert header.get("seed") is not None, f"{name}: header.seed missing"
            assert header.get("queryCount") == 100, f"{name}: header.queryCount"
            assert len(payload.get("queries", [])) == 100, f"{name}: queries length"
            sha = header.get("snapshotSha256")
            assert isinstance(sha, str) and re.fullmatch(r"[0-9a-f]{64}", sha), (
                f"{name}: snapshotSha256 must be 64 lowercase hex, got {sha!r}"
            )

    def test_committed_pins_refuse_a_different_copy(self, tmp_path):
        """The pins are active, not decorative (C3): a copy whose sha differs
        from the committed header is refused with exit 2 before any generator
        runs. The real committed corpora are the input; the copy file itself is
        a placeholder because the snapshot check short-circuits generation."""
        dummy = tmp_path / "not-the-pinned-copy.db"
        dummy.write_bytes(b"not a bank copy")
        report = refresh_corpora.refresh(dummy, COMMITTED_CORPORA, tmp_path / "out",
                                         copy_sha="0" * 64)
        assert report["exitCode"] == 2
        assert {r["name"] for r in report["corpora"]} == {
            "project-corpus-100.json", "eval-set-100.json"}
        for record in report["corpora"]:
            assert record["status"] == "snapshot-mismatch", record
            assert record["problems"], record
        assert not (tmp_path / "out" / "project-corpus-100.json").exists()
        assert not (tmp_path / "out" / "eval-set-100.json").exists()

    @pytest.mark.skipif(not PINNED_COPY.exists(),
                        reason=f"pinned copy absent: {PINNED_COPY}")
    def test_pinned_copy_reproduces_every_committed_corpus(self, tmp_path):
        report = refresh_corpora.refresh(PINNED_COPY, COMMITTED_CORPORA,
                                         tmp_path / "regenerated")
        by_name = {r["name"]: r for r in report["corpora"]}
        assert set(by_name) == {"project-corpus-100.json", "eval-set-100.json"}
        for name, record in by_name.items():
            assert record["matchesCommitted"] is True, (name, record["problems"])
            assert record["status"] == "clean", (name, record["status"])
            assert record["queryCount"] == 100, name
        assert report["status"] == "clean"
        assert report["exitCode"] == 0
