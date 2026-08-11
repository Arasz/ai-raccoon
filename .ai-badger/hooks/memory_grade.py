#!/usr/bin/env python3
"""Grade a memory_search result — writes to the search_quality table.

Usage: python3 memory_grade.py grade <correlationId> <1-5> [note]

Replaces the old JSONL-based grading with server-side search_quality table writes.
See docs/plans/2026-08-11-search-quality-metric-plan.md.
"""
from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path


def _find_memory_db() -> Path | None:
    """Find the ai-raccoon memory.db in the default data root."""
    default = Path.home() / ".ai-raccoon" / "memory.db"
    if default.exists():
        return default
    return None


def grade(correlation_id: str, usefulness: int, note: str | None = None) -> bool:
    """Update the search_quality row with a usefulness grade. Returns True if a row was updated."""
    db_path = _find_memory_db()
    if db_path is None:
        print("memory_grade: memory.db not found", file=sys.stderr)
        return False
    try:
        conn = sqlite3.connect(str(db_path))
        try:
            cur = conn.execute(
                "UPDATE search_quality SET usefulness_grade = ?, grade_note = ? WHERE correlation_id = ?",
                (usefulness, note, correlation_id),
            )
            conn.commit()
            updated = cur.rowcount > 0
            if not updated:
                print(f"memory_grade: no row for correlationId {correlation_id}", file=sys.stderr)
            return updated
        finally:
            conn.close()
    except Exception as exc:
        print(f"memory_grade: {exc}", file=sys.stderr)
        return False


def main(argv: list[str] | None = None) -> int:
    args = argv or sys.argv[1:]
    if len(args) < 3 or args[0] != "grade":
        print("Usage: memory_grade.py grade <correlationId> <1-5> [note]", file=sys.stderr)
        return 1
    correlation_id = args[1]
    try:
        usefulness = int(args[2])
    except ValueError:
        print(f"memory_grade: invalid grade '{args[2]}'", file=sys.stderr)
        return 1
    if not 1 <= usefulness <= 5:
        print(f"memory_grade: grade must be 1-5, got {usefulness}", file=sys.stderr)
        return 1
    note = " ".join(args[3:]) if len(args) > 3 else None
    ok = grade(correlation_id, usefulness, note)
    if ok:
        # Also append to the legacy JSONL log for backward compatibility
        _append_jsonl(correlation_id, usefulness, note)
    return 0 if ok else 1


def _append_jsonl(correlation_id: str, usefulness: int, note: str | None) -> None:
    """Append to the legacy JSONL log alongside the table write."""
    import datetime
    log_path = Path.home() / ".ai-badger" / "memory-grade" / "memory-quality.jsonl"
    try:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        entry = {
            "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "correlationId": correlation_id,
            "usefulness": usefulness,
            "note": note,
        }
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")
    except OSError:
        pass  # best-effort


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
