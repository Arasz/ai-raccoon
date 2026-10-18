#!/usr/bin/env python3
"""`--discover`: ask each server a listing left unenumerated for its own tools.

`server_configs` finds the command or URL in Claude Code's config files and `mcp_client` speaks
MCP to it, up to `WORKERS` servers at a time; a `headersHelper` runs first (`headers_helper`).
What that leaves unenumerated — OAuth servers, `claude.ai` connectors, servers with no config —
can then be read from Claude Code's own init event (`host_tools`); a direct answer always wins.
A server that answered every tools/list page, or that the host reports connected, becomes
`tools_known` True and loses its failing health phrase; any other outcome leaves it exactly as
the listing had it, so `update` never reads a partial answer as removal.
"""

from __future__ import annotations

import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

# The sibling modules sit beside this file in every deployment shape.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import headers_helper as hh  # noqa: E402  pylint: disable=wrong-import-position,import-error
import host_tools as ht  # noqa: E402  pylint: disable=wrong-import-position,import-error
import mcp_client as mc  # noqa: E402  pylint: disable=wrong-import-position,import-error
import server_configs as sc  # noqa: E402  pylint: disable=wrong-import-position,import-error

TIMEOUT = 20.0
HOST_TIMEOUT = ht.TIMEOUT
WORKERS = 8
PENDING_APPROVAL_PHRASE = "pending approval"
AUTH_HINT = ("authenticate it in Claude Code (/mcp), then rerun --discover with host tools on "
             "(the default; not --no-host-tools)")

Server = Dict[str, Any]


def _challenge(exc: "mc.Unauthorized") -> str:
    """`needs authorization (HTTP 401): resource_metadata=…, scope=…` from the refusal."""
    parts = [f"{key}={value}" for key, value in (("resource_metadata", exc.resource_metadata),
                                                 ("scope", exc.scope)) if value]
    return f"{exc}: {', '.join(parts)}" if parts else str(exc)


def _with_helper(spec: Server, name: str, helper: Optional[sc.Helper]
                 ) -> Tuple[Server, str, bool]:
    """(the spec to connect with, a note, whether a helper ran); its headers override static ones."""
    command = spec.get("headersHelper")
    if not command or mc.transport_of(spec) != "http" or helper is None:
        return spec, "", False
    effective = {k: v for k, v in spec.items() if k != "headersHelper"}
    if not helper.trusted:
        return effective, "headersHelper not run: project not trusted in Claude Code", False
    url = mc.expand(str(spec.get("url") or ""), os.environ)
    env = hh.helper_env(os.environ, name=name, url=url, scrub=helper.scrub,
                        plugin_root=helper.plugin_root)
    dynamic = hh.run(str(command), cwd=helper.cwd, env=env)
    effective["headers"] = {**(spec.get("headers") or {}), **dynamic}
    return effective, "", True


def _left(deadline: float) -> float:
    """Seconds left of one server's deadline, which its headersHelper runs also spend."""
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise mc.Timeout("deadline spent before connecting")
    return remaining


def _list(spec: Server, cwd: str, deadline: float, name: str,
          helper: Optional[sc.Helper]) -> Tuple[List[Server], str]:
    """The tools, with one helper re-run and retry on 401/403.

    Claude Code: "If a tool call returns `401 Unauthorized` or `403 Forbidden`, Claude Code
    automatically re-runs the helper ..., reconnects with the fresh headers, and retries the
    call once" (code.claude.com/docs/en/mcp, headersHelper).
    """
    effective, note, helped = _with_helper(spec, name, helper)
    try:
        return mc.list_tools(effective, _left(deadline), cwd=cwd), note
    except mc.Unauthorized:
        if not helped:
            raise
    effective, note, _ = _with_helper(spec, name, helper)
    return mc.list_tools(effective, _left(deadline), cwd=cwd), note


def ask_server(spec: Server, cwd: str, timeout: float, name: str = "",
               helper: Optional[sc.Helper] = None) -> Tuple[Optional[List[Server]], str]:
    """(tools, outcome) from one server; tools is None when it did not fully answer."""
    note = "headersHelper not run: project not trusted in Claude Code" if (
        helper is not None and not helper.trusted and spec.get("headersHelper")) else ""
    try:
        tools, note = _list(spec, cwd, time.monotonic() + timeout, name, helper)
    except hh.HeadersHelperError as exc:
        return None, f"failed: headersHelper {exc}"
    except mc.Unauthorized as exc:
        return None, "; ".join(filter(None, [_challenge(exc), note]))
    except mc.Timeout:
        return None, "timed out"
    except mc.McpClientError as exc:
        return None, f"failed: {exc}"
    except Exception as exc:  # pylint: disable=broad-exception-caught
        # A bad URL, a malformed header or a dropped connection is this server's failure,
        # never the whole run's: future.result() would re-raise it into --discover.
        return None, f"failed: {type(exc).__name__}: {' '.join(str(exc).split())[:200]}"
    return tools, "; ".join(filter(None, [f"enumerated {len(tools)} tool(s)", note]))


