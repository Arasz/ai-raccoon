#!/usr/bin/env python3
"""PostToolUse hook: record a query for the memory-first gate.

The call's query is recorded as memory-searched for the project, so a grep for a pattern
the query contains passes the gate (memory_first_gate_hook.py); the session's telemetry row
is written as before. Advisory only, never blocking: no output, exit 0 on every path.

All logic lives in memory_first_gate.py; this entry only transports.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import memory_first_gate as gate  # pylint: disable=wrong-import-position


def _args(payload: Dict[str, Any]) -> Dict[str, Any]:
    """The call's arguments; Copilot may send `toolArgs` as a JSON string."""
    args = payload.get("tool_input") or payload.get("toolArgs") or {}
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except ValueError:
            return {}
    return args if isinstance(args, dict) else {}


def _failed(payload: Dict[str, Any]) -> bool:
    """True when the host reports the call failed: Claude's PostToolUseFailure event, or a
    Copilot toolResult whose resultType is not success."""
    if (payload.get("hook_event_name") or payload.get("hookEventName")) == "PostToolUseFailure":
        return True
    result = payload.get("toolResult")
    return isinstance(result, dict) and result.get("resultType", "success") != "success"


def main(argv: Optional[list] = None) -> int:
    """Read the hook payload from stdin; record a query's query; exit 0."""
    del argv
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    if not gate.is_query(tool_name):
        return 0
    gate.record_search(payload.get("session_id") or payload.get("sessionId"))
    gate.credit_search(gate.project_id(str(payload.get("cwd") or "")), _args(payload),
                       failed=_failed(payload))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        # Advisory only — a hook failure must never block the tool call.
        sys.exit(0)
