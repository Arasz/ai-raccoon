#!/usr/bin/env python3
"""Read-only live-bank copy + verification + pin (plan §5.1, §8; gate G1).

Copies the live memory bank to a scratch target via the sanctioned pattern — the
source is opened ONLY through a read-only URI connection (``file:...?mode=ro``),
and SQLite's ``.backup`` reads that connection while writing the target. The copy
is checkpointed out of WAL mode (no pending WAL frames; the main file alone is
the artifact) and immediately sha256-pinned in a sidecar JSON recording the sha,
row counts, ``user_version`` and the non-secret retrieval/fusion/embedding
settings snapshot. The copy is then verified: integrity check, entry/embedded
count parity against a fresh live snapshot, a SHA-256 spot check of sampled
(hash, value) rows, and a printout of the inherited ``retrieval.%`` /
``fusion.%`` settings (the settings-leak the tuning harness must write all 9
knobs explicitly to compensate for).

Usage:
    python make_memory_copy.py [--live ~/.ai-raccoon/memory.db]
                               [--target /tmp/continue-testing-algorithm/datasets/memory-copy.db]
                               [--pin /path/to/memory-copy.db.pin.json]
                               [--sample-size 3]

Exit code 0 = copy created and verified; 1 = any verification step failed.
Import-safe: no side effects at import time (all logic is in functions).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_LIVE = str(Path.home() / ".ai-raccoon" / "memory.db")
DEFAULT_TARGET = "/tmp/continue-testing-algorithm/datasets/memory-copy.db"

SETTINGS_QUERY = (
    "SELECT key, value FROM settings "
    "WHERE key LIKE 'retrieval.%' OR key LIKE 'fusion.%' ORDER BY key"
)

SETTINGS_SNAPSHOT_QUERY = (
    "SELECT key, value FROM settings "
    "WHERE key LIKE 'retrieval.%' OR key LIKE 'fusion.%' OR key LIKE 'embedding.%' "
    "ORDER BY key"
)

# A settings key naming a credential is never copied into the sidecar
# (embedding.apiKey is a persisted secret); the sidecar is a plain file.
_SECRET_KEY_MARKERS = ("apikey", "secret", "token", "password", "passwd", "credential")


def open_readonly(path: str, strict: bool = True) -> sqlite3.Connection:
    """Open a bank read-only.

    strict=True (the live bank): the ONLY permitted access mode is the read-only
    URI — never a writable handle. strict=False (the scratch copy): a WAL-mode
    database with no -shm file cannot open via ``mode=ro`` (SQLite must create
    the shm), so fall back to a plain connection with query_only=1, which is
    sufficient for verification of a file we just created ourselves.
    """
    uri = f"file:{Path(path).resolve()}?mode=ro"
    try:
        conn = sqlite3.connect(uri, uri=True)
    except sqlite3.OperationalError:
        if strict:
            raise
        conn = sqlite3.connect(path)
    conn.execute("PRAGMA query_only=ON")
    return conn


def snapshot_counts(conn: sqlite3.Connection) -> dict:
    """Entry counts from a bank connection.

    vec_entries is a vec0 virtual table; when the vec0 module is unavailable the
    count is reported as None (embedded-count parity is the fallback gate).
    """
    entries = conn.execute("SELECT count(*) FROM entries").fetchone()[0]
    embedded = conn.execute(
        "SELECT count(*) FROM entries WHERE embed_state='embedded'"
    ).fetchone()[0]
    try:
        vec_entries = conn.execute("SELECT count(*) FROM vec_entries").fetchone()[0]
    except sqlite3.OperationalError:
        vec_entries = None
    return {"entries": entries, "embedded": embedded, "vec_entries": vec_entries}


def integrity_ok(conn: sqlite3.Connection) -> bool:
    """PRAGMA integrity_check returns exactly ['ok'] on a healthy bank.

    A malformed schema makes the check itself raise (sqlite3.DatabaseError);
    that is a failed check, never a crash.
    """
    try:
        rows = [row[0] for row in conn.execute("PRAGMA integrity_check")]
    except sqlite3.DatabaseError:
        return False
    return rows == ["ok"]


def spot_check_hashes(
    live_conn: sqlite3.Connection, copy_conn: sqlite3.Connection, sample_hashes: list[str]
) -> list[dict]:
    """Compare SHA-256 of (hash, value) rows between live and copy for given hashes."""
    results = []
    for h in sample_hashes:
        live_row = live_conn.execute(
            "SELECT hash, value FROM entries WHERE hash=?", (h,)
        ).fetchone()
        copy_row = copy_conn.execute(
            "SELECT hash, value FROM entries WHERE hash=?", (h,)
        ).fetchone()
        if live_row is None or copy_row is None:
            results.append({"hash": h, "sha256_match": False})
            continue
        live_digest = hashlib.sha256(live_row[1].encode("utf-8")).hexdigest()
        copy_digest = hashlib.sha256(copy_row[1].encode("utf-8")).hexdigest()
        results.append({"hash": h, "sha256_match": live_row[0] == copy_row[0] and live_digest == copy_digest})
    return results


def read_inherited_settings(conn: sqlite3.Connection) -> list[tuple[str, str]]:
    """Inherited retrieval/fusion settings rows — the settings-leak printout."""
    return [tuple(row) for row in conn.execute(SETTINGS_QUERY)]


def _is_secret_key(key: str) -> bool:
    lowered = key.lower()
    return any(marker in lowered for marker in _SECRET_KEY_MARKERS)


def read_settings_snapshot(conn: sqlite3.Connection) -> dict[str, str]:
    """Non-secret retrieval/fusion/embedding settings rows, keyed by name.

    ``embedding.apiKey`` is a persisted secret; any key naming a credential is
    withheld so the sidecar can never carry one.
    """
    return {k: v for k, v in conn.execute(SETTINGS_SNAPSHOT_QUERY) if not _is_secret_key(k)}


def verify_copy(
    live_path: str, copy_path: str, sample_size: int = 3, rng: random.Random | None = None
) -> dict:
    """Verify an existing copy against a fresh read-only snapshot of the live bank.

    Counts are snapshotted from the live bank at call time (never cached), so a
    copy that drifted since the last regeneration fails the parity gate.
    """
    live = open_readonly(live_path, strict=True)
    live_counts = snapshot_counts(live)
    live_hashes = [row[0] for row in live.execute("SELECT hash FROM entries")]

    copy = open_readonly(copy_path, strict=False)
    try:
        copy_counts = snapshot_counts(copy)
        integrity = integrity_ok(copy)
        sample = (rng or random).sample(live_hashes, min(sample_size, len(live_hashes)))
        spot = spot_check_hashes(live, copy, sample)
        settings = read_inherited_settings(copy)
        settings_snapshot = read_settings_snapshot(copy)
        user_version = copy.execute("PRAGMA user_version").fetchone()[0]
    finally:
        copy.close()
    live.close()

    counts_parity = (
        live_counts["entries"] == copy_counts["entries"]
        and live_counts["embedded"] == copy_counts["embedded"]
        and (
            live_counts["vec_entries"] is None
            or copy_counts["vec_entries"] is None
            or live_counts["vec_entries"] == copy_counts["vec_entries"]
        )
    )
    ok = bool(integrity) and counts_parity and all(r["sha256_match"] for r in spot)

    return {
        "ok": ok,
        "integrity": "ok" if integrity else "FAILED",
        "entries_live": live_counts["entries"],
        "entries_copy": copy_counts["entries"],
        "embedded_live": live_counts["embedded"],
        "embedded_copy": copy_counts["embedded"],
        "vec_entries_live": live_counts["vec_entries"],
        "vec_entries_copy": copy_counts["vec_entries"],
        "spot_check": spot,
        "settings": settings,
        "settings_snapshot": settings_snapshot,
        "user_version": user_version,
        "target": str(copy_path),
    }


def sha256_file(path: str, chunk_size: int = 1024 * 1024) -> str:
    """SHA-256 of a file on disk, read in chunks (the live bank is ~848 MB)."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(chunk_size), b""):
            digest.update(chunk)
    return digest.hexdigest()


