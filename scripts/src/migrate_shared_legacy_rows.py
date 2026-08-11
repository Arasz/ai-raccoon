"""Migrate legacy path-addressed shared rows (shared//<abs-path>) to the value format.

Pre-1.6.3 shared rows were addressed as ``"shared/" + source.Path`` where source paths
were absolute, producing double-slash paths (``shared//Users/...``). The 1.6.3+ contract
addresses shared rows by value: ``shared/<sha256(value)>.md`` with
``hash = sha256(path + value)``. This one-off migrates the 59 legacy rows on the live bank
(2026-08-11, task mem-cleanup).

Safety: dry-run by default; pre-verifies the hash formula on every existing value-addressed
row; aborts (never deletes) when it finds value twins or unexpected shared-path shapes.
Only the path/hash columns are written — no entries trigger fires on those columns.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import sqlite3
import sys
from typing import Dict, List, Optional, TypedDict

# Legacy rows carry the pre-1.6.3 "shared/" + absolute source path shape.
LEGACY_PATH_PREFIX = "shared//"
# Value-addressed rows: shared/<64 lowercase hex>.md
VALUE_PATH_GLOB = "shared/[0-9a-f]*.md"


def content_hash(path: str, content: str) -> str:
    """ContentHash.Of — SHA-256 of UTF8(path) + UTF8(content), hex."""
    return hashlib.sha256((path + content).encode("utf-8")).hexdigest()


def value_path(value: str) -> str:
    """WritePathFor in the shared tier: shared/<sha256(value)>.md."""
    return "shared/" + hashlib.sha256(value.encode("utf-8")).hexdigest() + ".md"


class MigrationReport(TypedDict):
    value_rows: int
    legacy_rows: int
    formula_mismatches: List[str]
    legacy_twins: List[str]
    other_shapes: List[str]
    migrated: int


def _report_row(conn: sqlite3.Connection) -> MigrationReport:
    """Count/scan the shared tier; never writes."""
    value_rows = conn.execute(
        "SELECT path, hash, value FROM entries WHERE scope='shared' AND path GLOB ?",
        (VALUE_PATH_GLOB,),
    ).fetchall()
    legacy_rows = conn.execute(
        "SELECT id, path, hash, value FROM entries WHERE scope='shared' AND path LIKE ?",
        (LEGACY_PATH_PREFIX + "%",),
    ).fetchall()
    other = conn.execute(
        "SELECT path FROM entries WHERE scope='shared' "
        "AND path NOT GLOB ? AND path NOT LIKE ?",
        (VALUE_PATH_GLOB, LEGACY_PATH_PREFIX + "%"),
    ).fetchall()
    mismatches = [p for p, h, v in value_rows if h != content_hash(p, v)]
    by_value: Dict[str, List[int]] = {}
    for row_id, _path, _hash, value in legacy_rows:
        by_value.setdefault(value, []).append(row_id)
    twins = [value for value, ids in by_value.items() if len(ids) > 1]
    return MigrationReport(
        value_rows=len(value_rows),
        legacy_rows=len(legacy_rows),
        formula_mismatches=mismatches,
        legacy_twins=twins,
        other_shapes=[p for (p,) in other],
        migrated=0,
    )


def migrate(bank_path: str, apply: bool = False) -> MigrationReport:
    """Migrate legacy shared rows to the value format.

    Returns a report dict. With ``apply=False`` nothing is written. Raises ValueError
    (before any write) when the pre-conditions do not hold.
    """
    conn = sqlite3.connect(bank_path)
    try:
        report = _report_row(conn)
        if report["formula_mismatches"] or report["legacy_twins"] or report["other_shapes"]:
            raise ValueError(
                "shared tier not in the expected state; refusing to migrate: "
                f"formula_mismatches={len(report['formula_mismatches'])} "
                f"twins={report['legacy_twins']} other_shapes={report['other_shapes']}"
            )
        if not apply or report["legacy_rows"] == 0:
            return report
        legacy_rows = conn.execute(
            "SELECT id, value FROM entries WHERE scope='shared' AND path LIKE ?",
            (LEGACY_PATH_PREFIX + "%",),
        ).fetchall()
        updates = []
        for row_id, value in legacy_rows:
            new_path = value_path(value)
            updates.append((new_path, content_hash(new_path, value), row_id))
        with conn:
            conn.executemany(
                "UPDATE entries SET path = ?, hash = ? WHERE id = ?", updates
            )
        report["migrated"] = len(updates)
        return report
    finally:
        conn.close()


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Migrate legacy shared//<path> rows to shared/<sha256(value)>.md"
    )
    parser.add_argument(
        "--bank", default="~/.ai-raccoon/memory.db", help="SQLite bank path"
    )
    parser.add_argument(
        "--apply", action="store_true", help="write the migration (default: dry-run report)"
    )
    args = parser.parse_args(argv)
    try:
        report = migrate(os.path.expanduser(args.bank), apply=args.apply)
    except ValueError as exc:
        print(f"ABORT: {exc}", file=sys.stderr)
        return 2
    print(
        "value_rows={value_rows} legacy_rows={legacy_rows} migrated={migrated} "
        "formula_mismatches={mismatches} twins={twins} other_shapes={other}".format(
            value_rows=report["value_rows"],
            legacy_rows=report["legacy_rows"],
            migrated=report["migrated"],
            mismatches=len(report["formula_mismatches"]),
            twins=report["legacy_twins"],
            other=report["other_shapes"],
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
