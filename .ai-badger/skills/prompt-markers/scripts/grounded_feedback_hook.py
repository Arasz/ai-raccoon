#!/usr/bin/env python3
"""PostToolUse hook: capture failure output from Bash commands for grounded feedback.

Rule 3C: when a Bash command exits non-zero, inject the tail of its output as
additionalContext so the agent has concrete failure evidence in its next turn
instead of relying on vague recollection.

Advisory only, never blocking: emits `additionalContext` alone, exit 0, and never
`decision`/`permissionDecision`/`continue`.

Silent when: not a Bash tool, zero exit code, no output, or any internal error.
"""
from __future__ import annotations

import json
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members
try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "grounded_feedback_hook"
MAX_OUTPUT_LINES = 30
MAX_OUTPUT_CHARS = 3000

_PAYLOAD: dict = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or _PAYLOAD.get("cwd")
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def extract_failure_output(payload: dict) -> str | None:
    """Extract the tail of output from a failed Bash command.

    Returns the output string (up to MAX_OUTPUT_LINES lines / MAX_OUTPUT_CHARS chars)
    or None if there is nothing to capture.

    Supports Claude (tool_response), Copilot (toolResponse), Hermes (tool_result),
    and generic (result) payload shapes.
    """
    tool_name = (payload.get("tool_name") or payload.get("toolName") or "").lower()
    if tool_name not in ("bash", "terminal"):
        return None

    result = (payload.get("tool_response") or payload.get("toolResponse")
              or payload.get("tool_result") or payload.get("result") or {})
    if not isinstance(result, dict):
        return None

    exit_code = result.get("exit_code") or result.get("exitCode")
    if exit_code is None or exit_code == 0:
        return None

    # Combine stdout and stderr when both are present; fall back to output field.
    parts = []
    for key in ("output", "stdout", "stderr"):
        val = result.get(key)
        if val and val.strip():
            parts.append(val.strip())
    output = "\n".join(parts) if parts else ""
    if not output:
        return None

    lines = output.splitlines()
    if len(lines) > MAX_OUTPUT_LINES:
        lines = lines[-MAX_OUTPUT_LINES:]

    truncated = "\n".join(lines)
    if len(truncated) > MAX_OUTPUT_CHARS:
        truncated = truncated[-MAX_OUTPUT_CHARS:]

    return truncated


ADVISORY_TEMPLATE = (
    "GROUNDED FEEDBACK: The last Bash command exited with code {exit_code}. "
    "Here is the failure output — use it as evidence for your next correction:\n\n"
    "```\n{output}\n```"
)


def main() -> int:
    """Read the hook payload from stdin; inject failure output if a Bash command failed."""
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    if not isinstance(payload, dict):
        return 0
    _PAYLOAD.update(payload)

    output = extract_failure_output(payload)
    if output is None:
        _debug("skip", reason="no_failure_output")
        return 0

    result = (payload.get("tool_response") or payload.get("toolResponse")
              or payload.get("tool_result") or payload.get("result") or {})
    exit_code = result.get("exit_code") or result.get("exitCode") or "?"
    message = ADVISORY_TEMPLATE.format(exit_code=exit_code, output=output)

    _debug("fire", exit_code=exit_code, output_lines=output.count("\n") + 1)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PostToolUse",
            "additionalContext": message,
        }
    }))
    return 0


HOOK_ERRORS_FILE = Path.home() / ".ai-badger" / "hook-errors.log"
MAX_ERROR_LOG_BYTES = 1_000_000


def record_hook_failure(where):
    """Leave one content-free line behind before a hook swallows an exception."""
    exc_type, _, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    name = exc_type.__name__ if exc_type else "Unknown"
    print(f"[ai-badger] {where} hook failed: {name} at {at}", file=sys.stderr)
    try:
        HOOK_ERRORS_FILE.parent.mkdir(parents=True, exist_ok=True)
        if HOOK_ERRORS_FILE.exists() and HOOK_ERRORS_FILE.stat().st_size > MAX_ERROR_LOG_BYTES:
            HOOK_ERRORS_FILE.unlink()
        with HOOK_ERRORS_FILE.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where} {name} at {at}\n")
    except OSError:
        pass


def guarded_main():
    """Run main(): a hook never breaks the session, but never fails invisibly either."""
    try:
        return main() or 0
    except Exception:  # pylint: disable=broad-exception-caught
        record_hook_failure(COMPONENT)
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
