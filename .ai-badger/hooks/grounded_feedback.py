"""Pending-context stashes for Hermes plugin.

Two stash-and-pop patterns that keep ai_badger_hooks.py under the 1000-line
pylint cap:
- Grounded feedback (Rule 3C): terminal failure output for next-turn evidence.
- Commit reminder: uncommitted-file nudge surfaced in pre_llm_inject_context.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Dict, Optional

# --- Grounded feedback (Rule 3C) ---

PENDING_FEEDBACK_FILE = Path.home() / ".ai-badger" / "pending-feedback.json"
MAX_FEEDBACK_LINES = 30
MAX_FEEDBACK_CHARS = 3000


def load_pending_feedback(path: Optional[Path] = None) -> Dict[str, str]:
    """Load the pending-feedback file; ``{}`` on missing file or bad JSON."""
    try:
        raw = (path or PENDING_FEEDBACK_FILE).read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def save_pending_feedback(pending: Dict[str, str],
                          path: Optional[Path] = None) -> None:
    """Persist the pending-feedback file, owner-only (failure logs may hold secrets)."""
    target = path or PENDING_FEEDBACK_FILE
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(pending), encoding="utf-8")
    try:
        target.chmod(0o600)
    except OSError:
        pass


def set_pending_feedback(project: str, message: str,
                         path: Optional[Path] = None) -> None:
    """Stash grounded feedback for *project*."""
    pending = load_pending_feedback(path)
    pending[str(Path(project).resolve())] = message
    save_pending_feedback(pending, path)


def pop_pending_feedback(project: str,
                         path: Optional[Path] = None) -> Optional[str]:
    """Return and clear the pending grounded feedback for *project*, or None.

    Fail-open: if persisting the cleared state fails (read-only fs, permissions),
    the message is still returned — the prompt hook must never break over cleanup.
    A stale entry then re-surfaces next turn, which is harmless.
    """
    pending = load_pending_feedback(path)
    key = str(Path(project).resolve())
    message = pending.pop(key, None)
    if message is not None:
        try:
            save_pending_feedback(pending, path)
        except OSError:
            pass
    return message


def stash_if_failure(tool_name: str, result: str, project: str,
                     status: str = "ok", error_type: str = "",
                     debug_fn=None) -> None:
    """After a terminal/Bash command that genuinely failed, stash its output.

    Only stashes when *status* reports failure (Hermes carries status/error_type
    on post_tool_call; absent status defaults to ok so successes are never
    mislabeled).  *debug_fn* is an optional ``_debug(event, **fields)`` callback.
    """
    normalized = tool_name.lower()
    if normalized not in ("terminal", "bash"):
        return
    if status.lower() not in ("error", "failed", "failure"):
        return
    if not result or not result.strip():
        return
    lines = result.strip().splitlines()
    if len(lines) > MAX_FEEDBACK_LINES:
        lines = lines[-MAX_FEEDBACK_LINES:]
    truncated = "\n".join(lines)
    if len(truncated) > MAX_FEEDBACK_CHARS:
        truncated = truncated[-MAX_FEEDBACK_CHARS:]
    message = (
        "GROUNDED FEEDBACK: The last terminal command failed"
        f"{f' ({error_type})' if error_type else ''}. "
        "Use its output as evidence for your next correction:\n\n"
        f"```\n{truncated}\n```"
    )
    set_pending_feedback(project, message)
    if debug_fn:
        debug_fn("grounded_feedback", "stashed", project=project,
                 output_lines=len(lines))


# --- Commit reminder stash ---

PENDING_REMINDER_FILE = Path.home() / ".ai-badger" / "commit-reminder" / "pending.json"


def load_pending_reminders(path: Optional[Path] = None) -> Dict[str, str]:
    """Load the pending-reminder file; ``{}`` on missing file or bad JSON."""
    try:
        raw = (path or PENDING_REMINDER_FILE).read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def save_pending_reminders(pending: Dict[str, str],
                           path: Optional[Path] = None) -> None:
    """Persist the pending-reminder file."""
    target = path or PENDING_REMINDER_FILE
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(pending), encoding="utf-8")


def set_pending_reminder(project: str, message: str,
                         path: Optional[Path] = None) -> None:
    """Stash *message* for *project*."""
    pending = load_pending_reminders(path)
    pending[str(Path(project).resolve())] = message
    save_pending_reminders(pending, path)


def pop_pending_reminder(project: str,
                         path: Optional[Path] = None) -> Optional[str]:
    """Return and clear the pending reminder for *project*, or None.  Fail-open."""
    pending = load_pending_reminders(path)
    key = str(Path(project).resolve())
    message = pending.pop(key, None)
    if message is not None:
        try:
            save_pending_reminders(pending, path)
        except OSError:
            pass
    return message
