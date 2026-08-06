#!/usr/bin/env python3
"""PreToolUse hook for the memory-first gate.

Deny text-search tool calls until the session consulted memory_search, with a
3-strike pass-through so an agent cannot stall. The consulted marker is recorded by
memory_grade_hook.py (the PostToolUse entry both hosts already run) so one process
serves each memory_search. Exit 0 on every path — Copilot's command preToolUse hooks
are fail-closed, so a crash here would deny the very tool call the gate only meant to
gate. All logic lives in memory_first_gate.py; this entry only transports.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import memory_first_gate as gate  # pylint: disable=wrong-import-position


def _payload() -> Dict[str, Any]:
    """Parse stdin; {} on malformed JSON or a non-object (never raise)."""
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return {}
    return data if isinstance(data, dict) else {}


def _host(payload: Dict[str, Any]) -> str:
    """The transport, from the event name or the payload's spelling."""
    event = payload.get("hook_event_name") or payload.get("hookEventName") or ""
    if event in ("PreToolUse", "PostToolUse"):
        return "claude"
    if event in ("preToolUse", "postToolUse"):
        return "copilot"
    return "copilot" if "toolName" in payload else "claude"


def _deny(payload: Dict[str, Any]) -> Dict[str, Any]:
    """The per-host deny payload, or {} once the 3-strike guard passes through."""
    session_id = payload.get("session_id") or payload.get("sessionId")
    if gate.deny_count(session_id) >= gate.MAX_DENIALS:
        return {}
    gate.increment_denials(session_id)
    return gate.build_decision(
        _host(payload),
        payload.get("tool_name") or payload.get("toolName") or "",
        payload.get("tool_input") or payload.get("toolArgs"),
        session_id,
        cwd=str(payload.get("cwd") or ""),
    )


def main(argv: Optional[List[str]] = None) -> int:
    """Read the hook payload from stdin; print a deny JSON or nothing; always exit 0.

    Silence means allow on every host (Claude and Copilot both treat a PreToolUse hook
    with no output as no opinion), so pass paths emit nothing — the same discipline as
    memory_grade_hook.py.
    """
    args = list(sys.argv[1:] if argv is None else argv)
    payload = _payload()
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    tool_input = payload.get("tool_input") or payload.get("toolArgs")
    session_id = payload.get("session_id") or payload.get("sessionId")

    if not gate.is_text_search(tool_name, tool_input):
        return 0
    if gate.search_consulted(session_id):
        return 0

    decision = _deny(payload)
    if decision:
        print(json.dumps(decision))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        # A hook failure must never block the tool call it only meant to gate.
        sys.exit(0)
