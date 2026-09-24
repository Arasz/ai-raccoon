"""Pure logic for the commit-reminder skill: parse `git status --porcelain`, recognize an
edit-shaped tool call, and debounce the nudge with a per-project marker persisted outside
any scaffolded repo (state inside the measured repo would inflate its own count).

State lives as ``commit_reminder`` rows in the user store (~/.ai-badger/ai-badger.db), one
row per project (P1.3). STATE_FILE is the legacy source the store lazy-migrates on first
write: imported to rows, renamed *.migrated.json.
"""
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any, Dict, List, Tuple

import badger_store  # vendored beside this script in production; engine/ canonical in tests

#: Legacy source only: the store imports it on first write and renames it. Tests and surfaces
#: redirect the legacy file by rebinding this constant; AI_BADGER_USER_ROOT moves the rows' DB.
STATE_FILE = Path.home() / ".ai-badger" / "commit-reminder" / "state.json"

# Unanswered commands before the work is reported as at risk of being lost.
ESCALATE_AFTER = 3

CONVENTION_URL = "https://www.conventionalcommits.org/en/v1.0.0/"
COMMIT_FORM = "<type>[optional scope]: <description>"

_EDIT_TOOL_NAMES = frozenset({"Write", "Edit", "MultiEdit", "NotebookEdit"})
_HERMES_EDIT_SUBSTRINGS = ("write", "edit", "patch", "replace")


def _strip_quotes(path: str) -> str:
    if len(path) >= 2 and path[0] == '"' and path[-1] == '"':
        return path[1:-1]
    return path


def parse_porcelain(text: str) -> List[str]:
    """Parse `git status --porcelain` output into a list of file paths."""
    if not text or not text.strip():
        return []
    files: List[str] = []
    for line in text.splitlines():
        if not line.strip():
            continue
        path = line[3:] if len(line) > 3 else ""
        if " -> " in path:
            path = path.rsplit(" -> ", 1)[1]
        files.append(_strip_quotes(path))
    return files


def is_edit_tool(tool_name) -> bool:
    """True for Claude/Copilot's edit tool names, or a permissive Hermes lowercase match."""
    if tool_name is None:
        return False
    if tool_name in _EDIT_TOOL_NAMES:
        return True
    if not isinstance(tool_name, str) or tool_name != tool_name.lower():
        return False
    return any(sub in tool_name for sub in _HERMES_EDIT_SUBSTRINGS)


def should_remind(count: int, marker: int, threshold: int = 5) -> Tuple[bool, int]:
    """Debounce ratchet: fire once per threshold-crossing, re-arm as soon as count drops.

    If ``count`` falls below ``marker`` (a commit happened), ``marker`` ratchets down to
    ``count`` immediately so a later re-crossing of ``threshold`` fires again — it never
    stays stuck true forever the way a set-only flag would.
    """
    marker = min(marker, count)
    fires = count >= threshold and count > marker
    if fires:
        marker = count
    return fires, marker


def advance(entry: Dict, count: int, threshold: int = 5, escalate_after: int = ESCALATE_AFTER,
            now: str = "", session: str = "") -> Tuple[bool, bool, Dict]:
    """Run the ratchet and the stuck-agent counter together.

    Returns ``(fires, at_risk, entry)``. ``fires`` counts commands that went unanswered: a
    dropped file count is the only evidence of a commit this hook has, so that is what clears
    it. ``at_risk`` is the signal `ensure-work-is-committed` reports to a parent.
    """
    marker = entry.get("marker", 0)
    unanswered = entry.get("fires", 0)
    if count < marker:
        unanswered = 0

    fires, new_marker = should_remind(count, marker, threshold)
    if fires:
        unanswered += 1

    updated = dict(entry)
    updated.update({"marker": new_marker, "fires": unanswered})
    if fires:
        # The *first* unanswered command, not the latest: a parent reads this as how long the
        # work has been at risk, and refreshing it would understate the very risk it reports.
        if unanswered == 1:
            updated["since"] = now
        updated["session"] = session or entry.get("session", "")
    return fires, fires and unanswered >= escalate_after, updated


