#!/usr/bin/env python3
"""PostToolUse hook: auto-save the Semantica export_graph result to .semantica/.

Claude Code and Copilot have no in-process plugin, so their PostToolUse /
postToolUse entries cannot call autosave_export directly. This transport reads
the hook payload (tool name, result, session id) from stdin and delegates to the
sibling export module's autosave_export — the same function the Hermes plugin's
post_tool_call dispatch runs. Advisory only, never blocking: no output, exit 0
on every path (a crash here must never fail the tool call).

Payload field spellings follow the two hook schemas:
  Claude Code: tool_name / tool_response / session_id
  Copilot:     toolName / toolResponse / sessionId
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional

# Load the sibling export module by path so this hook works standalone in the
# shipped skill tree (same trick as _load_sibling_module in ai_badger_hooks.py).
_EXPORT_SCRIPT = Path(__file__).resolve().parent / "export_semantica_graph.py"


def _load_export_module():
    """Import export_semantica_graph.py from this hook's own directory."""
    if not _EXPORT_SCRIPT.is_file():
        return None
    spec = importlib.util.spec_from_file_location("ai_badger_semantica_export_hook", _EXPORT_SCRIPT)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _extract(payload: Dict[str, Any]) -> tuple:
    """Pull (tool_name, result, session_id) from either agent's payload spelling."""
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    result = payload.get("tool_response") or payload.get("toolResponse") or payload.get("result")
    session_id = payload.get("session_id") or payload.get("sessionId")
    return tool_name, result, session_id


def _project_dir(payload: Dict[str, Any]) -> Optional[Path]:
    """Resolve the project dir: $CLAUDE_PROJECT_DIR, else the payload's cwd, else None.

    PostToolUse payloads carry no cwd, and the hook's process cwd is the
    agent's launcher, not the project — writing .semantica/ there would scatter
    dumps across the machine. Mirrors commit_reminder_hook.resolve_project_root
    and debug_log.resolve_project_root: env first, payload cwd second, never a
    guess. None means skip (no write, exit 0).
    """
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root:
        return Path(env_root)
    cwd = payload.get("cwd")
    return Path(cwd) if cwd else None


def main(argv: Optional[list] = None) -> int:
    """Read the hook payload from stdin; autosave an export_graph result; exit 0."""
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0

    export_module = _load_export_module()
    if export_module is None:
        return 0
    if not export_module.is_export_graph(_extract(payload)[0]):
        return 0

    project_dir = _project_dir(payload)
    if project_dir is None:
        return 0

    try:
        tool_name, result, session_id = _extract(payload)
        export_module.autosave_export(tool_name, result, session_id, project_dir)
    except Exception:  # pylint: disable=broad-exception-caught
        # Advisory only — a hook failure must never block the tool call.
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
