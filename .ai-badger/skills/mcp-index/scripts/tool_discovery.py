#!/usr/bin/env python3
"""`--discover`: ask each server a listing left unenumerated for its own tools.

`server_configs` finds the command or URL in Claude Code's config files and `mcp_client` speaks
MCP to it, up to `WORKERS` servers at a time. A server that answered every tools/list page becomes
`tools_known` True and loses its failing health phrase; any other outcome leaves it exactly as the
listing had it, so `update` never reads a partial answer as removal.
"""

from __future__ import annotations

import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# mcp_client.py and server_configs.py sit beside this file in every deployment shape.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcp_client as mc  # noqa: E402  pylint: disable=wrong-import-position,import-error
import server_configs as sc  # noqa: E402  pylint: disable=wrong-import-position,import-error

TIMEOUT = 20.0
WORKERS = 8
PENDING_APPROVAL_PHRASE = "pending approval"

Server = Dict[str, Any]


def ask_server(spec: Server, cwd: str, timeout: float) -> Tuple[Optional[List[Server]], str]:
    """(tools, outcome) from one server; tools is None when it did not fully answer."""
    try:
        tools = mc.list_tools(spec, timeout, cwd=cwd)
    except mc.Unauthorized:
        return None, "needs authorization"
    except mc.Timeout:
        return None, "timed out"
    except mc.McpClientError as exc:
        return None, f"failed: {exc}"
    except Exception as exc:  # pylint: disable=broad-exception-caught
        # A bad URL, a malformed header or a dropped connection is this server's failure,
        # never the whole run's: future.result() would re-raise it into --discover.
        return None, f"failed: {type(exc).__name__}: {' '.join(str(exc).split())[:200]}"
    return tools, f"enumerated {len(tools)} tool(s)"


def _plan(pending: List[Server], configs: sc.Configs
          ) -> Tuple[Dict[str, Server], Dict[str, str], List[Server]]:
    """(specs to run by name, outcomes already known, servers with no config at all)."""
    jobs: Dict[str, Server] = {}
    outcomes: Dict[str, str] = {}
    unresolved: List[Server] = []
    for server in pending:
        name = server.get("name", "")
        spec, reason = sc.lookup(configs, name)
        if str(server.get("host_status") or "").strip().lower() == PENDING_APPROVAL_PHRASE:
            outcomes[name] = "not run: pending approval in Claude Code"
        elif spec is not None:
            jobs[name] = spec
        elif reason == sc.HOST_MANAGED:
            outcomes[name] = f"not enumerated: {reason}"
        else:
            outcomes[name] = reason
            if reason == sc.NO_CONFIG:  # hermes runs its own config, never the repo's command
                unresolved.append(server)
    return jobs, outcomes, unresolved


def enrich_with_mcp_client(servers: List[Server], target: str, home: Path,
                           timeout: float = TIMEOUT) -> Tuple[List[str], List[Server]]:
    """Fill every unenumerated server that answers; return (`name: outcome` lines, no-config)."""
    pending = [s for s in servers if not s.get("tools_known", True)]
    jobs, outcomes, unresolved = _plan(pending, sc.load(Path(target), home))
    answers: Dict[str, Optional[List[Server]]] = {}
    if jobs:
        with ThreadPoolExecutor(max_workers=min(WORKERS, len(jobs))) as pool:
            futures = {name: pool.submit(ask_server, spec, target, timeout)
                       for name, spec in jobs.items()}
            for name, future in futures.items():
                answers[name], outcomes[name] = future.result()
    lines = []
    for server in pending:
        name = server.get("name", "")
        tools = answers.get(name)
        if tools is not None:
            server["tools"] = tools
            server["tools_known"] = True
            server.pop("host_status", None)
            server["discovered_via"] = "mcp-client"
        lines.append(f"{name}: {outcomes[name]}")
    return lines, unresolved
