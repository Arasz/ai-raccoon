#!/usr/bin/env python3
"""PostToolUse hook: nudge to commit once uncommitted files cross a threshold.

Advisory only, never blocking: emits `additionalContext` alone, exit 0, and never
`decision`/`permissionDecision`/`continue` — a hook coercing tool calls is exactly what
docs/changelog/0.33.0-no-third-party-tool-call-interception.md documents ripping out.

The trigger is the live `git status --porcelain` count (via commit_reminder.uncommitted_files),
debounced by a persisted marker so it fires once per threshold-crossing and re-arms after a
commit (commit_reminder.should_remind). Silent whenever there is nothing to say: not an edit
tool, no resolvable project root, below threshold, already reminded at this count, or any
internal error.
"""
from __future__ import annotations

import json
import os
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import commit_reminder  # pylint: disable=wrong-import-position
import impact_estimator  # pylint: disable=wrong-import-position

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

DEFAULT_THRESHOLD = 5
COMPONENT = "commit_reminder_hook"


# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: Dict[str, Any] = {}


def _debug(event: str, **fields: Any) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def resolve_project_root(payload: Dict[str, Any]) -> Optional[str]:
    """`$CLAUDE_PROJECT_DIR` first, else the payload's own `cwd` field, else None."""
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root:
        return env_root
    cwd = payload.get("cwd")
    return cwd if cwd else None


def _threshold() -> int:
    """`AI_BADGER_COMMIT_REMINDER_THRESHOLD`, falling back to the default on any bad value."""
    try:
        return int(os.environ.get("AI_BADGER_COMMIT_REMINDER_THRESHOLD", str(DEFAULT_THRESHOLD)))
    except (TypeError, ValueError):
        return DEFAULT_THRESHOLD


def _escalate_after() -> int:
    """`AI_BADGER_COMMIT_ESCALATE_AFTER`, falling back to the default on any bad value."""
    try:
        return int(os.environ.get("AI_BADGER_COMMIT_ESCALATE_AFTER",
                                  str(commit_reminder.ESCALATE_AFTER)))
    except (TypeError, ValueError):
        return commit_reminder.ESCALATE_AFTER


def _now_iso() -> str:
    """UTC timestamp for the moment a command first went unanswered."""
    return datetime.now(timezone.utc).isoformat()


def main() -> int:
    """Read the hook payload from stdin; print additionalContext iff the debounce fires."""
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    if not isinstance(payload, dict):
        return 0
    _PAYLOAD.update(payload)

    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    if not commit_reminder.is_edit_tool(tool_name):
        _debug("skip", reason="not_edit_tool")
        return 0

    root = resolve_project_root(payload)
    if root is None:
        _debug("skip", reason="no_root")
        return 0

    files = commit_reminder.uncommitted_files(root)
    count = len(files)
    threshold = _threshold()
    fires, at_risk, entry = commit_reminder.advance(
        commit_reminder.get_entry(root), count, threshold, _escalate_after(),
        now=_now_iso(), session=str(payload.get("session_id") or ""),
    )
    commit_reminder.set_entry(root, entry)
    _debug("checked", project=root, count=count, threshold=threshold)

    if not fires:
        return 0

    use_graph = os.environ.get("AI_BADGER_COMMIT_REMINDER_IMPACT") == "graph"
    reason = impact_estimator.estimate_impact(files, root, use_graph=use_graph)
    message = commit_reminder.build_message(count, reason, entry["fires"], at_risk)

    _debug("fire", project=root, count=count, unanswered=entry["fires"], atRisk=at_risk)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PostToolUse",
            "additionalContext": message,
        }
    }))
    return 0


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
