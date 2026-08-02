#!/usr/bin/env python3
"""Capture Claude Code statusLine JSON for task-skill automation, then delegate display.

The user's rich status line remains the renderer. This wrapper persists the raw
statusLine payload so poll_limit.py can use Claude Code's own rate-limit metadata
instead of spending a probe while a reset time is known.
"""
# pylint: disable=missing-function-docstring
# Originally ported from the job-search-ai-assistant repo's /task skill; its docstring style
# is kept rather than churned.

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tracker_lib as lib

# The delegate is resolved per run, never baked in: CLAUDE_USER_STATUSLINE wins when set,
# otherwise the record the scaffolder wrote when it displaced the project's own renderer.
# Whether settings `env` reaches a statusLine command is undocumented, so the record must stand alone.
_USER_STATUSLINE_ENV = "CLAUDE_USER_STATUSLINE"
STATUSLINE_STATE = lib.DATA_DIR / "statusline-state.json"
DELEGATE_RECORD = lib.DATA_DIR / "statusline-delegate.json"


def capture_statusline(input_text: str) -> None:
    try:
        payload = json.loads(input_text)
    except json.JSONDecodeError:
        return
    lib.ensure_data_dir()
    state = {
        "capturedAt": lib.now_iso(),
        "sessionId": payload.get("session_id"),
        "transcriptPath": payload.get("transcript_path"),
        "cwd": payload.get("cwd") or payload.get("workspace", {}).get("current_dir"),
        "rateLimits": payload.get("rate_limits", {}),
        "contextWindow": payload.get("context_window", {}),
        "model": payload.get("model", {}),
    }
    lib.save_json(STATUSLINE_STATE, state)


def resolve_delegate():
    """The renderer command to delegate to, or None when nothing renders behind the capture."""
    configured = os.environ.get(_USER_STATUSLINE_ENV)
    if configured:
        return configured
    try:
        record = json.loads(DELEGATE_RECORD.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    command = record.get("command") if isinstance(record, dict) else None
    return command or None


def render_user_statusline(input_text: str) -> int:
    command = resolve_delegate()
    if not command:
        return 0
    # A settings statusLine command may be a bare path or a shell string; run each as itself.
    expanded = os.path.expanduser(command)
    runnable = os.path.isfile(expanded)
    try:
        result = subprocess.run(
            [expanded] if runnable else command,
            shell=not runnable,
            input=input_text,
            text=True,
            capture_output=True,
            timeout=5,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return 0
    if result.stdout:
        print(result.stdout, end="")
    return 0


def main() -> int:
    input_text = sys.stdin.read()
    capture_statusline(input_text)
    return render_user_statusline(input_text)


if __name__ == "__main__":
    sys.exit(main())
