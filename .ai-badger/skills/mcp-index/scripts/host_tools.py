#!/usr/bin/env python3
"""Tool names from Claude Code's own headless init event: `read_init`, `map_tools`, `fill`.

`claude -p --output-format stream-json --verbose` prints a `system/init` event whose `tools`
carry `mcp__<server>__<tool>` for every server the host connected, and whose `mcp_servers`
carry each server's status. The host does the authenticating — OAuth servers and `claude.ai`
connectors included — so this module handles no credentials and reads no token store.

Measured on Claude Code 2.1.302 (2026-10-18): the prompt `/cost` is a local command, so the
init event arrives (~5.5s) with 0 API requests and `total_cost_usd: 0`; `--settings
{"disableAllHooks": true}` ran 0 settings hooks (43 without it), though it cannot switch off
managed-policy hooks; `--no-session-persistence` left no transcript. Names only: the event
carries no descriptions. The run is read on to its `result` event, and one that shows a model
turn leaves a sentinel that refuses every later run until it is deleted.
"""

from __future__ import annotations

import json
import os
import queue
import re
import shutil
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, NamedTuple, Optional, Set

# The sibling modules sit beside this file in every deployment shape.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcp_client as mc  # noqa: E402  pylint: disable=wrong-import-position,import-error
import tool_descriptions as td  # noqa: E402  pylint: disable=wrong-import-position,import-error

CLAUDE_ENV = "MCP_INDEX_CLAUDE"
TIMEOUT = 60.0
# Seconds after the init event to wait for the `result` event that proves the run was free.
SETTLE = 10.0
CONNECTED = "connected"
VIA = "claude-init"
# `/cost` answers with this model on its local, unbilled assistant message (measured 2.1.302).
LOCAL_MODEL = "<synthetic>"
ARGV = ("-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
        "--settings", json.dumps({"disableAllHooks": True}), "/cost")
# Claude Code's own name for a tool: `mcp__<server>__<tool>`, the server name with every
# character outside [A-Za-z0-9_-] replaced by `_` (code.claude.com/docs/en/mcp, plugin tools).
_UNSAFE = re.compile(r"[^A-Za-z0-9_-]")
PREFIX, SEP = "mcp__", "__"

Server = Dict[str, Any]


class HostToolsError(RuntimeError):
    """The init event could not be read; the message is one line naming why."""


class HostBilled(HostToolsError):
    """The run showed a model turn; the sentinel has been written."""


class HostInit(NamedTuple):
    """What the init event says: tool names by listing name, statuses, refused names."""

    tools: Dict[str, List[str]]
    statuses: Dict[str, str]
    ambiguous: Set[str]


class Reading(NamedTuple):
    """The init event, and a warning when the run's cost could not be confirmed."""

    init: Dict[str, Any]
    warning: str


def executable() -> Optional[str]:
    """`$MCP_INDEX_CLAUDE` when set (empty means none), else the `claude` on PATH."""
    if CLAUDE_ENV in os.environ:
        return os.environ[CLAUDE_ENV] or None
    return shutil.which("claude")


def sentinel() -> Path:
    """`~/.ai-badger/mcp-index/host-tools-billed.json`: present means a run looked billed."""
    return Path.home() / ".ai-badger" / "mcp-index" / "host-tools-billed.json"


def blocked() -> str:
    """Why the host step must not run because of an earlier billed run, else ''."""
    path = sentinel()
    if not path.exists():
        return ""
    return (f"refused: an earlier run showed a model turn (see {path}); check your usage, "
            f"then delete {path} to re-enable host tools")


def _billed(reason: str, cwd: str) -> HostBilled:
    path = sentinel()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"reason": reason, "target": cwd, "at": datetime.now(
            timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "argv": list(ARGV)}, indent=2) + "\n",
                        encoding="utf-8")
    except OSError:
        pass
    return HostBilled(f"MODEL TURN DETECTED ({reason}); host tools are off until you delete "
                      f"{path}")


def _status_phrases() -> Dict[str, str]:
    """Init status -> the `claude mcp list` phrase with the same first word, from the one
    vocabulary `tool_descriptions.HOST_STATUS_PHRASES` (`needs-auth` -> `needs authentication`)."""
    phrases: Dict[str, str] = {}
    for status in ("needs-auth", "failed", "pending"):
        head = status.split("-", 1)[0]
        matches = [p for p in td.HOST_STATUS_PHRASES if p.split()[0] == head]
        if len(matches) == 1:
            phrases[status] = matches[0]
    return phrases


STATUS_PHRASES = _status_phrases()


def prefixes(name: str) -> Set[str]:
    """The `mcp__` prefix forms one server name may take: the documented rule, and the same
    with runs of `_` collapsed and trimmed, which `claude.ai <Name>` connectors match too."""
    plain = _UNSAFE.sub("_", name)
    return {plain, re.sub(r"_+", "_", plain).strip("_")} - {""}


def map_tools(init: Dict[str, Any]) -> HostInit:
    """Assign each `mcp__` tool to exactly one listed server and one tool name; refuse guesses.

    A server whose prefix another server shares, or one tool name that parses more than one
    way (two servers, or two splits of one server such as `x_`), is refused whole.
    """
    statuses = {str(s["name"]): str(s.get("status") or "")
                for s in init.get("mcp_servers") or []
                if isinstance(s, dict) and isinstance(s.get("name"), str)}
    owners: Dict[str, Set[str]] = {}
    for name in statuses:
        for form in prefixes(name):
            owners.setdefault(form, set()).add(name)
    ambiguous = {n for names in owners.values() if len(names) > 1 for n in names}
    tools: Dict[str, List[str]] = {name: [] for name in statuses}
    for full in init.get("tools") or []:
        if not isinstance(full, str) or not full.startswith(PREFIX):
            continue
        rest = full[len(PREFIX):]
        claims = {(n, rest[len(f) + len(SEP):]) for f, names in owners.items() for n in names
                  if rest.startswith(f + SEP) and len(rest) > len(f) + len(SEP)}
        if len(claims) > 1:
            ambiguous |= {n for n, _ in claims}
        elif claims:
            name, tool = claims.pop()
            if tool not in tools[name]:
                tools[name].append(tool)
    return HostInit({n: t for n, t in tools.items() if n not in ambiguous}, statuses, ambiguous)


