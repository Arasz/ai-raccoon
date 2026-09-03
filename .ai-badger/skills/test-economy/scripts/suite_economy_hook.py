#!/usr/bin/env python3
"""PostToolUse hook: command the test-run economy once full-suite runs start repeating.

Named suite_economy_hook (not test_*): the scaffold's delivery ignores test_*.py — a
production hook script must not wear a test's name.

Advisory only, never blocking: emits `additionalContext` alone, exit 0, and never
`decision`/`permissionDecision`/`continue` — a hook coercing tool calls is exactly what
docs/changelog/0.33.0-no-third-party-tool-call-interception.md documents ripping out.

The trigger is the command of a shell-shaped tool call, classified by
suite_economy.is_test_run. Filtered runs never count; a full-suite run past the session's
budget (3rd by default) fires the nudge, and from the escalation bar (5th) every run nags.
Silent whenever there is nothing to say: not a shell tool, no command, not a test run, no
resolvable project root, under budget, or any internal error.
"""
from __future__ import annotations

import json
import os
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

sys.path.insert(0, str(Path(__file__).resolve().parent))
import suite_economy  # pylint: disable=wrong-import-position

# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members
try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "test_economy_hook"

_PAYLOAD: Dict[str, Any] = {}


def _debug(event: str, **fields: Any) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def resolve_project_root(payload: Dict[str, Any]) -> str:
    """`$CLAUDE_PROJECT_DIR` first, else the payload's own `cwd` field, else empty."""
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root:
        return env_root
    cwd = payload.get("cwd")
    return cwd if isinstance(cwd, str) else ""


def _session(payload: Dict[str, Any]) -> str:
    """The payload's session id, or a shared default bucket when the transport omits it."""
    session = payload.get("session_id") or payload.get("sessionId") or ""
    return str(session) if session else "default"


def main() -> int:
    """Read the hook payload from stdin; print additionalContext iff the count crosses."""
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    if not isinstance(payload, dict):
        return 0
    _PAYLOAD.update(payload)

    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    if not suite_economy.is_shell_tool(tool_name):
        _debug("skip", reason="not_shell_tool")
        return 0

    command = suite_economy.extract_command(payload)
    run = suite_economy.is_test_run(command)
    if run is None:
        _debug("skip", reason="not_a_test_run")
        return 0

    root = resolve_project_root(payload)
    if not root:
        _debug("skip", reason="no_root")
        return 0

    entry = suite_economy.get_entry(root)
    fires, escalated, entry = suite_economy.advance_session(
        entry, _session(payload), run["kind"] == "full", now=_now_iso(),
    )
    suite_economy.set_entry(root, entry)
    _debug("checked", project=root, runner=run["runner"], kind=run["kind"])

    if not fires:
        return 0

    gates = suite_economy.detect_local_gates(root)
    message = suite_economy.build_message(
        entry["sessions"][_session(payload)]["full"], run["runner"], gates,
        escalated=escalated)

    _debug("fire", project=root, runner=run["runner"], escalated=escalated)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PostToolUse",
            "additionalContext": message,
        }
    }))
    return 0


def _now_iso() -> str:
    """UTC timestamp for the moment the first nudge fired."""
    return datetime.now(timezone.utc).isoformat()


HOOK_ERRORS_FILE = Path.home() / ".ai-badger" / "hook-errors.log"
MAX_ERROR_LOG_BYTES = 1_000_000


def record_hook_failure(where):
    """Leave one content-free line behind before a hook swallows an exception.

    Type and location only: an exception message can quote scanned input.
    """
    exc_type, _, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    name = exc_type.__name__ if exc_type else "Unknown"
    print(f"[ai-badger] {where} hook failed: {name} at {at}", file=sys.stderr)
    try:
        HOOK_ERRORS_FILE.parent.mkdir(parents=True, exist_ok=True)
        if HOOK_ERRORS_FILE.exists() and HOOK_ERRORS_FILE.stat().st_size > MAX_ERROR_LOG_BYTES:
            HOOK_ERRORS_FILE.unlink()
        with HOOK_ERRORS_FILE.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where} {name} at {at}\n")
    except OSError:
        pass


def guarded_main():
    """Run main(): a hook never breaks the session, but never fails invisibly either."""
    try:
        return main() or 0
    except Exception:  # pylint: disable=broad-exception-caught
        record_hook_failure(COMPONENT)
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