def build_message(count: int, reason: str, fires: int, at_risk: bool = False) -> str:
    """The text the hook emits. Imperative: a suggestion is what this replaces."""
    convention = (f"Use Conventional Commits — {COMMIT_FORM} — see {CONVENTION_URL}")
    if at_risk:
        return (f"[ai-badger] STOP AND COMMIT — {count} uncommitted file(s) after {fires} "
                f"commands with no commit in between. This work is at risk of being lost: "
                f"an agent that ends here leaves it unrecoverable. Commit what works now, "
                f"even as a WIP. {convention}. {reason}").strip()
    return (f"[ai-badger] Commit now — {count} uncommitted file(s). "
            f"{convention}. {reason}").strip()


# badger_lib.GIT_LOCATION_ENV, repeated because this ships into projects that have no framework
# checkout to import it from. git exports GIT_DIR to its hooks and GIT_COMMON_DIR answers
# `--git-common-dir` outright, so a child that inherits either reports another repository's
# layout. tests/test_git_invocation.py pins every copy against the original.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env=None) -> dict:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


class _GitUnknown:  # pylint: disable=too-few-public-methods
    """Sentinel: `git status` could not be determined (it failed, timed out, or is missing)."""

    def __repr__(self) -> str:
        return "GIT_UNKNOWN"


#: A project whose git status is genuinely unknown must never look identical to a clean one
#: (L9-3) — `git_status` returns this instead of `[]` so a caller can tell the two apart.
GIT_UNKNOWN = _GitUnknown()


def git_status(root: str, timeout: float = 5.0):
    """`git status --porcelain` in ``root``: the file list, or `GIT_UNKNOWN` on failure/timeout."""
    try:
        result = subprocess.run(
            ["git", "-C", root, "status", "--porcelain"],
            capture_output=True, text=True, timeout=timeout, check=False, env=git_env(),
        )
    except (subprocess.TimeoutExpired, OSError):
        return GIT_UNKNOWN
    if result.returncode != 0:
        return GIT_UNKNOWN
    return parse_porcelain(result.stdout)


def uncommitted_files(root: str, timeout: float = 5.0) -> List[str]:
    """Run `git status --porcelain` in ``root``; `[]` on any failure, never raises.

    The fail-open view atop `git_status` for a caller (the hook) that only wants a live count
    and treats "could not tell" the same as "found nothing" — `ensure_committed`'s report is
    the one caller that must not, and calls `git_status` directly instead.
    """
    status = git_status(root, timeout)
    return [] if status is GIT_UNKNOWN else status


def open_store():
    """The user store narrowed to the commit-reminder family; STATE_FILE is its legacy seam."""
    families = {
        "commit_reminder": badger_store.Family(
            table="commit_reminder", db="user",
            legacy_path=lambda: STATE_FILE, legacy_kind="map",
        ),
    }
    return badger_store.open_user(families=families)


def _legacy_state() -> Dict[str, Any]:
    """The legacy state.json document, ``{}`` on missing file, read error, or bad JSON."""
    try:
        raw = STATE_FILE.read_text(encoding="utf-8")
    except (OSError, ValueError):  # ValueError: a non-UTF-8 file raises UnicodeDecodeError
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _load_from_store() -> Dict[str, Any]:
    store = open_store()
    try:
        return store.kv_all("commit_reminder")
    finally:
        store.close()


def load_state(strict: bool = False) -> Dict[str, Any]:
    """Per-project entries: store rows merged with the legacy file (D5a); ``{}`` fail-open.

    A store that cannot be opened fails open to the legacy file — the same contract as the
    missing-file case; the legacy file is only renamed by a write, never a read.

    ``strict=True`` propagates instead: only `ensure_committed`'s report reads state this way,
    so "nothing is at risk" is never confused with "the state could not be read" (L9-3, R26).
    Every other reader — this function's own default, `get_entry`, the Hermes hook — keeps the
    fail-open contract; a report is worth failing loudly over, a live nudge is not.
    """
    if strict:
        return _load_from_store()
    try:
        return _load_from_store()
    except Exception:  # pylint: disable=broad-exception-caught
        return _legacy_state()


