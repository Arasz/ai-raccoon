"""Behavior tests for the legacy shared-row migration (migrate_shared_legacy_rows).

Contract: legacy rows (shared//<abs-path>) are re-addressed to
shared/<sha256(value)>.md with hash = sha256(path + value); value-addressed rows are
untouched; any unexpected state (hash-formula mismatch, value twins, other path shapes)
aborts before writing anything.
"""

from __future__ import annotations

import sqlite3

import pytest

from migrate_shared_legacy_rows import content_hash, migrate, value_path


def _make_bank(path: str) -> sqlite3.Connection:
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY,
            scope TEXT NOT NULL,
            path TEXT NOT NULL,
            hash TEXT NOT NULL,
            value TEXT NOT NULL
        );
        CREATE UNIQUE INDEX uq_entries_shared_bucket
            ON entries(path, hash) WHERE scope = 'shared';
        """
    )
    return conn


def _seed(conn: sqlite3.Connection, rows) -> None:
    conn.executemany(
        "INSERT INTO entries (scope, path, hash, value) VALUES (?, ?, ?, ?)", rows
    )
    conn.commit()


def _value_row(value: str):
    path = value_path(value)
    return ("shared", path, content_hash(path, value), value)


def _legacy_row(value: str, path: str = "shared//Users/x/docs/adr/0001-foo.md"):
    return ("shared", path, "old-hash", value)


def _shared_rows(conn: sqlite3.Connection):
    return conn.execute(
        "SELECT path, hash FROM entries WHERE scope='shared' ORDER BY id"
    ).fetchall()


class TestDryRun:
    def test_reports_legacy_rows_and_writes_nothing(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [
            _value_row("value row"),
            _legacy_row("legacy one"),
            _legacy_row("legacy two", path="shared//Users/x/docs/adr/0002-bar.md"),
        ])
        conn.close()

        report = migrate(str(bank), apply=False)

        assert report["legacy_rows"] == 2
        assert report["value_rows"] == 1
        assert report["migrated"] == 0
        conn = sqlite3.connect(str(bank))
        try:
            rows = _shared_rows(conn)
            assert ("shared//Users/x/docs/adr/0001-foo.md", "old-hash") in rows
            assert ("shared//Users/x/docs/adr/0002-bar.md", "old-hash") in rows
            assert (value_path("value row"), content_hash(value_path("value row"), "value row")) in rows
        finally:
            conn.close()

    def test_empty_bank_reports_zero(self, tmp_path):
        bank = tmp_path / "bank.db"
        _make_bank(str(bank)).close()
        report = migrate(str(bank))
        assert report["legacy_rows"] == 0
        assert report["value_rows"] == 0


class TestApply:
    def test_readdresses_legacy_rows_to_value_format(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [_value_row("value row"), _legacy_row("legacy one")])
        conn.close()

        report = migrate(str(bank), apply=True)

        assert report["migrated"] == 1
        conn = sqlite3.connect(str(bank))
        try:
            rows = conn.execute(
                "SELECT path, hash FROM entries WHERE scope='shared' ORDER BY id"
            ).fetchall()
            # value row untouched
            assert rows[0] == (value_path("value row"), content_hash(value_path("value row"), "value row"))
            # legacy row re-addressed
            new_path = value_path("legacy one")
            assert rows[1] == (new_path, content_hash(new_path, "legacy one"))
        finally:
            conn.close()

    def test_repeat_apply_is_idempotent(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [_legacy_row("legacy one")])
        conn.close()
        migrate(str(bank), apply=True)
        second = migrate(str(bank), apply=True)
        assert second["legacy_rows"] == 0
        assert second["migrated"] == 0


class TestAborts:
    def test_formula_mismatch_in_value_rows_aborts_before_writing(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [("shared", value_path("bad"), "not-the-right-hash", "bad"), _legacy_row("legacy one")])
        conn.close()

        with pytest.raises(ValueError, match="formula_mismatches"):
            migrate(str(bank), apply=True)

        conn = sqlite3.connect(str(bank))
        try:
            assert ("shared//Users/x/docs/adr/0001-foo.md", "old-hash") in _shared_rows(conn)
        finally:
            conn.close()

    def test_legacy_twins_abort_before_writing(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [
            _legacy_row("same value"),
            _legacy_row("same value", path="shared//Users/x/docs/adr/0002-bar.md"),
        ])
        conn.close()

        with pytest.raises(ValueError, match="twins"):
            migrate(str(bank), apply=True)

        conn = sqlite3.connect(str(bank))
        try:
            assert len(_shared_rows(conn)) == 2
        finally:
            conn.close()

    def test_unexpected_shared_shape_aborts(self, tmp_path):
        bank = tmp_path / "bank.db"
        conn = _make_bank(str(bank))
        _seed(conn, [("shared", "shared/odd.md", "h", "odd shape")])
        conn.close()

        with pytest.raises(ValueError, match="other_shapes"):
            migrate(str(bank), apply=True)
