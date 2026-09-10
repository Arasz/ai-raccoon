"""P3 AC4: old-vs-new CLI parity harness for the touched CLIs.

Opt-in (heavy: each side imports chromadb/torch) and pinned to the task's base
commit. Extract the base tree, run the SAME arg matrix on legacy and new, and
diff exit code / stdout / stderr / output artifacts. Expected values come from
executing the legacy code on the fixtures — never from reading it.

Run:
    P3_CLI_PARITY=1 python3 -m pytest scripts/tests/test_p3_cli_parity.py -q

CLIs covered: ingest, evaluate (llamaindex_harness), report
(llamaindex_harness), slice_copy, plus the refresh pair (legacy generators vs
the new generator outputs — the refresh wrapper has no legacy CLI, so its
parity is the byte identity of what it regenerates).
"""

import hashlib
import json
import os
import sqlite3
import subprocess
import sys
import tarfile
import io
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
P3_BASE = "12a72dfb"
PINNED_COPY = Path(os.environ.get("AI_RACCOON_EVAL_COPY", "/tmp/p1-live-copy.db"))
PARITY = os.environ.get("P3_CLI_PARITY") == "1"

pytestmark = [
    pytest.mark.skipif(not PARITY, reason="set P3_CLI_PARITY=1 to run the heavy parity harness"),
    pytest.mark.skipif(subprocess.run(["git", "-C", str(REPO), "cat-file", "-e",
                                       f"{P3_BASE}^{{commit}}"],
                                      capture_output=True).returncode != 0,
                       reason=f"base commit {P3_BASE} not available"),
]


def _extract_tree(tmp_path: Path) -> Path:
    """The base commit's scripts trees, unpacked under tmp_path/legacy."""
    legacy = tmp_path / "legacy"
    legacy.mkdir()
    archive = subprocess.run(
        ["git", "-C", str(REPO), "archive", P3_BASE, "scripts/retrieval_tuning",
         "scripts/src/retrieval_tuning"], capture_output=True, check=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(legacy)  # noqa: S202 — trusted local git archive
    return legacy


def _run(tree: Path, module: str, argv: list[str], *, cwd_rel="scripts/retrieval_tuning",
         extra_env=None):
    env = dict(os.environ)
    env["PYTHONPATH"] = str(tree / "scripts" / "src")
    env["ANONYMIZED_TELEMETRY"] = "False"
    if extra_env:
        env.update(extra_env)
    return subprocess.run(
        [sys.executable, "-m", module, *argv],
        cwd=tree / cwd_rel, env=env, capture_output=True, text=True, timeout=600)


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _bank_copy(path: Path, rows=None, *, fts=False) -> None:
    """A minimal bank copy; fts=True adds the live external-content FTS shape."""
    rows = rows if rows is not None else [
        ("h1", "ai-raccoon alpha observation", "p/a.md", "project", "ai-raccoon",
         "docs/a.md", "Decision", "A > Decision", 0, 1),
        ("h2", "ai-raccoon beta observation", "p/b.md", "project", "ai-raccoon",
         "docs/b.md", "Context", "B > Context", 0, 1),
        ("h3", "shared gamma observation", "shared/g.md", "shared", "ai-raccoon",
         "docs/g.md", "Notes", "G > Notes", 0, 1),
    ]
    conn = sqlite3.connect(path)
    conn.execute(
        "CREATE TABLE entries (id INTEGER PRIMARY KEY, hash TEXT, value TEXT, path TEXT,"
        " scope TEXT, project_id TEXT, source_file TEXT, section TEXT, heading_path TEXT,"
        " chunk_index INTEGER, total_chunks INTEGER)")
    if fts:
        conn.executescript(
            "CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section,"
            " content='entries', content_rowid='id');"
            "CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN"
            " INSERT INTO entries_fts(rowid, value, source_file, section)"
            " VALUES (new.id, new.value, new.source_file, new.section); END;"
            "CREATE TRIGGER entries_fts_ad AFTER DELETE ON entries BEGIN"
            " INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)"
            " VALUES('delete', old.id, old.value, old.source_file, old.section); END;"
            "CREATE TRIGGER entries_fts_au AFTER UPDATE ON entries BEGIN"
            " INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)"
            " VALUES('delete', old.id, old.value, old.source_file, old.section);"
            " INSERT INTO entries_fts(rowid, value, source_file, section)"
            " VALUES (new.id, new.value, new.source_file, new.section); END;")
    conn.executemany(
        "INSERT INTO entries (hash, value, path, scope, project_id, source_file, section,"
        " heading_path, chunk_index, total_chunks) VALUES (?,?,?,?,?,?,?,?,?,?)", rows)
    conn.commit()
    conn.close()


def _build_store_with_legacy(legacy: Path, copy: Path, store: Path) -> None:
    """Ingest the fixture with the LEGACY code (embed seam) — the shared input."""
    argv = ["--copy", str(copy), "--store-dir", str(store),
            "--buckets", "ai-raccoon"]
    code = (
        "import sys;"
        f"sys.path.insert(0, {str(legacy / 'scripts' / 'retrieval_tuning')!r});"
        "from llamaindex_harness import ingest;"
        f"sys.exit(ingest.main({argv!r}, embed=lambda texts: [[0.1] * 8 for _ in texts]))")
    proc = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True,
                          timeout=600, env={**os.environ, "ANONYMIZED_TELEMETRY": "False"})
    assert proc.returncode == 0, proc.stderr[-2000:]


