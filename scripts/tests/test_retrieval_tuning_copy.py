"""Tests for scripts/retrieval_tuning/make_memory_copy.py (WP2, plan §5.1/§8/§12 G1).

Self-contained: the module under test is loaded by path, no harness package imports.
The module is import-safe (no side effects at import time).
"""

import hashlib
import importlib.util
import sqlite3
from pathlib import Path

import pytest

_SCRIPT = Path(__file__).resolve().parents[1] / "retrieval_tuning" / "make_memory_copy.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("make_memory_copy", _SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


mc = _load_module()


# ---------------------------------------------------------------- fixtures

def _make_fixture_live(path: Path, n_rows: int = 5, embedded: int | None = None) -> sqlite3.Connection:
    """Build a minimal live-bank-shaped fixture: entries + settings tables."""
    if embedded is None:
        embedded = n_rows
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY,
            hash TEXT,
            path TEXT,
            value TEXT,
            scope TEXT,
            project_id TEXT,
            embed_state TEXT NOT NULL DEFAULT 'pending'
        );
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
        """
    )
    for i in range(n_rows):
        conn.execute(
            "INSERT INTO entries (id, hash, path, value, scope, project_id, embed_state) "
            "VALUES (?, ?, ?, ?, 'project', 'ai-raccoon', ?)",
            (i + 1, f"hash{i:064x}", f"/repo/docs/adr/{i:04d}.md", f"value-{i}", "embedded" if i < embedded else "pending"),
        )
    conn.execute("INSERT INTO settings VALUES ('retrieval.structureAlpha', '0.5')")
    conn.execute("INSERT INTO settings VALUES ('fusion.noRegression.enabled.global', 'true')")
    conn.execute("INSERT INTO settings VALUES ('retrieval.ftsWeight', '1')")
    conn.execute("INSERT INTO settings VALUES ('unrelated.key', 'x')")
    conn.commit()
    return conn


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------- read-only discipline

def test_open_readonly_rejects_writes(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live)
    conn = mc.open_readonly(str(live))
    with pytest.raises(sqlite3.OperationalError):
        conn.execute("INSERT INTO entries (id, hash) VALUES (999, 'x')")
    with pytest.raises(sqlite3.OperationalError):
        conn.execute("DELETE FROM entries")
    conn.close()


def test_open_readonly_uri_uses_mode_ro(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live)
    conn = mc.open_readonly(str(live))
    # query_only=1 is belt-and-braces on top of the mode=ro URI
    assert conn.execute("PRAGMA query_only").fetchone()[0] == 1
    conn.close()


# ---------------------------------------------------------------- counts

def test_snapshot_counts(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live, n_rows=4, embedded=3)
    conn = sqlite3.connect(live)
    counts = mc.snapshot_counts(conn)
    conn.close()
    assert counts["entries"] == 4
    assert counts["embedded"] == 3
    # vec_entries is a vec0 virtual table absent from fixtures -> None, never an exception
    assert counts["vec_entries"] is None


def test_snapshot_counts_embedded_matches_embed_state(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live, n_rows=6, embedded=2)
    conn = sqlite3.connect(live)
    counts = mc.snapshot_counts(conn)
    conn.close()
    assert counts["entries"] == 6
    assert counts["embedded"] == 2


# ---------------------------------------------------------------- integrity

def test_integrity_ok_true_on_fixture(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    assert mc.integrity_ok(conn) is True
    conn.close()


def test_integrity_ok_false_on_corrupt_copy(tmp_path):
    live = tmp_path / "live.db"
    copy = tmp_path / "copy.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    dst = sqlite3.connect(copy)
    conn.backup(dst)
    dst.close()
    conn.close()
    # corrupt the copy: damage sqlite_master, integrity_check must stop returning 'ok'
    bad = sqlite3.connect(copy)
    bad.execute("PRAGMA writable_schema=ON")
    bad.execute("UPDATE sqlite_master SET sql='garbage' WHERE name='entries'")
    bad.commit()
    bad.close()
    assert mc.integrity_ok(sqlite3.connect(copy)) is False


# ---------------------------------------------------------------- spot check

def test_spot_check_matches_identical_fixtures(tmp_path):
    live = tmp_path / "live.db"
    copy = tmp_path / "copy.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    dst = sqlite3.connect(copy)
    conn.backup(dst)
    dst.close()
    hashes = [r[0] for r in conn.execute("SELECT hash FROM entries")]
    conn.close()
    results = mc.spot_check_hashes(sqlite3.connect(live), sqlite3.connect(copy), hashes)
    assert len(results) == len(hashes)
    assert all(r["sha256_match"] for r in results)


def test_spot_check_detects_value_change(tmp_path):
    live = tmp_path / "live.db"
    copy = tmp_path / "copy.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    dst = sqlite3.connect(copy)
    conn.backup(dst)
    dst.close()
    target_hash = conn.execute("SELECT hash FROM entries WHERE id=1").fetchone()[0]
    conn.close()
    c = sqlite3.connect(copy)
    c.execute("UPDATE entries SET value='tampered' WHERE id=1")
    c.commit()
    c.close()
    results = mc.spot_check_hashes(sqlite3.connect(live), sqlite3.connect(copy), [target_hash])
    assert results[0]["sha256_match"] is False


def test_spot_check_detects_deleted_row(tmp_path):
    live = tmp_path / "live.db"
    copy = tmp_path / "copy.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    dst = sqlite3.connect(copy)
    conn.backup(dst)
    dst.close()
    target_hash = conn.execute("SELECT hash FROM entries WHERE id=2").fetchone()[0]
    conn.close()
    c = sqlite3.connect(copy)
    c.execute("DELETE FROM entries WHERE id=2")
    c.commit()
    c.close()
    results = mc.spot_check_hashes(sqlite3.connect(live), sqlite3.connect(copy), [target_hash])
    assert results[0]["sha256_match"] is False


def test_spot_check_detects_hash_change(tmp_path):
    live = tmp_path / "live.db"
    copy = tmp_path / "copy.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    dst = sqlite3.connect(copy)
    conn.backup(dst)
    dst.close()
    target_hash = conn.execute("SELECT hash FROM entries WHERE id=3").fetchone()[0]
    conn.close()
    c = sqlite3.connect(copy)
    c.execute("UPDATE entries SET hash='deadbeef' WHERE id=3")
    c.commit()
    c.close()
    results = mc.spot_check_hashes(sqlite3.connect(live), sqlite3.connect(copy), [target_hash])
    assert results[0]["sha256_match"] is False


# ---------------------------------------------------------------- settings leak

def test_read_inherited_settings_returns_only_retrieval_and_fusion(tmp_path):
    live = tmp_path / "live.db"
    _make_fixture_live(live)
    conn = sqlite3.connect(live)
    rows = mc.read_inherited_settings(conn)
    conn.close()
    keys = {k for k, _ in rows}
    assert keys == {"retrieval.structureAlpha", "fusion.noRegression.enabled.global", "retrieval.ftsWeight"}
    assert ("fusion.noRegression.enabled.global", "true") in rows
    assert ("retrieval.structureAlpha", "0.5") in rows


# ---------------------------------------------------------------- end-to-end copy+verify

def test_run_copy_and_verify_roundtrip(tmp_path):
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live, n_rows=5)
    report = mc.run_copy_and_verify(str(live), str(target), sample_size=3, rng=__import__("random").Random(42))
    assert report["ok"] is True
    assert report["integrity"] == "ok"
    assert report["entries_live"] == report["entries_copy"] == 5
    assert report["embedded_live"] == report["embedded_copy"] == 5
    assert report["vec_entries_live"] is None and report["vec_entries_copy"] is None
    assert len(report["spot_check"]) == 3
    assert all(r["sha256_match"] for r in report["spot_check"])
    assert any(k == "fusion.noRegression.enabled.global" for k, _ in report["settings"])
    assert target.exists()


def test_run_copy_and_verify_detects_entry_count_drift(tmp_path):
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live, n_rows=5)
    assert mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=__import__("random").Random(1))["ok"] is True
    # the copy gains a row after the fact -> verify_copy (no regeneration) must fail the parity check
    c = sqlite3.connect(target)
    c.execute("INSERT INTO entries (id, hash, value, scope, embed_state) VALUES (99, 'x', 'y', 'project', 'embedded')")
    c.commit()
    c.close()
    report = mc.verify_copy(str(live), str(target), sample_size=2, rng=__import__("random").Random(1))
    assert report["ok"] is False
    assert report["entries_live"] != report["entries_copy"]


def test_run_copy_and_verify_detects_embedded_drift(tmp_path):
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live, n_rows=5)
    assert mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=__import__("random").Random(1))["ok"] is True
    c = sqlite3.connect(target)
    c.execute("UPDATE entries SET embed_state='pending' WHERE id=1")
    c.commit()
    c.close()
    report = mc.verify_copy(str(live), str(target), sample_size=2, rng=__import__("random").Random(1))
    assert report["ok"] is False
    assert report["embedded_live"] != report["embedded_copy"]


def test_main_returns_zero_on_success_and_prints_settings(tmp_path, capsys):
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live)
    rc = mc.main(["--live", str(live), "--target", str(target)])
    out = capsys.readouterr().out
    assert rc == 0
    assert "fusion.noRegression.enabled.global" in out
    assert "retrieval.structureAlpha" in out
    assert "integrity_check = ok" in out


def test_main_returns_nonzero_on_verification_failure(tmp_path, capsys):
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live)
    assert mc.main(["--live", str(live), "--target", str(target)]) == 0
    c = sqlite3.connect(target)
    c.execute("UPDATE entries SET value='tampered' WHERE id=1")
    c.commit()
    c.close()
    rc = mc.main(["--live", str(live), "--target", str(target), "--sample-size", "5", "--verify-only"])
    assert rc == 1
    out = capsys.readouterr().out
    assert "FAIL" in out
