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

import badger_store  # vendored beside this module in the plugin dir; engine/ canonical in tests

# --- Grounded feedback (Rule 3C) ---

#: Legacy source only: the store imports it on first write and renames it (P1.3). Tests and
#: surfaces redirect the legacy file by passing ``path``; AI_BADGER_USER_ROOT moves the DB.
PENDING_FEEDBACK_FILE = Path.home() / ".ai-badger" / "pending-feedback.json"
#: The stash document is one ``pending_feedback``-table row under this key (kvdoc shape).
PENDING_FEEDBACK_ROW_KEY = "pending"
MAX_FEEDBACK_LINES = 30
MAX_FEEDBACK_CHARS = 3000


def _feedback_store(path: Optional[Path] = None):
    """The user store narrowed to the feedback family; ``path`` rebinds the legacy seam."""
    families = {
        "pending_feedback": badger_store.Family(
            table="pending_feedback", db="user",
            legacy_path=lambda: path or PENDING_FEEDBACK_FILE, legacy_kind="kvdoc",
            row_key=PENDING_FEEDBACK_ROW_KEY,
        ),
    }
    return badger_store.open_user(families=families)


def _legacy_load_pending_feedback(path: Optional[Path] = None) -> Dict[str, str]:
    """The legacy pending-feedback.json document, ``{}`` on missing file or bad JSON."""
    try:
        raw = (path or PENDING_FEEDBACK_FILE).read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def load_pending_feedback(path: Optional[Path] = None) -> Dict[str, str]:
    """The stash document: the store's row merged with the legacy file; ``{}`` fail-open."""
    try:
        store = _feedback_store(path)
        try:
            row = store.kv_get("pending_feedback", PENDING_FEEDBACK_ROW_KEY, {})
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        return _legacy_load_pending_feedback(path)
    return row if isinstance(row, dict) else {}


def save_pending_feedback(pending: Dict[str, str],
                          path: Optional[Path] = None) -> None:
    """Write the whole stash document as its one row; the DB carries owner-only perms (D17)."""
    store = _feedback_store(path)
    try:
        store.kv_set("pending_feedback", PENDING_FEEDBACK_ROW_KEY, dict(pending))
    finally:
        store.close()


def set_pending_feedback(project: str, message: str,
                         path: Optional[Path] = None) -> None:
    """Stash grounded feedback for *project* (atomic read-modify-write)."""
    key = str(Path(project).resolve())

    def _stash(doc: Dict[str, str]) -> Dict[str, str]:
        merged = dict(doc) if isinstance(doc, dict) else {}
        merged[key] = message
        return merged

    store = _feedback_store(path)
    try:
        store.kv_update("pending_feedback", PENDING_FEEDBACK_ROW_KEY, _stash, {})
    finally:
        store.close()


def pop_pending_feedback(project: str,
                         path: Optional[Path] = None) -> Optional[str]:
    """Return and clear the pending grounded feedback for *project*, or None.

    The pop is one atomic read-modify-write (kv_update): concurrent surfacers serialize on
    the store's write lock, so a stashed message is delivered at most once. Fail-open: if
    the store fails, the entry stays stored and re-surfaces next turn, which is harmless —
    the prompt hook must never break over cleanup.
    """
    key = str(Path(project).resolve())
    popped: list = []

    def _pop(doc):
        message = doc.pop(key, None) if isinstance(doc, dict) else None
        if message is not None:
            popped.append(message)
        return doc if isinstance(doc, dict) else {}

    try:
        store = _feedback_store(path)
        try:
            store.kv_update("pending_feedback", PENDING_FEEDBACK_ROW_KEY, _pop, {})
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        pending = _legacy_load_pending_feedback(path)
        message = pending.pop(key, None)
        if message is not None:
            try:
                save_pending_feedback(pending, path)
            except OSError:
                pass
        return message
    return popped[0] if popped else None


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

#: Legacy source only: the store imports it on first write and renames it (P1.3). Tests and
#: surfaces redirect the legacy file by passing ``path``; AI_BADGER_USER_ROOT moves the DB.
PENDING_REMINDER_FILE = Path.home() / ".ai-badger" / "commit-reminder" / "pending.json"
#: The stash document is one ``commit_reminder``-table row under this key (kvdoc shape).
PENDING_REMINDER_ROW_KEY = "pending"


def _reminder_store(path: Optional[Path] = None):
    """The user store narrowed to the reminder family; ``path`` rebinds the legacy seam."""
    families = {
        "commit_reminder": badger_store.Family(
            table="commit_reminder", db="user",
            legacy_path=lambda: path or PENDING_REMINDER_FILE, legacy_kind="kvdoc",
            row_key=PENDING_REMINDER_ROW_KEY,
        ),
    }
    return badger_store.open_user(families=families)


def _legacy_load_pending_reminders(path: Optional[Path] = None) -> Dict[str, str]:
    """The legacy pending.json document, ``{}`` on missing file or bad JSON."""
    try:
        raw = (path or PENDING_REMINDER_FILE).read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _legacy_save_pending_reminders(pending: Dict[str, str],
                                   path: Optional[Path] = None) -> None:
    """Persist the legacy pending-reminder file."""
    target = path or PENDING_REMINDER_FILE
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(pending), encoding="utf-8")


def load_pending_reminders(path: Optional[Path] = None) -> Dict[str, str]:
    """The pending stash document: the store's row merged with the legacy file; ``{}`` fail-open."""
    try:
        store = _reminder_store(path)
        try:
            row = store.kv_get("commit_reminder", PENDING_REMINDER_ROW_KEY, {})
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        return _legacy_load_pending_reminders(path)
    return row if isinstance(row, dict) else {}


def save_pending_reminders(pending: Dict[str, str],
                           path: Optional[Path] = None) -> None:
    """Write the whole stash document as its one row; the first write migrates legacy (D6)."""
    store = _reminder_store(path)
    try:
        store.kv_set("commit_reminder", PENDING_REMINDER_ROW_KEY, dict(pending))
    finally:
        store.close()


def set_pending_reminder(project: str, message: str,
                         path: Optional[Path] = None) -> None:
    """Stash *message* for *project* in the stash document row (atomic read-modify-write)."""
    key = str(Path(project).resolve())

    def _stash(doc: Dict[str, str]) -> Dict[str, str]:
        merged = dict(doc) if isinstance(doc, dict) else {}
        merged[key] = message
        return merged

    store = _reminder_store(path)
    try:
        store.kv_update("commit_reminder", PENDING_REMINDER_ROW_KEY, _stash, {})
    finally:
        store.close()


def pop_pending_reminder(project: str,
                         path: Optional[Path] = None) -> Optional[str]:
    """Return and clear the pending reminder for *project*, or None.

    The pop is one atomic read-modify-write (kv_update): concurrent surfacers serialize on
    the store's write lock, so a stashed message is delivered at most once. Fail-open: if
    the store fails, the entry stays stored and re-surfaces next turn, which is harmless —
    the prompt hook must never break over cleanup.
    """
    key = str(Path(project).resolve())
    popped: list = []

    def _pop(doc):
        message = doc.pop(key, None) if isinstance(doc, dict) else None
        if message is not None:
            popped.append(message)
        return doc if isinstance(doc, dict) else {}

    try:
        store = _reminder_store(path)
        try:
            store.kv_update("commit_reminder", PENDING_REMINDER_ROW_KEY, _pop, {})
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        pending = _legacy_load_pending_reminders(path)
        message = pending.pop(key, None)
        if message is not None:
            try:
                _legacy_save_pending_reminders(pending, path)
            except OSError:
                pass
        return message
    return popped[0] if popped else None
