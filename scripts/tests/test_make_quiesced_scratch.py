"""C11 gates: the quiesced-scratch builder disables the kill switches, refuses
row-count drift, and the run-recipe row-stability snapshot can go red."""

import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))
from make_quiesced_scratch import (  # noqa: E402
    ScratchRefusedError, entries_count, make_quiesced_scratch, file_sha256,
    quiesce_settings, row_stability,
)


def _bank(path: Path, settings: dict, rows: int) -> None:
    conn = sqlite3.connect(path)
    conn.executescript(
        "CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT);"
        "CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT,"
        " created_at INTEGER, updated_at INTEGER);")
    for key, value in settings.items():
        conn.execute("INSERT INTO settings(key, value) VALUES(?, ?)", (key, value))
    for i in range(rows):
        conn.execute("INSERT INTO entries(created_at, updated_at) VALUES(?, ?)",
                     (1000 + i, 2000 + i))
    conn.commit()
    conn.close()


def _settings(path: Path) -> dict:
    conn = sqlite3.connect(path)
    try:
        return dict(conn.execute("SELECT key, value FROM settings"))
    finally:
        conn.close()


def test_make_quiesced_scratch_disables_watch_sweep_extract(tmp_path):
    source = tmp_path / "copy.db"
    out = tmp_path / "base.db"
    _bank(source, {
        "watch.enabled.global": "true",
        "watch.enabled.ai-raccoon": "true",
        "watch.enabled.jsaa": "true",
        "sweep.enabled.global": "true",
        "extract.enabled.global": "true",
        "retrieval.rrfK": "60",
    }, rows=3)

    report = make_quiesced_scratch(source, out)

    settings = _settings(out)
    assert settings["watch.enabled.global"] == "false"
    assert settings["watch.enabled.ai-raccoon"] == "false"
    assert settings["watch.enabled.jsaa"] == "false"
    assert settings["sweep.enabled.global"] == "false"
    assert settings["extract.enabled.global"] == "false"
    assert settings["retrieval.rrfK"] == "60"  # frozen knobs are untouched
    assert report["rows"] == 3
    assert report["settingsDisabled"] == 5
    assert report["sha256"] == file_sha256(out)


def test_make_quiesced_scratch_refuses_row_count_drift(tmp_path):
    source = tmp_path / "copy.db"
    out = tmp_path / "base.db"
    _bank(source, {"watch.enabled.global": "true"}, rows=3)

    def drifting_copy(_src, dst):
        _bank(dst, {"watch.enabled.global": "true"}, rows=2)

    with pytest.raises(ScratchRefusedError, match="rows"):
        make_quiesced_scratch(source, out, copy_fn=drifting_copy)
    assert not out.exists()  # a refused base never poisons the run recipe


def test_make_quiesced_scratch_refuses_missing_settings_table(tmp_path):
    source = tmp_path / "copy.db"
    out = tmp_path / "base.db"
    conn = sqlite3.connect(source)
    conn.execute("CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT)")
    conn.commit()
    conn.close()

    with pytest.raises(ScratchRefusedError, match="settings"):
        make_quiesced_scratch(source, out)
    assert not out.exists()


def test_row_stability_snapshot_detects_a_write(tmp_path):
    """The before/after run-recipe assertion must be able to go red."""
    db = tmp_path / "memory.db"
    _bank(db, {}, rows=2)
    conn = sqlite3.connect(db)
    before = row_stability(conn)
    assert before == {"entries": 2, "max_created_at": 1001, "max_updated_at": 2001}

    conn.execute("INSERT INTO entries(created_at, updated_at) VALUES(3000, 4000)")
    conn.commit()
    assert row_stability(conn) != before  # a new row moves entries + maxes

    conn.execute("UPDATE entries SET updated_at = 9000 WHERE id = 1")
    conn.commit()
    assert row_stability(conn)["max_updated_at"] == 9000
    conn.close()


def test_entries_count_and_quiesce_are_readback():
    # quiesce_settings returns the rows it changed; entries_count is the raw count.
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        db = Path(d) / "x.db"
        _bank(db, {"watch.enabled.global": "true", "sweep.enabled.global": "true"}, rows=4)
        conn = sqlite3.connect(db)
        try:
            assert entries_count(conn) == 4
            assert quiesce_settings(conn) == 2
            conn.commit()
            assert quiesce_settings(conn) == 2  # idempotent: sets false again
        finally:
            conn.close()