def save_state(state: Dict[str, Any]) -> None:
    """Upsert every project's entry as its own row; the first write migrates legacy (D6).

    Keys absent from ``state`` keep their rows — callers evolve one project's entry through
    set_entry; nothing deletes another project's row by saving a narrower document.
    """
    store = open_store()
    try:
        for key, value in state.items():
            store.kv_set("commit_reminder", key, value)
    finally:
        store.close()


def _normalize_entry(value: Any) -> Dict:
    """A raw stored value as a normalised entry.

    Every machine that ran the pre-store hook has ``{root: <int>}`` on disk (now in its
    migrated row), so a bare integer reads as a marker with no unanswered commands rather
    than as corrupt state.
    """
    if isinstance(value, int):
        return {"marker": value, "fires": 0}
    if not isinstance(value, dict):
        return {"marker": 0, "fires": 0}
    entry = dict(value)
    entry.setdefault("marker", 0)
    entry.setdefault("fires", 0)
    return entry


def get_entry(root: str) -> Dict:
    """Return the persisted entry for ``root``, normalising the old marker-only form."""
    return _normalize_entry(load_state().get(str(Path(root).resolve()), 0))


def set_entry(root: str, entry: Dict) -> None:
    """Persist ``entry`` as ``root``'s own row, keyed by its resolved absolute path."""
    store = open_store()
    try:
        store.kv_set("commit_reminder", str(Path(root).resolve()), entry)
    finally:
        store.close()


def update_entry(root: str, count: int, threshold: int = 5,
                  escalate_after: int = ESCALATE_AFTER, now: str = "",
                  session: str = "") -> Tuple[bool, bool, Dict]:
    """Atomically advance ``root``'s entry through the store's own `kv_update` (L4-8, R5).

    `get_entry` then `advance` then `set_entry`, as the hook used to run it, is three separate
    store round trips: a second invocation between the read and the write computes from the
    same stale entry, so one of the two increments is lost. `kv_update` runs the read, the
    advance, and the write inside one transaction, so a concurrent call blocks on the write
    lock and sees this call's result before it computes its own. `commit_reminder_hook` is the
    only caller; other readers (`get_entry`/`set_entry`, the report, the Hermes hook) are
    unaffected.
    """
    outcome: Dict[str, Any] = {}

    def _advance(current: Any) -> Dict:
        fires, at_risk, updated = advance(
            _normalize_entry(current), count, threshold, escalate_after, now, session)
        outcome["fires"], outcome["at_risk"] = fires, at_risk
        return updated

    store = open_store()
    try:
        entry = store.kv_update("commit_reminder", str(Path(root).resolve()), _advance, 0)
    finally:
        store.close()
    return outcome["fires"], outcome["at_risk"], entry


def at_risk_entries(strict: bool = False) -> Dict[str, Dict]:
    """Every project whose unanswered-command count has reached the escalation bar.

    The read side of the hook's state: what `ensure-work-is-committed` reports to a parent.
    ``strict=True`` propagates a broken store instead of reading it as empty (L9-3).
    """
    found = {}
    for root, value in load_state(strict=strict).items():
        fires = value.get("fires") if isinstance(value, dict) else None
        if isinstance(fires, int) and not isinstance(fires, bool) and fires >= ESCALATE_AFTER:
            found[root] = value
    return found


def get_marker(root: str) -> int:
    """Return the persisted marker for ``root``, or 0 if never seen."""
    return get_entry(root)["marker"]


def set_marker(root: str, marker: int) -> None:
    """Persist ``marker`` as ``root``'s own row, keyed by its resolved absolute path."""
    store = open_store()
    try:
        store.kv_set("commit_reminder", str(Path(root).resolve()), int(marker))
    finally:
        store.close()
