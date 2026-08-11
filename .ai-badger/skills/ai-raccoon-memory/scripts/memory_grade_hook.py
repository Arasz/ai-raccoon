#!/usr/bin/env python3
"""PostToolUse hook: after memory_search stash results, after Read detect follow-through.

Agent-agnostic passive follow-through measurement (Joachims implicit feedback):
1. After memory_search: stash {correlationId, sourceFiles, timestamp} in a shared file.
2. After Read/ReadFile/read_file: check the stash for a path match within 60s.
3. Match → write follow_through_count/files to the search_quality table via direct SQLite.

Uses file-based state at ~/.ai-badger/memory-grade/searches.json for cross-process
coordination across Claude/Copilot shell-hook invocations. Advisory only — never blocking,
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

FOLLOW_THROUGH_WINDOW = 60  # seconds
SEARCHES_FILE = Path.home() / ".ai-badger" / "memory-grade" / "searches.json"


def _is_memory_search(tool_name: str) -> bool:
    """True for any naming spelling of the memory_search tool."""
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == "memory_search"


def _is_read_file(tool_name: str) -> bool:
    """True for file-reading tool names across agents (Hermes/Claude/Copilot)."""
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name in ("read_file", "Read", "ReadFile", "readfile")


def _load_searches() -> Dict[str, list]:
    """Load the searches stash file; {} on missing/read/malformed."""
    try:
        raw = SEARCHES_FILE.read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _save_searches(stash: Dict[str, list]) -> None:
    """Persist the searches stash, creating parent directories."""
    SEARCHES_FILE.parent.mkdir(parents=True, exist_ok=True)
    SEARCHES_FILE.write_text(json.dumps(stash), encoding="utf-8")


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
    """After memory_search, stash {correlationId, sourceFiles, timestamp}."""
    corr_id = _extract_correlation_id(result)
    if not corr_id:
        return
    source_files = _extract_source_files(result)
    if not source_files:
        return
    now = time.time()
    stash = _load_searches()
    entry = {"correlationId": corr_id, "sourceFiles": source_files, "ts": now}
    stash.setdefault("recent", []).append(entry)
    # Prune old entries
    stash["recent"] = [
        e for e in stash["recent"]
        if now - e.get("ts", 0) < FOLLOW_THROUGH_WINDOW * 2
    ]
    _save_searches(stash)


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
    """After read_file, check stash for path match within the window."""
    file_path = _extract_file_path(result)
    if not file_path:
        return
    try:
        file_path = str(Path(file_path).resolve())
    except (ValueError, OSError):
        return

    stash = _load_searches()
    entries = stash.get("recent", [])
    now = time.time()
    for entry in entries:
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


def main(argv: Optional[list] = None) -> int:
    """Read hook payload from stdin; stash or check follow-through; exit 0."""
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0

    tool_name = payload.get("tool_name") or payload.get("toolName") or ""

    if _is_memory_search(tool_name):
        result = payload.get("result") or payload.get("response") or {}
        if isinstance(result, str):
            try:
                result = json.loads(result)
            except (ValueError, TypeError):
                return 0
        _stash_search_sources(result)

    elif _is_read_file(tool_name):
        result = payload.get("result") or payload.get("response") or {}
        if isinstance(result, str):
            try:
                result = json.loads(result)
            except (ValueError, TypeError):
                result = {}
        _check_follow_through(result)

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
