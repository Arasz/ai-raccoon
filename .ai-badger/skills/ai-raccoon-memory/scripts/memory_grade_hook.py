#!/usr/bin/env python3
"""PostToolUse hook: after memory_search stash results, after Read detect follow-through.

Agent-agnostic passive follow-through measurement (Joachims implicit feedback):
1. After memory_search: append {correlationId, sourceFiles, ts} as one `searches` row.
2. After Read/ReadFile/read_file: check the stash for a path match within 60s.
3. Match → write follow_through_count/files to the search_quality table via direct SQLite.

State lives in the user-level store (~/.ai-badger/ai-badger.db); a legacy
~/.ai-badger/memory-grade/searches.json is imported and renamed on the first write (D6),
and its rows join the store's 60-day retention (G0-Q2). Advisory only — never blocking,
exit 0 on every path.
"""

from __future__ import annotations

import json
import os
import sqlite3
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

_SCRIPTS_DIR = Path(__file__).resolve().parent
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))
try:
    import badger_store  # pylint: disable=wrong-import-position  # noqa: E402
except ImportError:  # a partial deployment (the vendored copy never landed)
    badger_store = None  # type: ignore[assignment]  # every use sits in a fail-open path

FOLLOW_THROUGH_WINDOW = 60  # seconds
RETENTION_DAYS = 60  # G0-Q2: searches joins the log tables' 60-day retention
SEARCHES_FILE = Path.home() / ".ai-badger" / "memory-grade" / "searches.json"


def open_store() -> "badger_store.Store":
    """The user store narrowed to the searches family, rebound to this module's path.

    The lambda resolves SEARCHES_FILE at call time, so a redirected module constant
    moves the legacy import with it — the awm hooks' open_store pattern.
    """
    families = {"searches": badger_store.Family(
        table="searches", db="user", legacy_path=lambda: SEARCHES_FILE,
        legacy_kind="recent",
    )}
    return badger_store.open_user(families=families)


def _is_memory_search(tool_name: str) -> bool:
    """True for any naming spelling of the memory_search tool."""
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    if name == "memory_search":
        return True
    # pi spells MCP tool names `mcp_<server>_<tool>` (single underscores) and a server name
    # may itself contain underscores, so the tool part is matched as any trailing `_`-joined
    # tail of the spelling (verified live: pi carries `mcp_ai-raccoon_memory_search`).
    if not tool_name.startswith("mcp_") or tool_name.startswith("mcp__"):
        return False
    parts = tool_name[len("mcp_"):].split("_")
    return any("_".join(parts[i:]) == "memory_search" for i in range(1, len(parts)))


def _is_read_file(tool_name: str) -> bool:
    """True for file-reading tool names across agents (Hermes/Claude/Copilot)."""
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name in ("read_file", "Read", "ReadFile", "readfile")


def _extract_correlation_id(result: Dict[str, Any]) -> str:
    """Extract correlationId from the ApiEnvelope meta."""
    meta = result.get("meta") or result.get("Meta") or {}
    return meta.get("correlationId") or meta.get("CorrelationId") or ""


def _extract_source_files(result: Dict[str, Any]) -> List[str]:
    """Extract unique sourceFile paths from search results."""
    search_results = result.get("results") or result.get("data") or []
    files = []
    for r in search_results:
        sf = r.get("sourceFile") or r.get("SourceFile")
        if sf and sf not in files:
            files.append(sf)
    return files


def _extract_file_path(result: Dict[str, Any]) -> str:
    """Extract the file path from a read_file/Read response."""
    return result.get("path") or result.get("filePath") or result.get("file_path") or ""


def _stash_search_sources(result: Dict[str, Any]) -> None:
    """After memory_search, append {correlationId, sourceFiles, ts} as one searches row.

    The row's ts column is the entry's own ts as ISO-8601 (the prune must parse it); the
    payload keeps the entry verbatim, ts field included. A legacy searches.json is
    imported and renamed by this first write (D6) — never written again.
    """
    corr_id = _extract_correlation_id(result)
    if not corr_id:
        return
    source_files = _extract_source_files(result)
    if not source_files:
        return
    now = time.time()
    entry = {"correlationId": corr_id, "sourceFiles": source_files, "ts": now}
    store = open_store()  # first write imports + renames a legacy searches.json (D6)
    try:
        store.log_append("searches", badger_store.iso_row_ts(now), entry)
        # Retention (G0-Q2): the write is the prune opportunity — throttled by the store's
        # pruned_at stamp, one transaction with the DELETE, fail-open (a sqlite error
        # returns 0). The file stash's 120s self-prune is gone with the file: 60 days now.
        store.prune_expired("searches", max_age_days=RETENTION_DAYS)
    finally:
        store.close()


