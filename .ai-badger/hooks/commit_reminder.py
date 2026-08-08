"""Pure logic for the commit-reminder skill: parse `git status --porcelain`, recognize an
edit-shaped tool call, and debounce the nudge with a per-project marker persisted outside
any scaffolded repo (a state file inside the measured repo would inflate its own count).
"""
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Dict, List, Tuple

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


def uncommitted_files(root: str, timeout: float = 5.0) -> List[str]:
    """Run `git status --porcelain` in ``root``; `[]` on any failure, never raises."""
    try:
        result = subprocess.run(
            ["git", "-C", root, "status", "--porcelain"],
            capture_output=True, text=True, timeout=timeout, check=False, env=git_env(),
        )
    except (subprocess.TimeoutExpired, OSError):
        return []
    if result.returncode != 0:
        return []
    return parse_porcelain(result.stdout)


def load_state() -> Dict[str, int]:
    """Load the marker state file; `{}` on missing file, read error, or malformed JSON."""
    try:
        raw = STATE_FILE.read_text(encoding="utf-8")
    except (OSError, ValueError):  # ValueError: a non-UTF-8 file raises UnicodeDecodeError
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def save_state(state: Dict[str, int]) -> None:
    """Persist the state file atomically, creating parent directories as needed.

    Parallel agents in separate worktrees share one file. The write is a rename so a reader
    never sees a torn file: `load_state` degrades an unparseable read to `{}`, and the next
    writer would then persist that — silently cancelling every other project's escalation.
    """
    STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = STATE_FILE.with_name(f"{STATE_FILE.name}.{os.getpid()}.tmp")
    tmp.write_text(json.dumps(state), encoding="utf-8")
    os.replace(tmp, STATE_FILE)


def get_entry(root: str) -> Dict:
    """Return the persisted entry for ``root``, normalising the old marker-only form.

    Every machine that ran the previous hook has `{root: <int>}` on disk, so a bare integer
    reads as a marker with no unanswered commands rather than as corrupt state.
    """
    value = load_state().get(str(Path(root).resolve()), 0)
    if isinstance(value, int):
        return {"marker": value, "fires": 0}
    if not isinstance(value, dict):
        return {"marker": 0, "fires": 0}
    entry = dict(value)
    entry.setdefault("marker", 0)
    entry.setdefault("fires", 0)
    return entry


def set_entry(root: str, entry: Dict) -> None:
    """Persist ``entry`` for ``root``, keyed by its resolved absolute path."""
    state = load_state()
    state[str(Path(root).resolve())] = entry
    save_state(state)


def at_risk_entries() -> Dict[str, Dict]:
    """Every project whose unanswered-command count has reached the escalation bar.

    The read side of the hook's state: what `ensure-work-is-committed` reports to a parent.
    """
    found = {}
    for root, value in load_state().items():
        fires = value.get("fires") if isinstance(value, dict) else None
        if isinstance(fires, int) and not isinstance(fires, bool) and fires >= ESCALATE_AFTER:
            found[root] = value
    return found


def get_marker(root: str) -> int:
    """Return the persisted marker for ``root``, or 0 if never seen."""
    return get_entry(root)["marker"]


def set_marker(root: str, marker: int) -> None:
    """Persist ``marker`` for ``root``, keyed by its resolved absolute path."""
    key = str(Path(root).resolve())
    state = load_state()
    state[key] = marker
    save_state(state)