def _plan(pending: List[Server], configs: sc.Configs
          ) -> Tuple[Dict[str, Server], Dict[str, str], List[Server], Set[str]]:
    """(specs to run, outcomes already known, servers with no config, servers refused a run)."""
    jobs: Dict[str, Server] = {}
    outcomes: Dict[str, str] = {}
    unresolved: List[Server] = []
    refused: Set[str] = set()
    for server in pending:
        name = server.get("name", "")
        spec, reason = sc.lookup(configs, name)
        if str(server.get("host_status") or "").strip().lower() == PENDING_APPROVAL_PHRASE:
            outcomes[name] = "not run: pending approval in Claude Code"
            refused.add(name)
        elif spec is not None:
            jobs[name] = spec
        elif reason == sc.HOST_MANAGED:
            outcomes[name] = f"not enumerated: {reason}"
        else:
            outcomes[name] = reason
            if reason == sc.NOT_APPROVED:
                refused.add(name)
            if reason == sc.NO_CONFIG:  # hermes runs its own config, never the repo's command
                unresolved.append(server)
    return jobs, outcomes, unresolved, refused


def _ask_host(pending: List[Server], target: str, home: Path, host: Optional[str],
              host_timeout: float) -> Tuple[Dict[str, str], List[str]]:
    """(per-server notes, run-level lines) from Claude Code's init event.

    `claude -p` skips the workspace trust dialog, so the target's settings helpers
    (apiKeyHelper, otelHeadersHelper, approved headersHelpers, ...) would run in a directory
    nobody trusted: an untrusted target is never run, whatever `--host-tools` says.
    """
    if not host or not pending:
        return {}, []
    if not sc.trusted(Path(target), home):
        return {}, [f"host tools skipped: {target} is not trusted in Claude Code — open it in "
                    f"Claude Code once and accept the trust dialog"]
    refusal = ht.blocked()
    if refusal:
        return {}, [f"host tools {refusal}"]
    try:
        reading = ht.read_init(host, target, host_timeout)
    except ht.HostBilled as exc:
        return {}, [f"WARNING: claude init {exc}"]
    except ht.HostToolsError as exc:
        return {}, [f"claude init failed: {exc}"]
    notes = ht.fill(pending, ht.map_tools(reading.init))
    return notes, [f"WARNING: claude init {reading.warning}"] if reading.warning else []


def _line(outcome: str, note: Optional[str], server: Server) -> str:
    """One report line: the direct outcome, what the host added, and the fix if still skipped."""
    if note and server.get("discovered_via") == ht.VIA:
        return f"{note} (direct: {outcome})"
    text = f"{outcome}; {note}" if note else outcome
    if not server.get("tools_known", True) and outcome.startswith(("needs authorization",
                                                                   "forbidden")):
        text += f" — {AUTH_HINT}"
    return text


def enrich_with_mcp_client(servers: List[Server], target: str, home: Path,
                           timeout: float = TIMEOUT, host: Optional[str] = None,
                           host_timeout: float = HOST_TIMEOUT
                           ) -> Tuple[List[str], List[Server]]:
    """Fill every unenumerated server that answers, then ask `host` (a `claude` executable)
    for the rest; return (`name: outcome` lines, no-config servers still unenumerated)."""
    pending = [s for s in servers if not s.get("tools_known", True)]
    configs = sc.load(Path(target), home)
    jobs, outcomes, unresolved, refused = _plan(pending, configs)
    answers: Dict[str, Optional[List[Server]]] = {}
    if jobs:
        with ThreadPoolExecutor(max_workers=min(WORKERS, len(jobs))) as pool:
            futures = {name: pool.submit(ask_server, spec, target, timeout, name,
                                         configs.helpers.get(name))
                       for name, spec in jobs.items()}
            for name, future in futures.items():
                answers[name], outcomes[name] = future.result()
    for server in pending:
        tools = answers.get(server.get("name", ""))
        if tools is not None:
            server["tools"] = tools
            server["tools_known"] = True
            server.pop("host_status", None)
            server["discovered_via"] = "mcp-client"
    left = [s for s in pending if not s.get("tools_known", True)
            and s.get("name", "") not in refused]
    notes, run_lines = _ask_host(left, target, home, host, host_timeout)
    lines = []
    for server in pending:
        name = server.get("name", "")
        lines.append(f"{name}: {_line(outcomes[name], notes.get(name), server)}")
    lines.extend(f"({line})" for line in run_lines)
    return lines, [s for s in unresolved if not s.get("tools_known", True)]
