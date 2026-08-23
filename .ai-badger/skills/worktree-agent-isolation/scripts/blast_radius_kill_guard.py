#!/usr/bin/env python3
"""PreToolUse hook: deny an unscoped process kill or shared-cache reap while more than one
agent lane is live; re-issuing the same command 3 times opens an escape valve for it alone.
Verified by verify_hooks.py in this directory.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shlex
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, NamedTuple, Optional, Tuple

SOCK_DIR = Path("/tmp/cc-socks")
MAX_DENIALS = 3
MARKER_TTL_SECONDS = 86400


class StackHazards(NamedTuple):
    """One ecosystem's shared resources: `commands` is (program, words all of which must appear
    in its arguments); `caches` is path fragments whose recursive delete every lane feels."""
    commands: Tuple[Tuple[str, Tuple[str, ...]], ...]
    caches: Tuple[str, ...]


# A deliberate allowlist of stack-specific hazards; completeness is an explicit non-goal — an
# unlisted command is allowed, so add to it as incidents teach us new ones. A new stack is a new
# row here and nothing else: the lexer below never learns a program name.
STACK_HAZARDS: Dict[str, StackHazards] = {
    "dotnet": StackHazards(
        commands=(("dotnet", ("build-server", "shutdown")),),
        caches=(r"\.nuget", r"\.dotnet", r"\.local/share/nuget", r"msbuildcache"),
    ),
    "node": StackHazards(
        commands=(("npm", ("cache", "clean")), ("yarn", ("cache", "clean")),
                  ("pnpm", ("store", "prune"))),
        caches=(r"\.npm", r"\.cache/yarn", r"\.yarn/berry", r"\.pnpm-store"),
    ),
    "python": StackHazards(
        commands=(("pip", ("cache", "purge")), ("uv", ("cache", "clean"))),
        caches=(r"\.cache/pip", r"\.cache/uv", r"\.local/share/virtualenvs"),
    ),
    "rust": StackHazards(
        commands=(("sccache", ("--stop-server",)),),
        caches=(r"\.cargo/registry", r"\.cargo/git", r"\.rustup"),
    ),
}

_CACHE_PATTERNS: Dict[str, "re.Pattern[str]"] = {
    stack: re.compile(r"(^|/)(" + "|".join(hazards.caches) + r")(/|$)")
    for stack, hazards in STACK_HAZARDS.items()
}


def _marker_dir() -> Optional[Path]:
    """The denial-counter directory, or None when this process has no home directory."""
    try:
        return Path.home() / ".ai-badger" / "blast-radius-guard"
    except (RuntimeError, OSError):
        return None


MARKER_DIR: Optional[Path] = _marker_dir()

_SHELLS = frozenset(("sh", "bash", "zsh", "dash", "ksh"))
_SKIPPABLE = frozenset(("sudo", "command", "env", "nohup", "exec", "time"))
_OPERATORS = frozenset((";", "|", "||", "&", "&&", "(", ")", "<", ">", ">>"))
_ENV_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_KILLALL = re.compile(r"^killall\d*$")
_SIGNAL_FLAG = re.compile(r"^-(?:\d+|[A-Za-z]+)$")
_PID = re.compile(r"^\d+$")
_SIGNAL_OPTION = frozenset(("-s", "--signal", "-n"))
# The lexer is superlinear; past this a payload-sized command would outrun the hook timeout.
_MAX_COMMAND = 100_000
_RECURSIVE_RM = re.compile(r"^-(?!-)[a-zA-Z]*[rR]|^--recursive$")


def _dash_c_index(args: List[str]) -> Optional[int]:
    """Index of the shell's command-string flag, spelled `-c` or combined as `-lc`/`-ec`."""
    for index, token in enumerate(args):
        if token.startswith("-") and not token.startswith("--") and "c" in token[1:]:
            return index
    return None


def _tokenize(text: str) -> List[str]:
    """Shell tokens with operators kept separate; [] when the text does not lex."""
    lexer = shlex.shlex(text, posix=True, punctuation_chars=True)
    lexer.whitespace_split = True
    try:
        return list(lexer)
    except ValueError:
        return []


def _segments(command: str) -> List[List[str]]:
    """The command split into pipeline/list segments, each a token list."""
    out: List[List[str]] = []
    for line in command.splitlines():
        current: List[str] = []
        for token in _tokenize(line):
            if token in _OPERATORS:
                if current:
                    out.append(current)
                current = []
                continue
            current.append(token)
        if current:
            out.append(current)
    return out


def _split_command(tokens: List[str]) -> Optional[Tuple[str, List[str]]]:
    """(program, args) for a segment, past env assignments, `sudo`-likes and any path prefix."""
    for index, token in enumerate(tokens):
        if _ENV_ASSIGN.match(token) or token in _SKIPPABLE:
            continue
        return token.rsplit("/", 1)[-1], tokens[index + 1:]
    return None


def _kill_hazard(args: List[str]) -> Optional[str]:
    """A reason when `kill` targets anything other than PIDs or job specs, else None."""
    targets: List[str] = []
    skip_next = False
    for arg in args:
        if skip_next:
            skip_next = False
            continue
        if arg in _SIGNAL_OPTION:
            skip_next = True
            continue
        if arg == "--" or _SIGNAL_FLAG.match(arg):
            continue
        targets.append(arg)
    if targets and all(_PID.match(t) or t.startswith("%") for t in targets):
        return None
    return "kill without an explicit numeric PID or job spec targets a pattern, not a process"


