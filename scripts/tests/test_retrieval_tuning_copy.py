"""Tests for scripts/retrieval_tuning/make_memory_copy.py (WP2, plan §5.1/§8/§12 G1).

Self-contained: the module under test is loaded by path, no harness package imports.
The module is import-safe (no side effects at import time).
"""

import hashlib
import importlib.util
import json
import random
import sqlite3
import subprocess
import sys
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


def test_strict_read_only_live_source(tmp_path, monkeypatch):
    """The live source is only ever connected through the mode=ro URI."""
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    _make_fixture_live(live)

    seen = []
    real_connect = sqlite3.connect

    def spy(*args, **kwargs):
        seen.append((args, kwargs))
        return real_connect(*args, **kwargs)

    monkeypatch.setattr(mc.sqlite3, "connect", spy)
    report = mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=random.Random(5))
    assert report["ok"] is True

    live_uri = f"file:{live.resolve()}?mode=ro"
    source_opens = [
        (args, kwargs) for args, kwargs in seen if args and str(live.resolve()) in str(args[0])
    ]
    assert source_opens, "run_copy_and_verify never opened the live source"
    for args, kwargs in source_opens:
        assert str(args[0]) == live_uri
        assert kwargs.get("uri") is True


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


# ---------------------------------------------------------------- WAL checkpoint + pin sidecar

def test_target_has_no_pending_wal_frames(tmp_path):
    """The target is checkpointed out of WAL: no -wal, no pending frames, complete main file."""
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    writer = _make_fixture_live(live, n_rows=5)
    writer.execute("PRAGMA journal_mode=WAL")
    writer.execute(
        "INSERT INTO entries (id, hash, path, value, scope, project_id, embed_state) "
        "VALUES (6, 'hash6', '/repo/docs/adr/0006.md', 'value-6', 'project', 'ai-raccoon', 'embedded')"
    )
    writer.commit()
    live_wal = Path(str(live) + "-wal")
    assert live_wal.exists() and live_wal.stat().st_size > 0  # frames pending on the source
    try:
        report = mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=random.Random(7))
    finally:
        writer.close()

    assert report["ok"] is True
    assert report["journal_mode"] == "delete"
    assert report["wal_frames"] == 0
    assert not Path(str(target) + "-wal").exists()
    # the target is DELETE-mode, so a mode=ro read serves the complete main file
    # and leaves no -wal/-shm behind (a WAL-flagged target would create them)
    ro = sqlite3.connect(f"file:{target.resolve()}?mode=ro", uri=True)
    try:
        assert ro.execute("SELECT count(*) FROM entries").fetchone()[0] == 6
    finally:
        ro.close()
    assert not Path(str(target) + "-wal").exists()
    assert not Path(str(target) + "-shm").exists()


def test_sidecar_records_sha_counts_settings(tmp_path):
    """The pin sidecar carries the file sha, copy counts, user_version and non-secret settings.

    The source is WAL with a pending frame, so the checkpoint really rewrites the
    target's bytes: the pin must describe the post-checkpoint file.
    """
    live = tmp_path / "live.db"
    target = tmp_path / "out" / "memory-copy.db"
    writer = _make_fixture_live(live, n_rows=6, embedded=4)
    writer.execute("PRAGMA journal_mode=WAL")
    writer.execute("PRAGMA user_version=17")
    writer.execute("INSERT INTO settings VALUES ('embedding.device', 'coreml')")
    writer.execute("INSERT INTO settings VALUES ('embedding.chunkBudget', '1022')")
    # one credential-shaped key per declared marker, plus a snake_case spelling
    secret_settings = {
        "embedding.apiKey": "sk-secret-apikey-must-not-leak",
        "embedding.api_key": "sk-secret-snake-apikey-must-not-leak",
        "embedding.apikey": "sk-secret-lower-apikey-must-not-leak",
        "embedding.secret": "sk-secret-secret-must-not-leak",
        "embedding.token": "sk-secret-token-must-not-leak",
        "embedding.password": "sk-secret-password-must-not-leak",
        "embedding.passwd": "sk-secret-passwd-must-not-leak",
        "embedding.credential": "sk-secret-credential-must-not-leak",
    }
    for key, value in secret_settings.items():
        writer.execute("INSERT INTO settings VALUES (?, ?)", (key, value))
    writer.commit()
    live_wal = Path(str(live) + "-wal")
    assert live_wal.exists() and live_wal.stat().st_size > 0  # pending frame on the WAL source
    try:
        report = mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=random.Random(11))
    finally:
        writer.close()
    assert report["ok"] is True

    pin_path = Path(str(target) + ".pin.json")
    assert pin_path.exists()
    assert report["pin_path"] == str(pin_path)
    pin = json.loads(pin_path.read_text(encoding="utf-8"))
    # sha of the final (post-checkpoint, post-rename) file; a pre-checkpoint hash differs for a WAL source
    assert pin["sha256"] == hashlib.sha256(target.read_bytes()).hexdigest()
    assert pin["bytes"] == target.stat().st_size
    assert pin["entries"] == 6
    assert pin["embedded"] == 4
    assert pin["userVersion"] == 17
    assert pin["path"] == str(target.resolve())
    assert pin["settings"]["embedding.device"] == "coreml"
    assert pin["settings"]["embedding.chunkBudget"] == "1022"
    assert pin["settings"]["retrieval.structureAlpha"] == "0.5"
    # the pin is taken after the checkpoint, never of a WAL-backed file
    assert pin["journalMode"] == "delete"
    assert pin["walFrames"] == 0
    assert pin["verified"] is True
    assert pin["sourcePath"] == str(live.resolve())
    assert pin["vecEntries"] is None
    # a persisted secret must never be copied into the sidecar, under any marker
    # spelling or key name: assert the values at file level so a re-keyed leak fails too
    sidecar_text = pin_path.read_text(encoding="utf-8")
    for key, value in secret_settings.items():
        assert key not in pin["settings"], f"credential key leaked into sidecar: {key}"
        assert value not in sidecar_text, f"credential value leaked into sidecar: {key}"


