#!/usr/bin/env python3
"""PreToolUse hook: deny an unscoped process kill or shared-daemon reap while more than
one agent lane is plausibly live on this machine.

See docs/work/2026-08-07-cross-session-agent-coordination.md, incident 8: an agent told
to "kill any build you have running" ran `pkill -f "dotnet build"` — an unscoped pattern
match that killed every matching process on the machine, including another session's
in-flight compile. The generalisable rule from that note: never pattern-match a kill or
reap a shared daemon while another session may be using it. `pkill`, `killall`,
`dotnet build-server shutdown`, and any `kill` that targets a pattern rather than a
numeric PID are all the same hazard.

Denies only when more than one lane is plausibly live (one socket per live top-level
claude process under /tmp/cc-socks/*.sock, verified alive) — a single-session machine
gets no friction. Carries the same 3-strike escape valve as memory_first_gate_hook.py so
a genuinely-needed kill is still reachable; every path exits 0 (fail open).
"""
from __future__ import annotations

import json
import os
import re
import shlex
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

SOCK_DIR = Path("/tmp/cc-socks")
MARKER_DIR = Path.home() / ".ai-badger" / "blast-radius-guard"
MAX_DENIALS = 3

_SEGMENT_SPLIT = re.compile(r"&&|\|\||[;|\n]")
_SIGNAL_FLAG = re.compile(r"^-(?:\d+|[A-Za-z]+)$")
_PID = re.compile(r"^\d+$")

_PATTERNS = (
    (re.compile(r"\bpkill\b"), "pkill matches processes by name/pattern, never by PID"),
    (re.compile(r"\bkillall\b"), "killall matches processes by name, never by PID"),
    (re.compile(r"dotnet\s+build-server\s+shutdown\b"),
     "dotnet build-server shutdown reaps every worker on the shared MSBuild server"),
)


def _kill_hazard(segment: str) -> Optional[str]:
    """A reason string when *segment* runs `kill` against something other than bare PIDs."""
    try:
        tokens = shlex.split(segment)
    except ValueError:
        return None
    if "kill" not in tokens:
        return None
    idx = tokens.index("kill")
    args = tokens[idx + 1:]
    targets = [t for t in args if not _SIGNAL_FLAG.match(t)]
    if targets and all(_PID.match(t) for t in targets):
        return None  # every target is a bare numeric PID — scoped, allowed
    return "kill without an explicit numeric PID targets a pattern, not a process"


def find_hazard(command: str) -> Optional[str]:
    """The reason *command* is a blast-radius hazard, or None when it looks scoped."""
    for pattern, reason in _PATTERNS:
        if pattern.search(command):
            return reason
    for segment in _SEGMENT_SPLIT.split(command):
        reason = _kill_hazard(segment)
        if reason:
            return reason
    return None


def _live_lane_pids() -> List[int]:
    """PIDs of live top-level claude processes, from /tmp/cc-socks/*.sock (self-cleaning:
    one socket per live process, named by PID)."""
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
    if not session_id:
        return ""
    return session_id.replace("/", "_").replace("\\", "_")


def _denials_path(session_id: Optional[str]) -> Path:
    safe = _safe_session(session_id)
    return MARKER_DIR / (safe + ".denials") if safe else Path("")


def deny_count(session_id: Optional[str]) -> int:
    try:
        return int(_denials_path(session_id).read_text(encoding="utf-8").strip() or "0")
    except (OSError, ValueError):
        return 0


def increment_denials(session_id: Optional[str]) -> bool:
    path = _denials_path(session_id)
    if not path.name:
        return False
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(str(deny_count(session_id) + 1), encoding="utf-8")
        return True
    except OSError:
        return False


def _reason(hazard: str, live_pids: List[int]) -> str:
    lanes = ", ".join(str(p) for p in sorted(live_pids))
    return (
        f"Blast-radius guard: {hazard}. {len(live_pids)} agent lanes are plausibly live "
        f"right now (PIDs {lanes}) — this command could kill another session's process. "
        f"Scope the kill to PIDs you started (e.g. `kill <pid>` for a PID you launched, "
        f"not `pkill`/`killall`/a pattern). Re-issue this call if it is genuinely needed; "
        f"it is allowed through after {MAX_DENIALS} denials."
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
    if deny_count(session_id) >= MAX_DENIALS:
        return {}  # escape valve: a genuinely-needed kill still gets through
    increment_denials(session_id)

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
