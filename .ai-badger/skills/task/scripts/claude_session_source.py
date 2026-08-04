"""Claude session source: the transcript-based tracker data path, delivered by an adjustment.

Under this framework every agent's session source is registered the same way — a module
installed by `features/<agent>/adjustments/` beside the common tracker scripts. Claude's
source is the transcript reader: it resolves sessions from CLAUDE_CODE_SESSION_ID (with the
pid/cwd fallbacks against current-session.json), checkpoints from the JSONL transcript, and
resumes with `claude --resume`.
"""
from __future__ import annotations

import os
from pathlib import Path

CLAUDE_SESSION_ENV = "CLAUDE_CODE_SESSION_ID"


def register(tracker_lib) -> None:
    """Register the claude session source with a tracker_lib module."""
    tracker_lib.register_session_source(
        "claude",
        env_var=CLAUDE_SESSION_ENV,
        resolve=lambda: _resolve(tracker_lib),
        checkpoint=lambda session: tracker_lib.make_checkpoint(
            session.get("transcriptPath") or ""),
        resume=lambda session_id: f"claude --resume {session_id}",
        transcript=True,
    )


def _resolve(tracker_lib) -> dict:
    """Identify the invoking claude session: env var, then pid ancestry, then unique cwd."""
    sessions = tracker_lib.load_current_sessions()

    env_id = os.environ.get(CLAUDE_SESSION_ENV)
    if env_id:
        if env_id in sessions:
            return {"sessionId": env_id, **sessions[env_id]}
        return {"sessionId": env_id, "transcriptPath": None}  # hook just hasn't fired yet

    ancestry = set(tracker_lib._own_pid_ancestry())
    for sid, info in sessions.items():
        if info.get("pid") in ancestry:
            return {"sessionId": sid, **info}

    cwd = str(Path.cwd())
    cwd_matches = [(sid, info) for sid, info in sessions.items() if info.get("cwd") == cwd]
    if len(cwd_matches) == 1:
        sid, info = cwd_matches[0]
        return {"sessionId": sid, **info}

    return {}
