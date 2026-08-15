"""Shared memory-first gate module: tool matchers, session markers, per-host deny builders.

The gate blocks repo text-search tools (grep/find/search_files) until the session has
consulted AiRaccoon memory. Pure functions plus marker-file IO. A hook must never
raise, and Copilot's fail-closed preToolUse means exit 0 on every path.
"""
from __future__ import annotations

import os
import shlex
import subprocess
from pathlib import Path
from typing import Any, Dict, Optional

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


def is_memory_search(tool_name: Any) -> bool:
    """True for any naming spelling of the memory_search tool; never matches other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith(_MCP_PREFIX):
        name = name[len(_MCP_PREFIX):].split(_DOUBLE_UNDERSCORE, 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == "memory_search"

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
    """Session id made filesystem-safe; the empty string is not a valid marker."""
    if not session_id:
        return ""
    return session_id.replace("/", "_").replace("\\", "_")


def marker_path(session_id: Optional[str]) -> Path:
    """The consulted marker file for a session (empty path when no session id)."""
    safe = _safe_session(session_id)
    return MARKER_DIR / safe if safe else Path("")


def record_search(session_id: Optional[str]) -> bool:
    """Touch the consulted marker; False on a missing session id or IO failure."""
    path = marker_path(session_id)
    if not path.name:
        return False
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        return True
    except OSError:
        return False


def search_consulted(session_id: Optional[str]) -> bool:
    """True when the session already ran memory_search (marker exists)."""
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
    """Denials so far for the session; 0 on missing session id or any read error."""
    try:
        return int(_denials_path(session_id).read_text(encoding="utf-8").strip() or "0")
    except (OSError, ValueError):
        return 0


def increment_denials(session_id: Optional[str]) -> bool:
    """Bump the session's denial counter; False on missing session id or IO failure."""
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
