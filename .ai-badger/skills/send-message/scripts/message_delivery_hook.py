#!/usr/bin/env python3
"""Message-bus delivery hook: inject the session's unread bus mail into its context.

The ONE delivery surface for every hook-shaped harness: Claude (UserPromptSubmit /
SessionStart / SessionEnd), Copilot (userPromptSubmitted / sessionStart), and pi via the
adapter's child-process bridge all speak the same Claude-shaped contract —

    stdin:  {"hook_event_name": "...", "session_id": "...", "cwd": "..."}
    stdout: {"hookSpecificOutput": {"hookEventName": "...", "additionalContext": "..."}}

Delivery events (SessionStart, UserPromptSubmit, Stop, either harness's spelling) call
``Store.deliver_for_session`` — exactly-once + the 30-minute first-read gate + the
16-message start cap are the store's one transaction, not this script's (P1); the
cursor-less per-turn read applies that gate once too (D5). Stop is Claude's turn-end
event: its ``additionalContext`` continues the turn (host loop-capped), so mail that
arrived mid-work surfaces without another user prompt — and exactly-once keeps that
continuation loop-safe (a second firing finds nothing new). Close events (SessionEnd)
drop the session's cursor (R6). Everything else is a no-op.

Residual: a fully idle session fires no hook until its next prompt — hooks cannot wake
Claude (no timer surface exists; FileChanged/Notification discard context output per
the Claude Code hooks reference), so mail arriving while idle still waits for the
next turn.

``additionalContext`` carries one schema-conformant message document per line
(``schemas/message.schema.json``, F4) in chronological order — render_messages is the
seam P9-t1 asserts through. A mail-bearing response additionally carries the delivery
transaction's wake classification as ``hookSpecificOutput.aiBadgerBus``
= ``{"addressed": n, "broadcast": m}`` (C2 — merged after build_response, so
``build_response``'s own shape is unchanged). An empty inbox injects nothing and adds
nothing: the response is ``{}`` — exactly.

Fail-open (D31): any store, registry or input failure exits 0 with parseable no-op
JSON — a broken bus must never break a session — and the failure is wire-distinguishable
from a clean empty read: ``{"hookSpecificOutput": {"aiBadgerBus": {"error": true}}}``
(C2b, CR-M1: a poller advancing on a parseable ``{}`` would strand undelivered mail).
The only trace is one line in the operator's hook-error log, because a hook that dies
quietly is indistinguishable from one that did its job.
"""
from __future__ import annotations

import json
import os
import re
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import badger_store  # pylint: disable=wrong-import-position

#: Events that deliver mail. Claude's spellings (SessionStart, UserPromptSubmit, and
#: the turn-end Stop — whose additionalContext continues the turn so mid-work mail
#: surfaces without another prompt) plus Copilot's (sessionStart / userPromptSubmitted)
#: — one surface, per-harness event names, matched case-insensitively.
DELIVERY_EVENTS = frozenset({"sessionstart", "userpromptsubmit", "userpromptsubmitted",
                              "stop"})

#: Events that end a session: drop the cursor (R6). Copilot's sessionEnd is wired for this
#: too (P8's verdict: the event exists — tooling/validate.py, changelog 0.50.0), so its
#: lowercase spelling routes here like Claude's; unwired harnesses' cursors die by the 4-day
#: TTL.
CLOSE_EVENTS = frozenset({"sessionend"})

#: The project directory env Claude sets for hook commands. Preferred over the payload's
#: cwd for ADDRESSING — it is the project the session belongs to and stays stable while
#: a session cd-wanders; the payload cwd is the fallback for harnesses that carry no env.
PROJECT_DIR_ENV = "CLAUDE_PROJECT_DIR"


def _event_name(payload: Dict[str, Any]) -> Optional[str]:
    """The harness's own event spelling, echoed verbatim in the response."""
    value = payload.get("hook_event_name") or payload.get("hookEventName")
    return value if isinstance(value, str) and value.strip() else None


def _session_id(payload: Dict[str, Any]) -> Optional[str]:
    """The session's id — the cursor's identity; blank or non-string is no session."""
    value = payload.get("session_id") or payload.get("sessionId")
    return value if isinstance(value, str) and value.strip() else None


