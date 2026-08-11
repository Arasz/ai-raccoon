#!/usr/bin/env python3
"""PostToolUse hook: record the memory-first gate's consulted marker.

The gate (memory_first_gate_hook.py, PreToolUse) blocks repo text-search until the
session ran memory_search. Claude/Copilot have no in-process plugin, so this
PostToolUse entry touches the consulted marker the same way the Hermes plugin's
post_tool_call does. Advisory only, never blocking: no output, exit 0 on every
path — Copilot's command preToolUse hooks are fail-closed, so a crash here must
never deny the tool call the gate only meant to gate.

All logic lives in memory_first_gate.py; this entry only transports.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import memory_first_gate as gate  # pylint: disable=wrong-import-position


def main(argv: Optional[list] = None) -> int:
    """Read the hook payload from stdin; record the marker for memory_search; exit 0."""
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    if not gate.is_memory_search(tool_name):
        return 0
    session_id = payload.get("session_id") or payload.get("sessionId")
    gate.record_search(session_id)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        # Advisory only — a hook failure must never block the tool call.
        sys.exit(0)