def _compare(name: str, legacy, new) -> None:
    """Diff the observable contract; pytest prints the first differing stream."""
    assert legacy.returncode == new.returncode, (
        f"{name}: exit {legacy.returncode} != {new.returncode}\n"
        f"legacy stdout={legacy.stdout[-1000:]!r}\nlegacy stderr={legacy.stderr[-1000:]!r}\n"
        f"new stdout={new.stdout[-1000:]!r}\nnew stderr={new.stderr[-1000:]!r}")
    assert legacy.stdout == new.stdout, f"{name}: stdout differs"
    assert legacy.stderr == new.stderr, f"{name}: stderr differs"


class TestIngestParity:
    def test_verify_only_success_and_bad_bucket_and_usage(self, tmp_path):
        legacy = _extract_tree(tmp_path)
        copy, store = tmp_path / "copy.db", tmp_path / "store"
        _bank_copy(copy, fts=True)
        _build_store_with_legacy(legacy, copy, store)
        matrix = {
            "verify_only": ["--verify-only", "--copy", str(copy),
                            "--store-dir", str(store), "--buckets", "ai-raccoon"],
            "bad_bucket": ["--verify-only", "--copy", str(copy),
                           "--store-dir", str(store), "--buckets", "no-such"],
            "no_args": [],
        }
        for name, argv in matrix.items():
            old = _run(legacy, "llamaindex_harness.ingest", argv)
            new = _run(REPO, "llamaindex_harness.ingest", argv)
            _compare(f"ingest {name}", old, new)


class TestEvaluateParity:
    def test_provenance_failure_and_mode_usage(self, tmp_path):
        legacy = _extract_tree(tmp_path)
        store = tmp_path / "store"
        store.mkdir()
        (store / "params.json").write_text(json.dumps({
            "model": "Salesforce/SFR-Embedding-Code-400M_R",
            "modelRevision": "not-the-pinned-revision"}))
        corpus = tmp_path / "corpus.json"
        corpus.write_text("[]")
        scratch = tmp_path / "scratch"
        scratch.mkdir()
        matrix = {
            "provenance": ["--corpus", str(corpus), "--store-dir", str(store),
                           "--scratch-data-root", str(scratch),
                           "--out", str(tmp_path / "out.json")],
            "repeats_mode": ["--corpus", str(corpus), "--store-dir", str(store),
                             "--repeats", "2", "--out", str(tmp_path / "out2.json")],
        }
        for name, argv in matrix.items():
            old = _run(legacy, "llamaindex_harness.evaluate", argv)
            new = _run(REPO, "llamaindex_harness.evaluate", argv)
            _compare(f"evaluate {name}", old, new)


