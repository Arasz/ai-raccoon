#!/usr/bin/env python3
"""Build the quiesced eval scratch base from a pinned bank copy (C11).

Copies a bank copy read-only, flips every watch/sweep/extract kill switch to
false, and verifies the copy has the source's row count. A scratch copied
straight from the bank inherits 12 enabled watches + maintenance and re-ingests
live files mid-eval (measured: rows 56,457→53,637; bank hit-rate 0.798→0.869);
this base is the precondition under which the frozen golden was measured.

Refuses (ScratchRefusedError, exit 1) when the copy's entries count differs
from the source's, or the source has no settings table — a refused base is
removed so it can never poison a run.

The run recipe additionally snapshots row stability before/after each eval:

    python3 make_quiesced_scratch.py --stability <scratch>/memory.db

`row_stability` is entries count + max(created_at/updated_at); equality across
a run proves no row wrote through the eval (a small bookkeeping-only WAL from
access bumps is expected).

Usage:
    python3 make_quiesced_scratch.py --source <bank-copy.db> --out <base.db>
    python3 make_quiesced_scratch.py --stability <scratch>/memory.db

Import-safe: no side effects at import time.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

WATCH_PREFIX = "watch.enabled."
SWEEP_KEY = "sweep.enabled.global"
EXTRACT_KEY = "extract.enabled.global"


class ScratchRefusedError(RuntimeError):
    """The source copy cannot become a sound eval base."""


def entries_count(conn: sqlite3.Connection) -> int:
    return int(conn.execute("SELECT count(*) FROM entries").fetchone()[0])


def row_stability(conn: sqlite3.Connection) -> dict:
    """The run-recipe no-write assertion: entries + max created/updated timestamps."""
    count, max_created, max_updated = conn.execute(
        "SELECT count(*), max(created_at), max(updated_at) FROM entries").fetchone()
    return {"entries": int(count), "max_created_at": max_created,
            "max_updated_at": max_updated}


def quiesce_settings(conn: sqlite3.Connection) -> int:
    """Set every watch/sweep/extract kill switch false; returns rows changed."""
    changed = conn.execute(
        "UPDATE settings SET value = 'false' WHERE key LIKE ?",
        (WATCH_PREFIX + "%",)).rowcount
    changed += conn.execute(
        "UPDATE settings SET value = 'false' WHERE key IN (?, ?)",
        (SWEEP_KEY, EXTRACT_KEY)).rowcount
    return int(changed)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _copy_readonly(source: Path, out: Path) -> None:
    """SQLite .backup from a read-only source connection (never writes the source)."""
    src = sqlite3.connect(f"file:{source.resolve()}?mode=ro", uri=True)
    dst = sqlite3.connect(out)
    try:
        src.backup(dst)
    finally:
        dst.close()
        src.close()


def make_quiesced_scratch(source: Path, out: Path, copy_fn=None) -> dict:
    """Copy `source` to `out`, disable the kill switches, and verify row parity."""
    source, out = Path(source), Path(out)
    if not source.exists():
        raise ScratchRefusedError(f"source bank copy not found: {source}")
    out.parent.mkdir(parents=True, exist_ok=True)
    for stale in (out, Path(str(out) + "-wal"), Path(str(out) + "-shm")):
        stale.unlink(missing_ok=True)

    (copy_fn or _copy_readonly)(source, out)

    src = sqlite3.connect(f"file:{source.resolve()}?mode=ro", uri=True)
    try:
        source_rows = entries_count(src)
    finally:
        src.close()

    conn = sqlite3.connect(out)
    try:
        try:
            changed = quiesce_settings(conn)
        except sqlite3.OperationalError as exc:
            raise ScratchRefusedError(
                f"settings table missing or unreadable in {source}: {exc}") from exc
        copy_rows = entries_count(conn)
        if copy_rows != source_rows:
            raise ScratchRefusedError(
                f"copy rows {copy_rows} != source rows {source_rows} — refusing base")
        conn.commit()
        try:
            conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        except sqlite3.OperationalError:
            pass  # delete-mode DBs have nothing to checkpoint
    except BaseException:
        conn.close()
        for stale in (out, Path(str(out) + "-wal"), Path(str(out) + "-shm")):
            stale.unlink(missing_ok=True)
        raise
    conn.close()
    return {"source": str(source), "out": str(out), "rows": source_rows,
            "settingsDisabled": changed, "sha256": file_sha256(out)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, help="pinned bank copy to quiesce")
    parser.add_argument("--out", type=Path, help="quiesced base target")
    parser.add_argument("--stability", type=Path,
                        help="print the row-stability snapshot for a scratch memory.db")
    args = parser.parse_args(argv)

    if args.stability:
        conn = sqlite3.connect(f"file:{args.stability.resolve()}?mode=ro", uri=True)
        try:
            print(json.dumps(row_stability(conn)))
        finally:
            conn.close()
        return 0

    if not args.source or not args.out:
        parser.error("--source and --out are required (or use --stability)")
    try:
        report = make_quiesced_scratch(args.source, args.out)
    except ScratchRefusedError as exc:
        print(f"FAIL: {exc}")
        return 1
    print(f"base: {report['out']}")
    print(f"source: {report['source']}")
    print(f"rows: {report['rows']} (copy parity verified)")
    print(f"settings disabled: {report['settingsDisabled']} "
          f"(watch/sweep/extract kill switches)")
    print(f"base sha256: {report['sha256']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
