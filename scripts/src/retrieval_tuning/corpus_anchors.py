"""Anchor-resolution primitives shared by the two retrieval corpus generators.

Both generators (build_project_corpus: exact-hash + verbatim-marker anchors;
build_eval_corpus: expectedSource/expectedHash anchors) read a sanctioned bank
copy read-only and resolve every anchor against it. These are the primitives
they share — one library, two contracts (P3 AC2/AC3). Per-generator candidate
enumeration, allocation and query rendering stay in their own modules.
"""

from __future__ import annotations

import hashlib
import sqlite3
from pathlib import Path

ANCHOR_COLUMNS = "hash, source_file, section, project_id, scope, value, chunk_index"


def open_copy(path) -> sqlite3.Connection:
    """Open a bank copy read-only (URI mode=ro + query_only), rows as sqlite3.Row."""
    conn = sqlite3.connect(f"file:{Path(path).resolve()}?mode=ro", uri=True)
    conn.execute("PRAGMA query_only=ON")
    conn.row_factory = sqlite3.Row
    return conn


def sha256_file(path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def hash_row_count(conn: sqlite3.Connection, hash_value: str) -> int:
    return int(conn.execute(
        "SELECT count(*) FROM entries WHERE hash=?", (hash_value,)).fetchone()[0])


def assert_hash_unique(conn: sqlite3.Connection, hash_value: str,
                       label: str | None = None) -> None:
    """An anchor hash must resolve to exactly one copy row (loud otherwise)."""
    n = hash_row_count(conn, hash_value)
    if n != 1:
        prefix = f"{label}: " if label else ""
        raise RuntimeError(
            f"{prefix}hash {hash_value[:16]}... is not unique in the copy ({n} rows)")


def marker_matches(conn: sqlite3.Connection, marker: str, *, project_id: str | None = None,
                   scope: str | None = None, chunk_index: int | None = None,
                   literal: bool = True,
                   columns: str = "hash") -> list[sqlite3.Row]:
    """Rows whose value carries `marker`, optionally narrowed to one bucket.

    literal=True matches a verbatim substring (`instr`), the project
    generator's uniqueness semantics; literal=False uses LIKE, the eval
    generator's marker contract. `columns` defaults to the count-caller's
    minimal projection; the eval resolver asks for the full anchor columns
    (`corpus_anchors.ANCHOR_COLUMNS`).
    """
    if literal:
        where, args = "instr(value, ?) > 0", [marker]
    else:
        where, args = "value LIKE ?", [f"%{marker}%"]
    if project_id:
        where += " AND project_id=?"
        args.append(project_id)
    if scope:
        where += " AND scope=?"
        args.append(scope)
    if chunk_index is not None:
        where += " AND chunk_index=?"
        args.append(chunk_index)
    return conn.execute(
        f"SELECT {columns} FROM entries WHERE {where}", args).fetchall()
