"""Deterministic subset slice of a bank copy (subset-first eval; import-safe, argparse + main(argv)).

Keep rule (documented in the subset report): ALL scope='shared' rows (the
global tier stays whole) + first --cap-per-bucket rows by id per project
bucket (scope IN ('project','custom')) + every --corpus expectedHash present
in the source (subset anchors must resolve). Everything else is DELETEd; the
live schema's entries_fts_ad trigger keeps the external-content FTS index
consistent, then VACUUM + integrity_check prove the slice is a healthy bank.

The slice feeds BOTH legs of a subset eval (harness ingest + scratch-server
data-root), so the paired comparison stays fair on a smaller universe.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sqlite3
import sys
import tempfile
from pathlib import Path

from . import scopes

DEFAULT_BUCKETS = ("ai-raccoon", "hermes-default")
DEFAULT_CAP = 1500


def _forced_hashes(corpus_path: str) -> list[str]:
    # Bare-list subset corpora and header-shaped corpora ({header, queries})
    # both force their anchors in; null anchors (content-targeted rows) carry
    # no hash and are skipped (the eval filters them pre-run_eval).
    _, entries = scopes.load_corpus(corpus_path)
    return [e["expectedHash"] for e in entries
            if isinstance(e.get("expectedHash"), str) and e["expectedHash"]]


def _ensure_vec0(conn: sqlite3.Connection) -> None:
    """Load the vec0 extension when the schema uses it (live-bank vec tables).

    Virtual-table modules resolve lazily: plain entries SELECTs work without
    vec0, but the vec_*_ad DELETE triggers fire it — without the module every
    sliced DELETE dies with 'no such module: vec0' and orphans would poison
    the ctx-partitioned KNN (MemorySql VectorSearchByFilter scans pre-JOIN)."""
    uses_vec = conn.execute(
        "SELECT count(*) FROM sqlite_master WHERE sql LIKE '%vec0%'").fetchone()[0]
    if not uses_vec:
        return
    try:
        import sqlite_vec  # noqa: PLC0415 — optional runtime dep (pyproject)
    except ImportError as exc:
        raise ValueError("slice source uses vec0 virtual tables but sqlite-vec is"
                         " not installed (python3 -m pip install sqlite-vec)") from exc
    conn.enable_load_extension(True)
    sqlite_vec.load(conn)


def slice_copy(source: str, target: str, forced: list[str],
               buckets: tuple[str, ...] = DEFAULT_BUCKETS,
               cap_per_bucket: int = DEFAULT_CAP) -> dict:
    """Backup source -> target, DELETE non-kept rows, VACUUM, verify. Returns counts."""
    src = sqlite3.connect(f"file:{Path(source).resolve()}?mode=ro", uri=True)
    if forced:
        present = {r[0] for r in src.execute("SELECT hash FROM entries")}
        missing = sorted(set(forced) - present)
    else:
        missing = []
    with tempfile.TemporaryDirectory() as tmp:
        # sqlite backup (the make_memory_copy.py mechanism): consistent even
        # with -wal sidecars, unlike a raw file copy.
        stage = str(Path(tmp) / "stage.db")
        dst = sqlite3.connect(stage)
        try:
            with dst:
                src.backup(dst)
        finally:
            dst.close()
        src.close()

        conn = sqlite3.connect(stage)
        _ensure_vec0(conn)
        try:
            # NULL-safe keep: NOT(keep) is NULL (not TRUE) for NULL-scope rows
            # and DELETE spares NULL — so NULL scopes die explicitly unless
            # force-listed. Mirrors ingest.load_rows (which can never see them).
            keep = ["scope = 'shared'"]
            params: list = []
            for bucket in buckets:
                keep.append("id IN (SELECT id FROM entries WHERE project_id = ?"
                            " AND scope IN ('project','custom') ORDER BY id LIMIT ?)")
                params.extend([bucket, cap_per_bucket])
            if forced:
                conn.execute(
                    "DELETE FROM entries WHERE"
                    " (scope IS NULL OR NOT (" + " OR ".join(keep) + "))"
                    f" AND hash NOT IN ({','.join('?' for _ in forced)})", params + forced)
            else:
                conn.execute(
                    "DELETE FROM entries WHERE"
                    " (scope IS NULL OR NOT (" + " OR ".join(keep) + "))", params)
            conn.commit()
            conn.execute("VACUUM")
            integrity = conn.execute("PRAGMA integrity_check").fetchone()[0]
            counts = {
                "entries": conn.execute("SELECT count(*) FROM entries").fetchone()[0],
                "fts": conn.execute("SELECT count(*) FROM entries_fts").fetchone()[0],
                "buckets": conn.execute(
                    "SELECT project_id || '/' || scope, count(*) FROM entries"
                    " GROUP BY 1 ORDER BY 2 DESC").fetchall(),
            }
        finally:
            conn.close()
        if integrity != "ok":
            raise ValueError(f"slice integrity_check = {integrity!r}")
        if counts["fts"] != counts["entries"]:
            raise ValueError(f"slice FTS skew: fts={counts['fts']} entries={counts['entries']}")
        Path(target).parent.mkdir(parents=True, exist_ok=True)
        shutil.move(stage, target)
    counts["missingForced"] = missing
    return counts


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="full bank copy (read-only)")
    parser.add_argument("--target", required=True, help="slice output path")
    parser.add_argument("--corpus", required=True, help="subset JSON (expectedHash force-in)")
    parser.add_argument("--cap-per-bucket", type=int, default=DEFAULT_CAP)
    parser.add_argument("--buckets", default=None,
                        help="explicit comma-separated buckets; wins over the corpus header")
    args = parser.parse_args(argv)
    forced = _forced_hashes(args.corpus)
    if args.buckets:
        buckets = tuple(b.strip() for b in args.buckets.split(",") if b.strip())
    else:
        header, _ = scopes.load_corpus(args.corpus)
        if header is not None and isinstance(header.get("projects"), dict):
            buckets = tuple(sorted(header["projects"]))
        else:
            # Legacy default: bare-list subset corpora carry no header, and the
            # slice gates pin the 2-bucket default there — header-shaped corpora
            # never reach this branch.
            buckets = DEFAULT_BUCKETS
    try:
        counts = slice_copy(args.source, args.target, forced,
                            buckets, args.cap_per_bucket)
    except Exception as exc:  # noqa: BLE001 — any slice failure is a failed gate
        print(f"FAIL: slice failed: {exc}")
        return 1
    print(f"sliced: entries={counts['entries']} fts={counts['fts']} forced={len(forced)}")
    for bucket, n in counts["buckets"]:
        print(f"  {bucket}: {n}")
    if counts["missingForced"]:
        print(f"WARNING: {len(counts['missingForced'])} forced anchors absent from source "
              f"(stale, unhittable): {counts['missingForced']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