def _probe_cwd(payload: Dict[str, Any]) -> Optional[str]:
    """The directory the project id resolves from: $CLAUDE_PROJECT_DIR, else payload cwd."""
    env_root = os.environ.get(PROJECT_DIR_ENV)
    if env_root:
        return env_root
    cwd = payload.get("cwd")
    return cwd if isinstance(cwd, str) and cwd.strip() else None


def _resolve_project(payload: Dict[str, Any]) -> Optional[str]:
    """The cwd resolver (D4, ADR-0025) — its explicit-override env wins inside it. An
    unresolved project (no .ai-badger/project-id above the cwd) degrades to 1:1-only
    delivery (D7); an unexpected resolver error is not designed and propagates to the
    fail-open net."""
    return badger_store.resolve_project_id(_probe_cwd(payload))


def render_messages(messages: list) -> str:
    """The injected context: ONE message document per line, chronological, verbatim —
    each line parses to the exact document the store returned and validates clean
    against schemas/message.schema.json (F4; the seam P9-t1 asserts through)."""
    return "\n".join(json.dumps(message, ensure_ascii=False) for message in messages)


def build_response(event_name: Optional[str], context: str) -> dict:
    """The hook's whole reply: additionalContext when there is mail, ``{}`` when not —
    never any coercing key (decision/continue): a delivery hook is advisory only."""
    if not context:
        return {}
    inner: Dict[str, Any] = {"additionalContext": context}
    if event_name:
        inner["hookEventName"] = event_name
    return {"hookSpecificOutput": inner}


def _deliver(event_name: Optional[str], session_id: str, payload: Dict[str, Any]) -> dict:
    """One store transaction: read + cursor advance (the store's; index-bounded, D6).

    C2's construction point: the txn's wake summary is merged into the response HERE,
    AFTER build_response — so build_response's own shape (and the exact-key-set pin on
    it) stays untouched while the wire still carries the summary. A mail-bearing
    response gains ``hookSpecificOutput.aiBadgerBus = {"addressed": n, "broadcast": m}``
    (additive, never a host-acted key, CR-N6); a clean empty read returns ``{}``
    unchanged — no envelope, no zero counts.
    """
    project_id = _resolve_project(payload)
    store = badger_store.open_user()
    try:
        messages, summary = store.deliver_for_session(session_id, project_id)
    finally:
        store.close()
    response = build_response(event_name, render_messages(messages))
    if messages and response:
        response["hookSpecificOutput"]["aiBadgerBus"] = {
            "addressed": summary["addressed"], "broadcast": summary["broadcast"]}
    return response


def _close(event_name: Optional[str], session_id: str) -> dict:
    """The close event's cleanup: drop the cursor (R6); a second close is harmless."""
    store = badger_store.open_user()
    try:
        store.delete_cursor(session_id)
    finally:
        store.close()
    return {}


def _read_payload_text() -> str:
    """The raw stdin text, captured before parsing: the C8 sanitizer redacts
    payload-derived substrings from any later exception's message, so the failure path
    needs the payload even when parsing itself is what fails."""
    try:
        return sys.stdin.read()
    except Exception:  # pylint: disable=broad-exception-caught
        return ""


def main(payload_text: Optional[str] = None) -> int:
    """One firing: parse, route by event, print one JSON response — every path prints."""
    raw = _read_payload_text() if payload_text is None else payload_text
    payload = json.loads(raw)
    if not isinstance(payload, dict):
        payload = {}
    event_name = _event_name(payload)
    session_id = _session_id(payload)
    kind = (event_name or "").strip().lower()
    if session_id and kind in DELIVERY_EVENTS:
        response = _deliver(event_name, session_id, payload)
    elif session_id and kind in CLOSE_EVENTS:
        response = _close(event_name, session_id)
    else:
        response = {}
    print(json.dumps(response))
    return 0


#: C2b (CR-M1): the fail-open net's wire shape. A failure inside guarded_main must be
#: distinguishable from a clean empty read on stdout — otherwise a poller advancing its
#: watermark on a parseable ``{}`` silently strands undelivered mail (M1's stall). The
#: marker is additive and rides inside ``hookSpecificOutput``, never a host-acted key
#: (CR-N6); a clean empty read stays exactly ``{}``.
FAILURE_MARKER = {"hookSpecificOutput": {"aiBadgerBus": {"error": True}}}