class TestReportParity:
    def test_report_artifact_bytes(self, tmp_path):
        legacy = _extract_tree(tmp_path)
        rows = [{
            "id": "C001", "query": "what about alpha?", "targetProjectId": "ai-raccoon",
            "targetScope": "project", "expectedHash": "h1",
            "harness": {"hashes": ["h1"], "hit": 1, "precision": 1.0, "recall": 1.0,
                        "f1": 1.0, "error": None, "fts_hit": 1, "vector_hit": 1},
            "airaccoon": {"hashes": ["h1"], "hit": 1, "precision": 1.0, "recall": 1.0,
                          "f1": 1.0, "error": None},
        }]
        results = {
            "rows": rows,
            "summary": {"n": 1, "n_paired": 1,
                        "harness": {"n": 1, "hit_rate": 1.0, "mean_f1": 1.0},
                        "airaccoon": {"n": 1, "hit_rate": 1.0, "mean_f1": 1.0},
                        "contingency": {"a": 1, "b": 0, "c": 0, "d": 0},
                        "mcc": None, "mcc_reason": "zero denominator"},
            "staleAnchors": [],
            "modelRevision": "test-revision", "modelBytes": 1,
            "copyPath": "fixture", "copySnapshotSha256": "ab" * 32,
            "resolvedBuckets": ["ai-raccoon"], "excludedProjects": [],
        }
        context = {"date": "fixture-date", "corpus": "fixture-corpus", "corpus_size": 1}
        results_path = tmp_path / "results.json"
        results_path.write_text(json.dumps(results))
        # Same --out path for both sides so the CLI's own stdout (it echoes the
        # path) is comparable; artifact bytes are captured per side.
        out = tmp_path / "report.md"
        legacy_proc = _run(legacy, "llamaindex_harness.report",
                           ["--results", str(results_path), "--out", str(out),
                            "--context", json.dumps(context)])
        legacy_bytes = out.read_bytes()
        new_proc = _run(REPO, "llamaindex_harness.report",
                        ["--results", str(results_path), "--out", str(out),
                         "--context", json.dumps(context)])
        _compare("report", legacy_proc, new_proc)
        assert legacy_bytes == out.read_bytes(), "report artifact bytes differ"


class TestSliceParity:
    def test_slice_artifact_bytes(self, tmp_path):
        legacy = _extract_tree(tmp_path)
        source = tmp_path / "bank.db"
        _bank_copy(source, fts=True)
        corpus = tmp_path / "subset.json"
        corpus.write_text(json.dumps([{"id": "E1", "query": "alpha", "expectedHash": "h1"}]))
        artifacts = {}
        for label, tree in (("legacy", legacy), ("new", REPO)):
            target = tmp_path / f"{label}-slice.db"
            proc = _run(tree, "llamaindex_harness.slice_copy",
                        ["--source", str(source), "--target", str(target),
                         "--corpus", str(corpus), "--cap-per-bucket", "1",
                         "--buckets", "ai-raccoon"])
            artifacts[label] = (proc, target)
        _compare("slice_copy", artifacts["legacy"][0], artifacts["new"][0])
        assert _sha256(artifacts["legacy"][1]) == _sha256(artifacts["new"][1]), \
            "slice artifact bytes differ"


class TestRefreshParity:
    @pytest.mark.skipif(not PINNED_COPY.exists(), reason=f"pinned copy absent: {PINNED_COPY}")
    def test_generators_reproduce_the_legacy_bytes(self, tmp_path):
        legacy = _extract_tree(tmp_path)
        legacy_scripts = legacy / "scripts" / "retrieval_tuning"
        for generator in ("build_project_corpus", "build_eval_corpus"):
            legacy_out = tmp_path / f"legacy-{generator}.json"
            new_out = tmp_path / f"new-{generator}.json"
            extra = ["--docs-dir", str(REPO / "docs" / "adr")] \
                if generator == "build_eval_corpus" else []
            old = subprocess.run(
                [sys.executable, str(legacy_scripts / f"{generator}.py"),
                 "--copy", str(PINNED_COPY), "--output", str(legacy_out), *extra],
                capture_output=True, text=True, timeout=900)
            assert old.returncode == 0, old.stderr[-2000:]
            new = subprocess.run(
                [sys.executable, str(REPO / "scripts" / "retrieval_tuning" / f"{generator}.py"),
                 "--copy", str(PINNED_COPY), "--output", str(new_out), *extra],
                capture_output=True, text=True, timeout=900)
            assert new.returncode == 0, new.stderr[-2000:]
            if generator == "build_eval_corpus":
                # C (air-eval-corpus-provenance-clean-ci): the eval corpus is now
                # header-shaped. The legacy contract is the QUERY PAYLOAD, so compare
                # that (bytes can no longer be identical by construction) and pin the
                # new shape: header present with a 64-hex snapshot hash.
                legacy_payload = json.loads(legacy_out.read_text())
                new_payload = json.loads(new_out.read_text())
                assert isinstance(new_payload, dict) and "queries" in new_payload, \
                    "eval corpus must be header-shaped after C"
                header = new_payload.get("header") or {}
                pin = header.get("snapshotSha256")
                assert isinstance(pin, str) and len(pin) == 64, \
                    "eval corpus header must carry a 64-hex snapshotSha256 pin"
                assert new_payload["queries"] == legacy_payload, \
                    "eval corpus query payload differs from the legacy output"
            else:
                assert _sha256(legacy_out) == _sha256(new_out), \
                    f"{generator}: legacy and new bytes differ"