def _legacy_entries() -> List[dict]:
    """A not-yet-migrated searches.json's entries, for the pre-first-write window (D5a).

    Read-only, and only ever non-empty before the first store write: after it the rename
    has removed the file. A resurrected file fails the store open itself (D5c), so this
    never races the migration.
    """
    try:
        data = json.loads(SEARCHES_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    entries = data.get("recent") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        return []
    return [entry for entry in entries if isinstance(entry, dict)]


def _load_searches() -> List[dict]:
    """The stash entries within the follow-through window: store rows first, then
    legacy-only file entries (D5a merge).

    Fail-open (D31): an unopenable store and undecodable payloads degrade to fewer
    entries. The store read is bounded to the window with the ts index (join-review
    finding: reading the whole 60-day table measured linearly, 59 ms at 20k rows) — the
    row ts is the stash moment's iso_row_ts, so the cutoff maps the window onto the same
    clock the writes used, and the caller's payload-ts filter stays the exact gate.
    The legacy file's entries are few and pre-migration only; they merge unbounded.
    """
    entries: List[dict] = []
    try:
        cutoff = badger_store.iso_row_ts(time.time() - FOLLOW_THROUGH_WINDOW)
        store = open_store()
        try:
            for _row_ts, payload in store.log_rows_since("searches", cutoff):
                try:
                    entry = json.loads(payload)
                except ValueError:
                    continue
                if isinstance(entry, dict):
                    entries.append(entry)
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        pass  # advisory: a broken store costs the match, not the hook
    entries.extend(_legacy_entries())
    return entries


def _record_follow_through_sql(correlation_id: str, file_path: str) -> None:
    """Write follow-through directly to the search_quality table via SQLite."""
    db_path = Path.home() / ".ai-raccoon" / "memory.db"
    if not db_path.exists():
        return
    try:
        conn = sqlite3.connect(str(db_path))
        try:
            row = conn.execute(
                "SELECT follow_through_files FROM search_quality WHERE correlation_id = ?",
                (correlation_id,)
            ).fetchone()
            if row is None:
                return
            existing = row[0] or "[]"
            try:
                files = json.loads(existing)
            except (ValueError, TypeError):
                files = []
            if not isinstance(files, list):
                files = []
            if file_path not in files:
                files.append(file_path)
            conn.execute(
                "UPDATE search_quality SET follow_through_count = ?, "
                "follow_through_files = ? WHERE correlation_id = ?",
                (len(files), json.dumps(files), correlation_id)
            )
            conn.commit()
        finally:
            conn.close()
    except Exception:  # pylint: disable=broad-exception-caught
        pass  # best-effort


def _check_follow_through(result: Dict[str, Any]) -> None:
    """After read_file, check the stash for a path match within the window."""
    file_path = _extract_file_path(result)
    if not file_path:
        return
    try:
        file_path = str(Path(file_path).resolve())
    except (ValueError, OSError):
        return

    now = time.time()
    for entry in _load_searches():
        if now - entry.get("ts", 0) > FOLLOW_THROUGH_WINDOW:
            continue
        for sf in entry.get("sourceFiles", []):
            try:
                sf_resolved = str(Path(sf).resolve())
            except (ValueError, OSError):
                continue
            if file_path == sf_resolved or file_path.startswith(sf_resolved + os.sep):
                _record_follow_through_sql(entry["correlationId"], sf)
                return  # first match wins


def _hook_result(payload: Dict[str, Any]) -> Dict[str, Any]:
    """The tool result document from the hook payload, {} when absent or unparsable."""
    result = payload.get("result") or payload.get("response") or {}
    if isinstance(result, str):
        try:
            result = json.loads(result)
        except (ValueError, TypeError):
            return {}
    return result if isinstance(result, dict) else {}


def main(argv: Optional[list] = None) -> int:
    """Read hook payload from stdin; stash or check follow-through; exit 0 on every path."""
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0

    tool_name = payload.get("tool_name") or payload.get("toolName") or ""

    if _is_memory_search(tool_name):
        try:
            _stash_search_sources(_hook_result(payload))
        except Exception:  # pylint: disable=broad-exception-caught
            pass  # advisory: a failed stash costs the metric, never the tool call

    elif _is_read_file(tool_name):
        try:
            _check_follow_through(_hook_result(payload))
        except Exception:  # pylint: disable=broad-exception-caught
            pass  # advisory: a failed match costs the metric, never the tool call

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