HOOK_ERRORS_FILE = Path.home() / ".ai-badger" / "hook-errors.log"
MAX_ERROR_LOG_BYTES = 1_000_000

#: Minimum length for a payload-derived candidate worth redacting (C8): shorter tokens
#: ("the", "and") would shred the message and hide the diagnosis without protecting
#: anything. JSON string values are candidates from 4 chars (session ids are short).
_REDACT_MIN_TOKEN = 8


def _payload_candidates(payload_text: Optional[str]) -> set:
    """Substrings of the raw stdin payload an exception message could quote (C8): the
    whole text, each line, each whitespace token, and every JSON string value the
    payload carries (parsed defensively — malformed stdin is itself a failure path).
    String values are split into tokens too: a leak may quote one word of the mail."""
    if not payload_text:
        return set()
    candidates: set = set()
    stripped = payload_text.strip()
    if len(stripped) >= _REDACT_MIN_TOKEN:
        candidates.add(stripped)
    for line in payload_text.splitlines():
        line = line.strip()
        if len(line) >= _REDACT_MIN_TOKEN:
            candidates.add(line)
    for token in re.findall(r"\S+", payload_text):
        if len(token) >= _REDACT_MIN_TOKEN:
            candidates.add(token)

    def _visit(value) -> None:
        if isinstance(value, str):
            if len(value) >= 4:
                candidates.add(value)
            for token in re.findall(r"\S+", value):
                if len(token) >= _REDACT_MIN_TOKEN:
                    candidates.add(token)
        elif isinstance(value, dict):
            for item in value.values():
                _visit(item)
        elif isinstance(value, list):
            for item in value:
                _visit(item)

    try:
        _visit(json.loads(payload_text))
    except ValueError:
        pass
    return candidates


def _redact_payload_text(message: str, payload_text: Optional[str]) -> str:
    """*message* with every payload-derived candidate substring replaced (C8):
    payload-derived substrings never reach the log."""
    redacted = message
    for candidate in _payload_candidates(payload_text):
        if candidate in redacted:
            redacted = redacted.replace(candidate, "[redacted]")
    return redacted


def record_hook_failure(where: str, payload_text: Optional[str] = None) -> None:
    """Leave one diagnosable line behind before the net swallows an exception.

    C8 (Lane B F4): the line carries the exception MESSAGE — sanitized first, because an
    exception message can quote scanned input: every substring derived from the hook's
    stdin payload is redacted (see _payload_candidates). Type and location stay.
    """
    exc_type, exc_value, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    name = exc_type.__name__ if exc_type else "Unknown"
    message = _redact_payload_text(str(exc_value) if exc_value else "", payload_text)
    detail = f"{name} at {at}: {message}" if message else f"{name} at {at}"
    print(f"[ai-badger] {where} hook failed: {name} at {at}", file=sys.stderr)
    try:
        HOOK_ERRORS_FILE.parent.mkdir(parents=True, exist_ok=True)
        if HOOK_ERRORS_FILE.exists() and HOOK_ERRORS_FILE.stat().st_size > MAX_ERROR_LOG_BYTES:
            HOOK_ERRORS_FILE.unlink()
        with HOOK_ERRORS_FILE.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where} {detail}\n")
    except OSError:
        pass


def guarded_main() -> int:
    """Run main(): a hook never breaks the session, but never fails invisibly either —
    the failure line goes to the log, and the host still gets parseable no-op JSON: the
    C2b failure marker, wire-distinguishable from a clean empty ``{}`` (CR-M1). B5: the
    log path itself is guarded — a throwing log write must never drop the response."""
    payload_text = _read_payload_text()
    try:
        return main(payload_text) or 0
    except Exception:  # pylint: disable=broad-exception-caught
        try:
            record_hook_failure("message_delivery_hook", payload_text)
        except Exception:  # pylint: disable=broad-exception-caught
            pass
        try:
            print(json.dumps(FAILURE_MARKER))
        except Exception:  # pylint: disable=broad-exception-caught
            pass
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