def _reap_hazard(program: str, args: List[str]) -> Optional[str]:
    """A reason when the segment reaps a shared daemon or cache from STACK_HAZARDS, else None."""
    for stack, hazards in STACK_HAZARDS.items():
        for name, words in hazards.commands:
            if program == name and all(word in args for word in words):
                return (f"`{name} {' '.join(words)}` reaps a {stack} service or cache every "
                        f"lane on this machine shares")
    if program == "launchctl" and "kickstart" in args and any(
            a.startswith("-") and "k" in a for a in args):
        return "launchctl kickstart -k restarts a system-wide service other sessions may be using"
    if program == "rm" and any(_RECURSIVE_RM.match(a) for a in args):
        for arg in args:
            expanded = os.path.expanduser(arg).lower()
            for stack, pattern in _CACHE_PATTERNS.items():
                if pattern.search(expanded):
                    return (f"a recursive delete of the shared {stack} cache breaks every "
                            f"other lane")
    return None


def _segment_hazard(tokens: List[str], depth: int) -> Optional[str]:
    """The reason this segment is a blast-radius hazard, or None — see STACK_HAZARDS on why an
    unlisted command is allowed through."""
    split = _split_command(tokens)
    if split is None:
        return None
    program, args = split
    if program == "pkill":
        return "pkill matches processes by name/pattern, never by PID"
    if _KILLALL.match(program):
        return "killall matches processes by name, never by PID"
    if program == "kill":
        return _kill_hazard(args)
    if program in _SHELLS and depth > 0:
        index = _dash_c_index(args)
        if index is not None and index + 1 < len(args):
            return find_hazard(args[index + 1], depth - 1)
    return _reap_hazard(program, args)


def find_hazard(command: str, depth: int = 3) -> Optional[str]:
    """The reason *command* is a blast-radius hazard, or None when it looks scoped."""
    if len(command) > _MAX_COMMAND:
        return None
    for tokens in _segments(command):
        reason = _segment_hazard(tokens, depth)
        if reason:
            return reason
    return None


def _live_lane_pids() -> List[int]:
    """PIDs of live top-level agent processes, one socket per process under SOCK_DIR."""
    try:
        sockets = list(SOCK_DIR.glob("*.sock"))
    except OSError:
        return []
    pids: List[int] = []
    for sock in sockets:
        try:
            pid = int(sock.stem)
        except ValueError:
            continue
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            continue  # socket present but process gone — not live
        except PermissionError:
            pids.append(pid)  # alive, just not owned by us
        except OSError:
            continue
        else:
            pids.append(pid)
    return pids


def _safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe; empty string is not a valid marker.

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


def _denials_path(session_id: Optional[str], command: str) -> Optional[Path]:
    """Counter file for this (session, exact command) pair, or None when unaddressable."""
    if MARKER_DIR is None or not session_id:
        return None
    safe = _safe_session(session_id)
    if not safe:
        return None
    digest = hashlib.sha256(command.encode("utf-8")).hexdigest()[:32]
    return MARKER_DIR / f"{safe}.{digest}.denials"


def deny_count(session_id: Optional[str], command: str) -> int:
    path = _denials_path(session_id, command)
    if path is None:
        return 0
    try:
        return int(path.read_text(encoding="utf-8").strip() or "0")
    except (OSError, ValueError):
        return 0


def _prune(directory: Path) -> None:
    """Drop counter files older than MARKER_TTL_SECONDS; best effort."""
    cutoff = time.time() - MARKER_TTL_SECONDS
    try:
        stale = list(directory.glob("*.denials"))
    except OSError:
        return
    for marker in stale:
        try:
            if marker.stat().st_mtime < cutoff:
                marker.unlink()
        except OSError:
            continue


def increment_denials(session_id: Optional[str], command: str) -> bool:
    path = _denials_path(session_id, command)
    if path is None:
        return False
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        _prune(path.parent)
        path.write_text(str(deny_count(session_id, command) + 1), encoding="utf-8")
        return True
    except OSError:
        return False


def _reason(hazard: str, live_pids: List[int]) -> str:
    lanes = ", ".join(str(p) for p in sorted(live_pids))
    return (
        f"Blast-radius guard: {hazard}. {len(live_pids)} agent lanes are plausibly live "
        f"right now (PIDs {lanes}) — this command could kill another session's process. "
        f"Scope the kill to PIDs you started (e.g. `kill <pid>` for a PID you launched, "
        f"not `pkill`/`killall`/a pattern). Re-issue this exact command if it is genuinely "
        f"needed; that same command is allowed through after {MAX_DENIALS} denials."
    )


def decide(payload: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(payload, dict):
        return {}
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    if str(tool_name).lower() not in ("bash", "terminal"):
        return {}
    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str) or not command.strip():
        return {}

    hazard = find_hazard(command)
    if hazard is None:
        return {}

    live_pids = _live_lane_pids()
    if len(live_pids) < 2:
        return {}  # exactly one (or zero, undetermined) lane live — nothing to protect

    session_id = payload.get("session_id") or payload.get("sessionId")
    if deny_count(session_id, command) >= MAX_DENIALS:
        return {}  # escape valve: this exact command, re-issued, still gets through
    increment_denials(session_id, command)

    return {"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": _reason(hazard, live_pids),
    }}


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    try:
        decision = decide(payload)
    except Exception:  # pylint: disable=broad-exception-caught
        return 0
    if decision:
        print(json.dumps(decision))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
