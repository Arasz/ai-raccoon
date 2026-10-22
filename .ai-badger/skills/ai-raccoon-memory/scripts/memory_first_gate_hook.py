#!/usr/bin/env python3
"""PreToolUse hook for the memory-first gate.

Deny a text-search call until every pattern it searches for was memory-searched for the
project. The first sighting of a pattern runs one memory search, captures shell grep-family
output asynchronously, and returns the retrieval hash in the deny reason.
Searched queries of the agent's own query are recorded by
memory_first_gate_post_hook.py. Exit 0 on every path — Copilot's command preToolUse hooks
are fail-closed, so a crash here would deny the very tool call the gate only meant to gate.
All logic lives in memory_first_gate.py; this entry only transports.
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


def tool_input(payload: Dict[str, Any]) -> Any:
    """The call's arguments; Copilot may send `toolArgs` as a JSON string."""
    args = payload.get("tool_input") or payload.get("toolArgs")
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except ValueError:
            return {}
    return args


def main(argv: Optional[List[str]] = None) -> int:
    """Read the hook payload from stdin; print a deny JSON or nothing; always exit 0.

    Silence means allow on every host (Claude and Copilot both treat a PreToolUse hook
    with no output as no opinion), so pass paths emit nothing.
    """
    del argv
    payload = _payload()
    decision = gate.decide(
        _host(payload),
        payload.get("tool_name") or payload.get("toolName") or "",
        tool_input(payload),
        payload.get("session_id") or payload.get("sessionId"),
        cwd=str(payload.get("cwd") or ""),
    )
    if decision:
        print(json.dumps(decision))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        # A hook failure must never block the tool call it only meant to gate.
        sys.exit(0)