def checkpoint_target(path: str) -> dict:
    """Checkpoint a copy's WAL, then take it out of WAL mode.

    ``wal_checkpoint(TRUNCATE)`` leaves no frames behind; ``journal_mode=DELETE``
    is persistent in the database header, so no later reader can find pending
    WAL frames and a sha256 of the main file describes the whole copy. Returns
    the resulting journal mode and the frame count the checkpoint saw (``-1`` on
    a database that was already out of WAL mode, normalized to 0).
    """
    conn = sqlite3.connect(path)
    try:
        _, frames, _ = conn.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()
        journal_mode = conn.execute("PRAGMA journal_mode=DELETE").fetchone()[0]
    finally:
        conn.close()
    return {"journal_mode": journal_mode, "wal_frames": max(frames, 0)}


def write_sidecar(pin_path: str, pin: dict) -> None:
    """Write the pin sidecar atomically (temp file + replace)."""
    path = Path(pin_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + f".tmp-{os.getpid()}")
    tmp.write_text(json.dumps(pin, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def run_copy_and_verify(
    live_path: str,
    target_path: str,
    sample_size: int = 3,
    rng: random.Random | None = None,
    pin_path: str | None = None,
) -> dict:
    """Copy live_path to target_path (.backup via read-only source) and verify.

    The target is written to a temp path, checkpointed out of WAL mode, then
    atomically renamed into place, so a concurrent reader never sees a
    half-written copy and the final file is self-contained. A sidecar JSON pin
    (``<target>.pin.json`` unless ``pin_path`` overrides) records the copy's
    sha256, counts, user_version, journal mode and settings snapshot.
    """
    live = open_readonly(live_path, strict=True)
    target = Path(target_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp_target = target.with_name(f"{target.name}.tmp-{os.getpid()}")
    dst = sqlite3.connect(tmp_target)
    try:
        live.backup(dst)
    finally:
        dst.close()
        live.close()
    wal = checkpoint_target(str(tmp_target))
    os.replace(tmp_target, target)
    report = verify_copy(live_path, str(target), sample_size=sample_size, rng=rng)
    report.update(wal)
    pin_path = pin_path or f"{target_path}.pin.json"
    pin = {
        "path": str(target.resolve()),
        "sourcePath": str(Path(live_path).resolve()),
        "sha256": sha256_file(str(target)),
        "bytes": target.stat().st_size,
        "entries": report["entries_copy"],
        "embedded": report["embedded_copy"],
        "vecEntries": report["vec_entries_copy"],
        "userVersion": report["user_version"],
        "journalMode": report["journal_mode"],
        "walFrames": report["wal_frames"],
        "settings": report["settings_snapshot"],
        "verified": report["ok"],
        "copiedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    write_sidecar(pin_path, pin)
    report["pin_path"] = str(pin_path)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", default=DEFAULT_LIVE, help="live bank path (read-only access)")
    parser.add_argument("--target", default=DEFAULT_TARGET, help="copy target path")
    parser.add_argument("--sample-size", type=int, default=3, help="spot-check sample size")
    parser.add_argument(
        "--pin",
        default=None,
        help="sidecar pin path (default: <target>.pin.json; copy runs only)",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="verify an existing copy; do not regenerate it",
    )
    args = parser.parse_args(argv)

    if not Path(args.live).exists():
        print(f"FAIL: live bank not found: {args.live}")
        return 1

    if args.verify_only:
        if not Path(args.target).exists():
            print(f"FAIL: copy not found: {args.target}")
            return 1
        report = verify_copy(args.live, args.target, sample_size=args.sample_size)
    else:
        report = run_copy_and_verify(
            args.live, args.target, sample_size=args.sample_size, pin_path=args.pin
        )
    print(f"live bank:  {args.live}")
    print(f"copy:       {report['target']}")
    if "pin_path" in report:
        print(f"pin:        {report['pin_path']}")
    print(f"integrity_check = {report['integrity']}")
    print(f"entries:    live={report['entries_live']}  copy={report['entries_copy']}")
    print(f"embedded:   live={report['embedded_live']}  copy={report['embedded_copy']}")
    print(f"vec_entries: live={report['vec_entries_live']}  copy={report['vec_entries_copy']}")
    for r in report["spot_check"]:
        print(f"spot-check  {r['hash'][:12]} sha256 {'match' if r['sha256_match'] else 'MISMATCH'}")
    print("inherited settings (settings-leak printout):")
    for key, value in report["settings"]:
        print(f"  {key} = {value}")
    if report["ok"]:
        print("VERIFIED: copy is healthy and matches the live snapshot")
        return 0
    print("FAIL: copy verification failed (see mismatches above)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
