#!/usr/bin/env python3
"""`SessionStart` / `SessionEnd` entry for the per-session memory broker (Claude and Copilot).

SessionStart starts a detached broker for the session and returns at once; SessionEnd asks it to
exit. Every other event, a missing session id and any failure end silently with exit 0: the
searches fall back to a per-call proxy when no broker is there.
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional

BROKER_MODULE_NAME = "ai_badger_memory_broker_entry"
START_EVENTS = frozenset({"sessionstart"})
END_EVENTS = frozenset({"sessionend"})


def _load_broker() -> Optional[Any]:
    """The sibling `memory_broker.py` loaded by path; None when absent or broken."""
    cached = sys.modules.get(BROKER_MODULE_NAME)
    if cached is not None:
        return cached
    path = Path(__file__).resolve().parent / "memory_broker.py"
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location(BROKER_MODULE_NAME, path)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[BROKER_MODULE_NAME] = module
    try:
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught
        sys.modules.pop(BROKER_MODULE_NAME, None)
        return None
    return module


def _payload() -> Dict[str, Any]:
    try:
        data = json.load(sys.stdin)
    except (ValueError, OSError):
        return {}
    return data if isinstance(data, dict) else {}


def main() -> int:
    """Start or stop the session's broker for a start or end event; 0 always."""
    payload = _payload()
    event = str(payload.get("hook_event_name") or payload.get("hookEventName") or "").lower()
    session = payload.get("session_id") or payload.get("sessionId")
    if event not in START_EVENTS | END_EVENTS or not isinstance(session, str) or not session:
        return 0
    broker = _load_broker()
    if broker is None:
        return 0
    if event in START_EVENTS:
        broker.ensure_started(session, dict(os.environ))
    else:
        broker.shutdown(session, os.environ.get("HOME"))
    return 0


def guarded_main() -> int:
    """Run main(): a broken hook must never block a session."""
    try:
        return main()
    except Exception:  # pylint: disable=broad-exception-caught
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
