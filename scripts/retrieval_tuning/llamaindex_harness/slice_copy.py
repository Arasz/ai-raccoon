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

DEFAULT_BUCKETS = ("ai-raccoon", "hermes-default")
DEFAULT_CAP = 1500


def _forced_hashes(corpus_path: str) -> list[str]:
    entries = json.loads(Path(corpus_path).read_text())
    return [e["expectedHash"] for e in entries
            if isinstance(e.get("expectedHash"), str) and e["expectedHash"]]


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
        try:
            keep = ["scope = 'shared'"]
            params: list = []
            if forced:
                keep.append(f"hash IN ({','.join('?' for _ in forced)})")
                params.extend(forced)
            for bucket in buckets:
                keep.append("id IN (SELECT id FROM entries WHERE project_id = ?"
                            " AND scope IN ('project','custom') ORDER BY id LIMIT ?)")
                params.extend([bucket, cap_per_bucket])
            conn.execute(f"DELETE FROM entries WHERE NOT ({' OR '.join(keep)})", params)
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
    parser.add_argument("--buckets", default=",".join(DEFAULT_BUCKETS))
    args = parser.parse_args(argv)
    forced = _forced_hashes(args.corpus)
    try:
        counts = slice_copy(args.source, args.target, forced,
                            tuple(args.buckets.split(",")), args.cap_per_bucket)
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
