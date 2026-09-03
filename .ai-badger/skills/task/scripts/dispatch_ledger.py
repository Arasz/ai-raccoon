"""Per-session ledger of recent Agent dispatches, so a gate can tell fan-out from a lone lane.

State lives in the badger store's `dispatch_lanes` table (one row per session, entries as
JSON). Every read fails open (0 siblings) — a gate that cannot read its own state must
allow.

Why a window and not PreToolUse/PostToolUse pairing, and why one row per session rather
than per-dispatch rows: `docs/changelog/0.138.0-a-contract-with-no-gate-behind-it.md`.
"""
from __future__ import annotations

import importlib.util
import json
import sqlite3
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

LEDGER_DIR = Path.home() / ".ai-badger" / "dispatch-lanes"

# Housekeeping only; the window passed to concurrent() is what decides parallelism.
PRUNE_SECONDS = 3600.0

# How long a recorded dispatch keeps counting as a live lane. UNMEASURED estimate standing
# in for "is that agent still running"; errs long. Tradeoff: `docs/changelog/0.138.0-a-contract-with-no-gate-behind-it.md`.
DEFAULT_WINDOW_SECONDS = 90.0


def _load_badger_store():
    """The store module: already-imported, importable, or the vendored copy beside this script."""
    if "badger_store" in sys.modules:
        return sys.modules["badger_store"]
    try:
        import badger_store  # pylint: disable=import-outside-toplevel,redefined-outer-name
        return badger_store
    except ImportError:
        pass
    try:
        path = Path(__file__).resolve().parent / "badger_store.py"
        spec = importlib.util.spec_from_file_location("badger_store", path)
        module = importlib.util.module_from_spec(spec)
        sys.modules["badger_store"] = module
        spec.loader.exec_module(module)
        return module
    except (OSError, ValueError):
        return None


badger_store = _load_badger_store()


def _now() -> str:
    """UTC ISO-8601 timestamp for the row's updated_at column."""
    return datetime.now(timezone.utc).isoformat()


def _safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe; the empty string is not a valid ledger.

    Keeps [A-Za-z0-9._-] and guards dot-only segments. Same rule as memory-first's markers.
    """
    if not session_id:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(session_id)
    )
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def ledger_path(session_id: Optional[str]) -> Path:
    """The legacy ledger file for a session (empty path when no session id); the lane's
    store row is keyed on this file's sanitized name."""
    safe = _safe_session(session_id)
    return LEDGER_DIR / safe if safe else Path("")


def _open_store():
    """The user store, or None when the store machinery is unavailable (fail open)."""
    if badger_store is None:
        return None
    try:
        return badger_store.open_user()
    except (OSError, sqlite3.Error, ValueError):
        return None


def _parse_entries(raw: Any) -> list[dict]:
    """The entries JSON column as a list; a corrupt or wrong-shaped cell yields nothing."""
    if not isinstance(raw, str):
        return []
    try:
        parsed = json.loads(raw)
    except ValueError:
        return []
    if not isinstance(parsed, list):
        return []
    return [entry for entry in parsed if isinstance(entry, dict)]


def _entry_ts(entry: dict) -> Optional[float]:
    try:
        return float(entry.get("ts"))
    except (TypeError, ValueError):
        return None


def record(session_id: Optional[str], tool_use_id: str, now: Optional[float] = None) -> bool:
    """Append this dispatch to its session's ledger row; False on no session id or failure.

    One serialized transaction: the store's BEGIN IMMEDIATE makes the read-append-write
    atomic, so a fan-out's overlapping hook processes cannot drop each other's entries.
    """
    lane = _safe_session(session_id)
    if not lane or not tool_use_id:
        return False
    stamp = time.time() if now is None else now
    # A newline in the id would forge extra entries.
    tid = str(tool_use_id).replace("\n", "_").replace("\r", "_")
    for attempt in (0, 1):
        store = _open_store()
        if store is None:
            return False
        try:
            store.migrate("dispatch_lanes")
            store.conn.execute("BEGIN IMMEDIATE")
            try:
                row = store.conn.execute(
                    "SELECT entries FROM dispatch_lanes WHERE lane_id = ?", (lane,)).fetchone()
                entries = _parse_entries(row[0] if row else None)
                entries.append({"ts": str(stamp), "tool_use_id": tid})
                kept = [entry for entry in entries
                        if (ts := _entry_ts(entry)) is not None
                        and 0 <= stamp - ts <= PRUNE_SECONDS]
                store.conn.execute(
                    "INSERT OR REPLACE INTO dispatch_lanes(lane_id, entries, updated_at) "
                    "VALUES (?, ?, ?)",
                    (lane, json.dumps(kept), _now()))
                store.conn.commit()
            except BaseException:
                store.conn.rollback()
                raise
            badger_store._assert_file_perms(store.db_path)  # pylint: disable=protected-access
            badger_store.notify_write(store.db_path)
            return True
        except (OSError, sqlite3.Error, ValueError):
            # A fan-out's overlapping opens race the store's first-open sequence
            # (WAL conversion, root mkdir) and can lose transiently; one retry is
            # safe because the append is idempotent — concurrent() counts distinct
            # tool_use_ids, so a re-appended id never counts twice.
            if attempt:
                return False
        finally:
            store.close()
    return False


def concurrent(session_id: Optional[str], tool_use_id: str,
               now: Optional[float] = None, window: float = DEFAULT_WINDOW_SECONDS) -> int:
    """How many *other* dispatches this session recorded inside `window` seconds.

    Counts distinct tool_use_ids so a retried dispatch never counts as its own sibling.
    """
    lane = _safe_session(session_id)
    if not lane:
        return 0
    store = _open_store()
    if store is None:
        return 0
    stamp = time.time() if now is None else now
    try:
        store.migrate("dispatch_lanes")
        row = store.conn.execute(
            "SELECT entries FROM dispatch_lanes WHERE lane_id = ?", (lane,)).fetchone()
    except (OSError, sqlite3.Error, ValueError):
        return 0  # D31: a broken store never blocks a caller
    finally:
        store.close()
    # `0 <=` too: a backward clock step leaves entries ahead of now, counted forever.
    siblings = {
        entry.get("tool_use_id")
        for entry in _parse_entries(row[0] if row else None)
        if (ts := _entry_ts(entry)) is not None
        and 0 <= stamp - ts <= window
        and entry.get("tool_use_id") != str(tool_use_id)
        and entry.get("tool_use_id") is not None
    }
    return len(siblings)
