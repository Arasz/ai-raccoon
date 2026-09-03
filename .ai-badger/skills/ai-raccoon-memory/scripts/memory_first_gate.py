"""Shared memory-first gate module: tool matchers, session presence rows, deny builders.

The gate blocks repo text-search tools (grep/find/search_files) until the session has
consulted AiRaccoon memory. Presence lives as memory_first rows in the user store (P2.1a):
consulted in the payload, the denial count in the denials column, keyed by session id. The
legacy marker files under MARKER_DIR are the lazy-migrated source (first write imports and
renames them) and the fail-open fallback when the store is unavailable. A hook must never
raise, and Copilot's fail-closed preToolUse means exit 0 on every path.
"""
from __future__ import annotations

import json
import os
import shlex
import subprocess
from pathlib import Path
from typing import Any, Dict, Optional

try:
    import badger_store  # vendored beside this script in production; engine/ canonical in tests
except ImportError:  # a deployment without the store keeps the legacy marker-file surface
    badger_store = None  # pylint: disable=invalid-name

MARKER_DIR = Path.home() / ".ai-badger" / "memory-first"
PROJECT_ID_ENV = "AI_RACCOON_PROJECT_ID"

# Keeps a hung/slow git from blocking the tool call the gate is meant to police.
_GIT_TIMEOUT_SECONDS = 2

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


# Bash/terminal commands whose first token is one of these are text search.
_SEARCH_COMMANDS = ("grep", "rg", "find", "rg.exe")

# Tool names that are always text search, by any host's spelling (case-insensitive).
_SEARCH_TOOLS = ("search_files", "grep", "rg", "glob")

MAX_DENIALS = 3

_MCP_PREFIX = "mcp__"
_DOUBLE_UNDERSCORE = "__"


_PI_MCP_PREFIX = "mcp_"


def _pi_mcp_spelling_matches(tool_name: str, target: str) -> bool:
    """True when `target` is a trailing underscore-joined segment of pi's MCP spelling.

    pi names MCP tools `mcp_<server>_<tool>` (single underscores) and a server name may
    itself contain underscores, so the tool part is not a fixed suffix — it is matched as
    any trailing `_`-joined tail of the spelling. Claude's `mcp__server__tool` spelling is
    handled by the branch above; this is the pi shape (verified live: the payload the pi
    bridge carries names the tool `mcp_ai-raccoon_memory_search`).
    """
    if not tool_name.startswith(_PI_MCP_PREFIX) or tool_name.startswith(_MCP_PREFIX):
        return False
    parts = tool_name[len(_PI_MCP_PREFIX):].split("_")
    return any("_".join(parts[i:]) == target for i in range(1, len(parts)))


def is_memory_search(tool_name: Any) -> bool:
    """True for any naming spelling of the memory_search tool; never matches other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith(_MCP_PREFIX):
        name = name[len(_MCP_PREFIX):].split(_DOUBLE_UNDERSCORE, 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == "memory_search" or _pi_mcp_spelling_matches(tool_name, "memory_search")

_REASON = (
    "Memory-first gate: run memory_search (projectId={project_id}) before repo text "
    "search; re-issue this call if the bank has no relevant hit."
)


def is_text_search(tool_name: Any, tool_input: Any) -> bool:
    """True when the tool call is repo text search that the gate should block.

    Bash/terminal only count when the command's first token is a search binary, so
    build steps with a piped grep (`git status | grep x`) pass.
    """
    if not isinstance(tool_name, str):
        return False
    name = tool_name.lower()
    if name in _SEARCH_TOOLS:
        return True
    if name not in ("bash", "terminal"):
        return False
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str):
        return False
    try:
        tokens = shlex.split(command)
    except ValueError:  # unterminated quotes — not a shape we can judge
        return False
    return bool(tokens) and tokens[0].lower() in _SEARCH_COMMANDS


def _safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe; the empty string is not a valid marker.

    Keeps [A-Za-z0-9._-]; substitutes '_' for everything else.
    Guards dot-only traversal segments: '.' -> '_' and '..' -> '__'.
    """
    if not session_id:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(session_id)
    )
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def marker_path(session_id: Optional[str]) -> Path:
    """The consulted marker file for a session (empty path when no session id)."""
    safe = _safe_session(session_id)
    return MARKER_DIR / safe if safe else Path("")


def _open_store():
    """The user store narrowed to the memory_first family; MARKER_DIR is its legacy seam."""
    return badger_store.open_user(families={
        "memory_first": badger_store.Family(
            table="memory_first", db="user",
            legacy_path=lambda: MARKER_DIR, legacy_kind="markers",
        ),
    })