def test_sha256_file_digests_every_chunk(tmp_path):
    """A file spanning several chunks is hashed in full (the live bank is ~848 MB).

    chunk_size is injected so the loop must iterate more than once; the digest
    oracle is a single-shot hashlib.sha256 over the same bytes.
    """
    payload = bytes(range(256)) * 5  # 1280 bytes, ~183 chunks at chunk_size=7
    blob = tmp_path / "blob.bin"
    blob.write_bytes(payload)
    assert mc.sha256_file(str(blob), chunk_size=7) == hashlib.sha256(payload).hexdigest()
    # a first-chunk-only read is a different, shorter digest
    assert mc.sha256_file(str(blob), chunk_size=7) != hashlib.sha256(payload[:7]).hexdigest()


def _crashed_wal_db(path: Path) -> None:
    """Leave a WAL with pending frames behind, as an unclean writer exit would."""
    code = (
        "import sqlite3, os\n"
        f"conn = sqlite3.connect({str(path)!r})\n"
        "conn.execute('PRAGMA journal_mode=WAL')\n"
        "conn.execute('CREATE TABLE entries (id INTEGER PRIMARY KEY, value TEXT)')\n"
        "conn.execute('PRAGMA wal_autocheckpoint=0')\n"
        "conn.execute(\"INSERT INTO entries VALUES (1, 'x')\")\n"
        "conn.execute(\"INSERT INTO entries VALUES (2, 'y')\")\n"
        "conn.commit()\n"
        "os._exit(0)\n"
    )
    subprocess.run([sys.executable, "-c", code], check=True)


def test_checkpoint_target_reports_and_truncates_pending_wal_frames(tmp_path):
    """A WAL with pending frames is checkpointed into the main file and reported.

    An unclean writer exit leaves the -wal behind; checkpoint_target must count
    the pending frames, remove the WAL, and make the main file whole.
    """
    db = tmp_path / "crashed.db"
    _crashed_wal_db(db)
    wal = Path(str(db) + "-wal")
    assert wal.exists() and wal.stat().st_size > 0

    result = mc.checkpoint_target(str(db))

    assert result["journal_mode"] == "delete"
    assert result["wal_frames"] > 0
    assert not wal.exists()
    assert not Path(str(db) + "-shm").exists()
    ro = sqlite3.connect(f"file:{db.resolve()}?mode=ro", uri=True)
    try:
        assert ro.execute("SELECT count(*) FROM entries").fetchone()[0] == 2
    finally:
        ro.close()


def test_checkpoint_target_issues_truncating_checkpoint(tmp_path, monkeypatch):
    """The sanctioned wal_checkpoint(TRUNCATE) runs before the mode switch.

    journal_mode=DELETE also checkpoints, so dropping the TRUNCATE call alone is
    artifact-equivalent (measured: identical main-file sha); this wiring pin keeps
    the explicit sanctioned checkpoint in the sequence.
    """
    db = tmp_path / "plain.db"
    conn = sqlite3.connect(db)
    conn.execute("CREATE TABLE t (x)")
    conn.commit()
    statements = []

    class _RecordingConnection(sqlite3.Connection):
        def execute(self, sql, *args, **kwargs):
            statements.append(sql)
            return super().execute(sql, *args, **kwargs)

    real_connect = sqlite3.connect

    def spy(*args, **kwargs):
        kwargs["factory"] = _RecordingConnection
        return real_connect(*args, **kwargs)

    monkeypatch.setattr(mc.sqlite3, "connect", spy)
    result = mc.checkpoint_target(str(db))
    conn.close()

    assert result["journal_mode"] == "delete"
    truncate = [i for i, sql in enumerate(statements) if "wal_checkpoint(TRUNCATE)" in sql]
    mode_switch = [i for i, sql in enumerate(statements) if "journal_mode=DELETE" in sql]
    assert truncate, f"no TRUNCATE checkpoint issued: {statements}"
    assert mode_switch and truncate[0] < mode_switch[0], f"TRUNCATE must precede the mode switch: {statements}"