def _last(stderr) -> str:
    return mc._one_line(stderr[-1]) if stderr else "no stderr"  # pylint: disable=protected-access


def _events(session, deadline: float):
    """Each JSON object on stdout until `deadline`; None once, on timeout; raises on exit."""
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            yield None
            return
        try:
            line = session.stdout.get(timeout=min(remaining, 0.2))
        except queue.Empty:
            # A child it spawned can hold stdout open after claude itself has exited.
            line = None if session.proc.poll() is not None else b""
        if line is None:
            time.sleep(0.05)  # let the stderr pump catch the last line
            raise HostToolsError(f"claude exited ({session.proc.poll()}): "
                                 f"{_last(session.stderr)}")
        if line is mc._TOO_LARGE:  # pylint: disable=protected-access
            raise HostToolsError(f"a line too large (over {mc.MAX_MESSAGE_BYTES} bytes)")
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if isinstance(event, dict):
            yield event


def _model_turn(event: Dict[str, Any]) -> str:
    """Why this event shows a billed model turn, else ''."""
    if event.get("type") == "assistant":
        message = event.get("message") if isinstance(event.get("message"), dict) else {}
        if message.get("model") != LOCAL_MODEL:
            return f"assistant message from model {mc._one_line(message.get('model'), 60)}"  # pylint: disable=protected-access
    if event.get("type") == "result":
        cost, turns = event.get("total_cost_usd") or 0, event.get("num_turns") or 0
        if not isinstance(cost, (int, float)) or not isinstance(turns, int) or cost > 0 or turns > 0:
            return f"result with total_cost_usd={cost!r}, num_turns={turns!r}"
    return ""


def _settle(session, deadline: float, cwd: str) -> str:
    """Read on to the `result` event: '' when it proves the run free, else a warning."""
    try:
        for event in _events(session, deadline):
            if event is None:
                break
            reason = _model_turn(event)
            if reason:
                raise _billed(reason, cwd)
            if event.get("type") == "result":
                return ""
    except HostBilled:
        raise
    except HostToolsError as exc:
        return f"unknown cost: claude ended before its result event ({exc})"
    return f"unknown cost: no result event within {SETTLE:g}s of init"


def read_init(exe: str, cwd: str, timeout: float = TIMEOUT) -> Reading:
    """Run `exe` in `cwd` to its init event and on to its result, then kill its process group.

    Raises HostToolsError when no init event came, HostBilled when the run showed a model turn.
    """
    deadline = time.monotonic() + timeout
    try:
        session = mc._Stdio({"command": exe, "args": list(ARGV)},  # pylint: disable=protected-access
                            deadline + SETTLE, cwd)
    except mc.McpClientError as exc:
        raise HostToolsError(str(exc)) from exc
    try:
        session.proc.stdin.close()  # a prompt is on argv; an open stdin only delays the start
        try:
            for event in _events(session, deadline):
                if event is None:
                    raise HostToolsError(f"timed out after {timeout:g}s waiting for the init "
                                         f"event")
                reason = _model_turn(event)
                if reason:
                    raise _billed(reason, cwd)
                if event.get("type") == "system" and event.get("subtype") == "init":
                    return Reading(event, _settle(session, time.monotonic() + SETTLE, cwd))
        except HostBilled:
            raise
        except HostToolsError as exc:
            if "exited" in str(exc):
                raise HostToolsError(f"{exc} before its init event") from exc
            raise
        raise HostToolsError("no init event")  # pragma: no cover - _events never ends quietly
    finally:
        session.close()


def fill(pending: List[Server], found: HostInit) -> Dict[str, str]:
    """Add each connected server's tools; return `name: note` for every pending server.

    The init `tools` list is filtered by permission deny rules and managed policy while the
    server still reads `connected`, so it is never the whole truth: the server is marked
    `tools_partial`, which adds tools but never removes one. A connected server with no tools,
    and any other status, leaves the server as listed; the status is noted, and becomes its
    `host_status` when the listing had none.
    """
    notes: Dict[str, str] = {}
    for server in pending:
        name = server.get("name", "")
        status = found.statuses.get(name)
        names = found.tools.get(name, [])
        if name in found.ambiguous:
            notes[name] = "claude init: ambiguous tool names, not filled"
        elif status is None:
            notes[name] = "not in claude init"
        elif status == CONNECTED and names:
            server["tools"] = [{"name": tool, "description": ""} for tool in names]
            server["tools_known"] = True
            server["tools_partial"] = True
            server.pop("host_status", None)
            server["discovered_via"] = VIA
            notes[name] = f"enumerated {len(names)} tool(s) via claude init"
        elif status == CONNECTED:
            notes[name] = "claude init reports connected with 0 tools; left as listed"
        else:
            notes[name] = f"claude init reports {mc._one_line(status, 40)}"  # pylint: disable=protected-access
            if not server.get("host_status") and status in STATUS_PHRASES:
                server["host_status"] = STATUS_PHRASES[status]
    return notes
