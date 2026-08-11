#!/usr/bin/env python3
"""PostToolUse hook: after memory_search, inject a grade ask with the correlationId.

Reads the tool response from stdin, extracts the correlationId from the
ApiEnvelope meta, and returns additionalContext asking the agent to grade.
The agent calls memory_grade.py (or memory_record_grade MCP tool) with the id.

Advisory only — never blocking, exit 0 on every path.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional


def main(argv: Optional[list] = None) -> int:
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0

    # Extract correlationId from the tool response
    # The response is the ApiEnvelope: {data, meta, result}
    meta = payload.get("meta") or {}
    correlation_id = meta.get("correlationId") or meta.get("CorrelationId")
    if not correlation_id:
        return 0

    # Stash for the next turn (Claude's UserPromptSubmit hook picks it up)
    pending_file = Path.home() / ".ai-badger" / "memory-grade" / "pending.json"
    try:
        pending_file.parent.mkdir(parents=True, exist_ok=True)
        # Read existing
        try:
            raw = pending_file.read_text(encoding="utf-8")
            pending = json.loads(raw) if raw.strip() else {}
        except (OSError, ValueError):
            pending = {}
        if not isinstance(pending, dict):
            pending = {}
        # We don't have the project cwd in the hook payload, use a generic key
        pending["last"] = {"correlationId": correlation_id}
        pending_file.write_text(json.dumps(pending), encoding="utf-8")
    except OSError:
        pass  # best-effort

    # Return additionalContext for the current turn
    grade_script = Path(__file__).resolve().parent / "memory_grade.py"
    print(json.dumps({
        "additionalContext": (
            f"[ai-badger] Rate that memory_search's usefulness 1-5 (5=best, or skip): "
            f"python3 {grade_script} grade {correlation_id} <1-5> [note]"
        )
    }))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
