#!/usr/bin/env python3
"""`UserPromptSubmit` entry for the per-prompt memory-context block (Claude and Copilot).

stdin carries the host's hook payload; `memory_context.build()` does the work. Claude payloads
(`hook_event_name == "UserPromptSubmit"`) get the `hookSpecificOutput` envelope; every other
payload, including Copilot's `{sessionId, timestamp, cwd, prompt}`, gets the flat
`{"additionalContext": ...}` shape — the only one Copilot CLI was measured to read (P0 spike:
3/3 flat, 0/3 envelope). Silent (exit 0, no output) on every expected failure: no prompt, no
block, or a closed stdout. A missing or broken `memory_context.py`, and any unexpected exception
here or inside `build()`, writes one content-free `hook-errors.log` line.
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

# A distinctive sys.modules key, not the bare "memory_context" a plain `import` would use — see
# context_enrichment_hook.py's CONTEXT_ENRICHMENT_MODULE_NAME for the same reasoning: several test
# modules load features/common/skills/ai-raccoon-memory/scripts/memory_context.py directly, and a
# bare import here could silently pick up whichever copy another test loaded first.
MEMORY_CONTEXT_MODULE_NAME = "ai_badger_memory_context_entry"

MAX_ERROR_LOG_BYTES = 1_000_000


def _load_memory_context() -> Optional[Any]:
    """Import the sibling `memory_context.py` lazily; None, with one log line, when it is
    missing or fails to import.

    Loaded by path, never by package name: a hook copied without its sibling (an older or
    partial scaffold) must degrade to no output, not crash on import before `guarded_main`
    can catch anything.
    """
    cached = sys.modules.get(MEMORY_CONTEXT_MODULE_NAME)
    if cached is not None:
        return cached
    path = Path(__file__).resolve().parent / "memory_context.py"
    if not path.is_file():
        record_hook_failure("memory_context_hook/missing-sibling")
        return None
    spec = importlib.util.spec_from_file_location(MEMORY_CONTEXT_MODULE_NAME, path)
    if spec is None or spec.loader is None:
        record_hook_failure("memory_context_hook/import")
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[MEMORY_CONTEXT_MODULE_NAME] = module
    try:
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught
        sys.modules.pop(MEMORY_CONTEXT_MODULE_NAME, None)
        record_hook_failure("memory_context_hook/import")
        return None
    return module


def _read_payload() -> Dict[str, Any]:
    """Parse stdin as a JSON object; {} on anything else (non-JSON, an array, empty stdin)."""
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError, OSError):
        return {}
    return data if isinstance(data, dict) else {}


def _cwd(payload: Dict[str, Any]) -> str:
    """The payload's own `cwd`, else `CLAUDE_PROJECT_DIR`, else the process cwd."""
    return str(payload.get("cwd") or os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd())


def _emit(obj: Dict[str, Any]) -> None:
    """Write one JSON line to stdout; a host that already hung up must never crash the hook."""
    try:
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()
    except BrokenPipeError:
        try:
            devnull = os.open(os.devnull, os.O_WRONLY)
            os.dup2(devnull, sys.stdout.fileno())
        except OSError:
            pass


def main() -> int:
    """Read the payload, build the block, print exactly one line in the host's shape, or none."""
    payload = _read_payload()
    prompt = payload.get("prompt")
    if not isinstance(prompt, str) or not prompt:
        return 0
    memory_context = _load_memory_context()
    if memory_context is None:
        return 0
    session_id = payload.get("session_id") or payload.get("sessionId")
    block = memory_context.build(prompt, _cwd(payload), session_id,
                                 on_error=record_hook_failure)
    if not block:
        return 0
    if payload.get("hook_event_name") == "UserPromptSubmit":
        _emit({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit",
                                       "additionalContext": block}})
    else:
        _emit({"additionalContext": block})
    return 0


def record_hook_failure(where: str) -> None:
    """Leave one content-free line behind before a hook swallows an exception, or a bare
    notice when none is being handled.

    Type and location only: an exception message can quote scanned prompt text. The log path
    is resolved here, at call time, not cached at import — so it follows a redirected HOME in
    every test rather than the HOME the module happened to see first.
    """
    exc_type, _, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    detail = f" {exc_type.__name__} at {at}" if exc_type else ""
    print(f"[ai-badger] {where} hook failed:{detail}" if exc_type else f"[ai-badger] {where}",
          file=sys.stderr)
    try:
        log_path = Path.home() / ".ai-badger" / "hook-errors.log"
        log_path.parent.mkdir(parents=True, exist_ok=True)
        if log_path.exists() and log_path.stat().st_size > MAX_ERROR_LOG_BYTES:
            log_path.unlink()
        with log_path.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where}{detail}\n")
    except OSError:
        pass


def guarded_main() -> int:
    """Run main(): a broken hook must never block a prompt, but never fail invisibly either."""
    try:
        return main() or 0
    except Exception:  # pylint: disable=broad-exception-caught
        record_hook_failure("memory_context_hook")
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