def record_search(session_id: Optional[str]) -> bool:
    """Record the session's memory_search as a consulted row; False on failure.

    The first write lazy-migrates the legacy marker set (D6). An unavailable store falls
    back to touching the legacy marker file, so the gate keeps working either way.
    """
    safe = _safe_session(session_id)
    if not safe:
        return False
    if badger_store is not None:
        try:
            store = _open_store()
            try:
                store.migrate("memory_first")
                store.conn.execute(
                    "INSERT INTO memory_first(session_id, payload, denials, updated_at) "
                    "VALUES (?, ?, 0, ?) ON CONFLICT(session_id) DO UPDATE SET "
                    "payload = excluded.payload, updated_at = excluded.updated_at",
                    # the shared row-stamp format comes from the store's own helper
                    # pylint: disable-next=protected-access
                    (safe, json.dumps({"consulted": True}), badger_store._now()),
                )
                store.conn.commit()
                return True
            finally:
                store.close()
        # a gate never raises
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    path = marker_path(session_id)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        return True
    except OSError:
        return False


def search_consulted(session_id: Optional[str]) -> bool:
    """True when the session already ran memory_search (row consulted, else legacy marker)."""
    safe = _safe_session(session_id)
    if not safe:
        return False
    if badger_store is not None:
        try:
            store = _open_store()
            try:
                row = store.conn.execute(
                    "SELECT payload FROM memory_first WHERE session_id = ?", (safe,)
                ).fetchone()
            finally:
                store.close()
            if row is not None:
                try:
                    return bool(json.loads(row[0]).get("consulted"))
                except ValueError:
                    return False
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    return marker_path(session_id).is_file()


def _main_checkout_basename(cwd: str) -> Optional[str]:
    """The main checkout's basename for `cwd`, collapsing a linked worktree to it.

    `git rev-parse --show-toplevel` returns the *worktree's own* path from inside a linked
    worktree; `--git-common-dir` is the one that points at the shared `.git`, whose parent is
    the main checkout. None on any failure (not a repo, no git, timeout) or empty `cwd`.
    """
    if not cwd:
        return None
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=cwd, capture_output=True, text=True, timeout=_GIT_TIMEOUT_SECONDS, check=False,
            env=git_env(),
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode != 0:
        return None
    common_dir = result.stdout.strip()
    return Path(common_dir).parent.name if common_dir else None


def project_id(cwd: str = "") -> str:
    """The bank's project id for a working directory: the main checkout's basename (a linked
    worktree collapses to it), else the cwd directory's own basename, else `unknown`."""
    override = os.environ.get(PROJECT_ID_ENV, "").strip()
    if override:
        return override
    return _main_checkout_basename(cwd) or Path(cwd or "").name or "unknown"


def _denials_path(session_id: Optional[str]) -> Path:
    """The per-session denial counter file; empty path when no session id."""
    safe = _safe_session(session_id)
    return MARKER_DIR / (safe + ".denials") if safe else Path("")


def deny_count(session_id: Optional[str]) -> int:
    """Denials so far for the session (the row's denials column); 0 on failure."""
    safe = _safe_session(session_id)
    if not safe:
        return 0
    if badger_store is not None:
        try:
            store = _open_store()
            try:
                row = store.conn.execute(
                    "SELECT denials FROM memory_first WHERE session_id = ?", (safe,)
                ).fetchone()
            finally:
                store.close()
            if row is not None:
                return int(row[0])
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    try:
        return int(_denials_path(session_id).read_text(encoding="utf-8").strip() or "0")
    except (OSError, ValueError):
        return 0


def increment_denials(session_id: Optional[str]) -> bool:
    """Bump the session's denial counter (the row's denials column); False on failure.

    The upsert must not clobber a consulted row's payload: a session can be denied before
    it runs memory_search, and the consulted fact survives the denials that preceded it.
    """
    safe = _safe_session(session_id)
    if not safe:
        return False
    if badger_store is not None:
        try:
            store = _open_store()
            try:
                store.migrate("memory_first")
                store.conn.execute(
                    "INSERT INTO memory_first(session_id, payload, denials, updated_at) "
                    "VALUES (?, ?, 1, ?) ON CONFLICT(session_id) DO UPDATE SET "
                    "denials = denials + 1, updated_at = excluded.updated_at",
                    # the shared row-stamp format comes from the store's own helper
                    # pylint: disable-next=protected-access
                    (safe, json.dumps({"consulted": False}), badger_store._now()),
                )
                store.conn.commit()
                return True
            finally:
                store.close()
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    path = _denials_path(session_id)
    if not path.name or path.name == ".denials":
        return False
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(str(deny_count(session_id) + 1), encoding="utf-8")
        return True
    except OSError:
        return False


def build_decision(host: str, tool_name: str, tool_input: Any,
                   session_id: Optional[str], cwd: str = "") -> Dict[str, Any]:
    """The per-host deny payload, or {} for an unknown host (fail open)."""
    reason = _REASON.format(project_id=project_id(cwd))
    if host == "hermes":
        return {"action": "block", "message": reason}
    if host == "claude":
        return {"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }}
    if host == "copilot":
        return {"permissionDecision": "deny", "permissionDecisionReason": reason}
    return {}
