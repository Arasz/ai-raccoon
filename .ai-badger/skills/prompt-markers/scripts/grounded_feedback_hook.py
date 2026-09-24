#!/usr/bin/env python3
"""PostToolUse / PostToolUseFailure hook: capture Bash failure output for grounded feedback.

Rule 3C: when a Bash command fails, inject the tail of its output as additionalContext so the
agent has concrete failure evidence in its next turn instead of relying on vague recollection.

Two arms wire this one script (hooks-manifest.json: `grounded-feedback`, `grounded-feedback-
failure`). Claude's PostToolUse "Runs immediately after a tool completes successfully"
(hooks.md) — it never sees a failed Bash call — so a second, claude-only manifest entry fires
this script on PostToolUseFailure instead, whose input carries `error` (a leading `Exit code N`
line, optionally, then the output) and `is_interrupt` rather than `tool_response`/`exit_code`.
Every other host (Copilot, Hermes, pi) runs its post-tool hook regardless of outcome, so their
single PostToolUse arm already reaches both cases via the `tool_response`/`exit_code` branch.

Advisory only, never blocking: emits `additionalContext` alone, exit 0, and never
`decision`/`permissionDecision`/`continue`.

Silent when: not a Bash tool, zero exit code (PostToolUse) or an interrupted call
(PostToolUseFailure), no output, or any internal error.
"""
from __future__ import annotations

import json
import re
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

# A PostToolUseFailure `error` string's own leading line, when Claude puts the exit code there.
EXIT_CODE_LINE = re.compile(r"^Exit code (-?\d+)\s*$")

_PAYLOAD: dict = {}


def _truncate_tail(text: str) -> str:
    """The tail of *text* (up to MAX_OUTPUT_LINES lines / MAX_OUTPUT_CHARS chars).

    Trims only from the front, so anything already at the end — including a truncation
    marker the caller appended — survives verbatim.
    """
    lines = text.splitlines()
    if len(lines) > MAX_OUTPUT_LINES:
        lines = lines[-MAX_OUTPUT_LINES:]
    truncated = "\n".join(lines)
    if len(truncated) > MAX_OUTPUT_CHARS:
        truncated = truncated[-MAX_OUTPUT_CHARS:]
    return truncated


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

    return _truncate_tail(output)


def extract_failure_error(payload: dict) -> tuple[str, str] | None:
    """(exit_code, output) from a PostToolUseFailure `error` string, or None to stay silent.

    Only the Bash/terminal arm (the same tools PostToolUse captures); an interrupted call
    (`is_interrupt: true`) carries no advisory. The exit code comes from a leading `Exit code N`
    line when the error has one, `?` otherwise; the rest of the string is kept verbatim
    (truncation marker included) and then run through the same tail-preserving trim.
    """
    tool_name = (payload.get("tool_name") or payload.get("toolName") or "").lower()
    if tool_name not in ("bash", "terminal"):
        return None
    if payload.get("is_interrupt"):
        return None

    error = payload.get("error")
    if not error or not str(error).strip():
        return None
    error = str(error)

    first_line, _, rest = error.partition("\n")
    match = EXIT_CODE_LINE.match(first_line.strip())
    if match:
        exit_code, body = match.group(1), rest
    else:
        exit_code, body = "?", error
    if not body.strip():
        body = error

    return exit_code, _truncate_tail(body)


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

    # The event this hook was fired for, echoed back verbatim (R11) — Claude's own payload
    # never lacks it, but every existing PostToolUse-only test predates the field, so a missing
    # one still means PostToolUse, its only caller until the failure arm existed.
    event = payload.get("hook_event_name") or payload.get("hookEventName") or "PostToolUse"

    if event == "PostToolUseFailure":
        parsed = extract_failure_error(payload)
        if parsed is None:
            _debug("skip", reason="no_failure_error")
            return 0
        exit_code, output = parsed
    else:
        output = extract_failure_output(payload)
        if output is None:
            _debug("skip", reason="no_failure_output")
            return 0
        result = (payload.get("tool_response") or payload.get("toolResponse")
                  or payload.get("tool_result") or payload.get("result") or {})
        exit_code = result.get("exit_code") or result.get("exitCode") or "?"

    message = ADVISORY_TEMPLATE.format(exit_code=exit_code, output=output)

    _debug("fire", hook_event=event, exit_code=exit_code, output_lines=output.count("\n") + 1)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": event,
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
