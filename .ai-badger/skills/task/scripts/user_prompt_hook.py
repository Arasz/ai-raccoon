#!/usr/bin/env python3
"""UserPromptSubmit hook: auto-register a task when the user types `/task <id>`.

This guarantees a tracked entry exists with a start checkpoint the moment a task begins, even if
the model forgets to run `task_tracker.py start`. `task_tracker.py start` is idempotent, so both
paths can run safely without clobbering each other. Also refreshes `current-session.json` on
every prompt so `resolve_own_session()` stays accurate for the rest of the skill's scripts.

This hook does registration only — it has no opinion on prompt markers or any other
UserPromptSubmit concern. If your project also uses the `prompt-markers` skill, register both
hooks as separate entries under the same `UserPromptSubmit` event; Claude Code runs every
registered hook for an event.

Failure handling: a broken hook must never block the user's prompt. Malformed stdin JSON is a
silent no-op, and a failure while registering the task (e.g. a corrupt tracking file) is swallowed
so the prompt always goes through — worst case, the model falls back to running
`task_tracker.py start` itself. Swallowed is not unrecorded: one line goes to stderr and to
`~/.ai-badger/hook-errors.log`.
"""
from __future__ import annotations

import json
import re
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tracker_lib as lib

# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members
try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "task_user_prompt_hook"


# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: dict = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or debug_log.resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)
# Matches `/task <id>` (optionally `/task:something <id>`, or namespaced as a plugin skill —
# `/<plugin>:task <id>` — the form Claude Code uses once this skill is installed via a
# marketplace plugin) at the very start of the prompt. Kept local to this hook, rather than in
# tracker_lib, so prompt parsing stays colocated with its only caller.
TASK_ID_RE = re.compile(r"^/(?:[\w-]+:)?task(?::\S+)?\s+([A-Za-z0-9._-]+)")


def task_id_from_prompt(prompt: str) -> str | None:
    """Return the task id in a leading `/task <id>` invocation, or None if there isn't one."""
    match = TASK_ID_RE.match(prompt.strip())
    return match.group(1) if match else None


def _register_task(task_id: str, session_id: str, transcript: str) -> None:
    checkpoint = lib.make_checkpoint(transcript)
    with lib.tracking_transaction() as store:
        tasks = lib.load_tasks(store)
        entry = lib.find_entry(tasks, task_id)
        if entry is not None and entry.get("state") == lib.STATE_FINISHED:
            return  # user referenced a finished task; nothing to register
        conflict = lib.find_other_entry_with_session(tasks, session_id, task_id)
        if conflict is not None and conflict.get("state") != lib.STATE_FINISHED:
            # current-session.json is a single global pointer shared by every concurrently
            # running session; it can be stale by the time this prompt is processed (another
            # session's hook may have overwritten it since). Don't silently steal session_id
            # from whatever task legitimately owns it — leave this entry untouched.
            return
        if entry is None:
            entry = {"taskId": task_id, "title": "", "branch": "", "resumeAttempts": []}
            tasks["tasks"].append(entry)
        entry.update(
            {
                "sessionId": session_id,
                "transcriptPath": transcript,
                "cwd": str(lib.PROJECT_ROOT),
                "startedAt": entry.get("startedAt") or lib.now_iso(),
                "finishedAt": None,
                "state": entry.get("state") or lib.STATE_STARTED,
                "resumeCommand": f"claude --resume {session_id}",
            }
        )
        lib.save_tasks(store, tasks)

        usage = lib.load_usage(store)
        usage_entry = lib.find_entry(usage, task_id)
        if usage_entry is None:
            usage_entry = {"taskId": task_id, "subagents": [], "grade": None}
            usage["tasks"].append(usage_entry)
        usage_entry["sessionId"] = session_id
        checkpoints = usage_entry.setdefault("checkpoints", {})
        checkpoints.setdefault("start", checkpoint)
        checkpoints["latest"] = checkpoint
        lib.save_usage(store, usage)


def main() -> int:
    """Read the hook payload from stdin, refresh the session, and register a `/task` invocation."""
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    _PAYLOAD.update(payload)
    session_id = payload.get("session_id", "")
    transcript = payload.get("transcript_path", "")
    if session_id:
        lib.save_current_session(session_id, transcript, payload.get("cwd", ""))

    task_id = task_id_from_prompt(payload.get("prompt", ""))
    if not task_id or not session_id:
        _debug("skip", reason="no_task_id" if not task_id else "no_session_id")
        return 0

    try:
        _register_task(task_id, session_id, transcript)
        _debug("register", task=task_id, session=session_id)
    except Exception:  # pylint: disable=broad-exception-caught
        # Registration is a convenience net, not the source of truth: task_tracker.py's own
        # `start` command is idempotent and can register the task later. A hook must never
        # block the user's prompt over a tracking-file problem — but it must say so.
        record_hook_failure("task_register")
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


if __name__ == "__main__":
    sys.exit(main())
