#!/usr/bin/env python3
"""Which MCP servers a host CLI reports, and what it says about each one.

Three verified surfaces, asked in that order by mcp_index's init/update: `hermes mcp list
--json` (the only one that can carry tool names — absent from hermes since 2026-07, issue
#188), `claude mcp list` (every server plus a reachability phrase), and the `hermes mcp list`
text table (server names and an enabled flag). Parsers only; the index file, the status
vocabulary and the CLI live elsewhere in this directory.
"""

from __future__ import annotations

import json
import re
import subprocess
from typing import Any, Callable, Dict, List, NamedTuple, Optional, Tuple

# `claude mcp list` health-checks every server before printing; 13.6s measured for 17 servers.
TIMEOUT_SECONDS = 180

HERMES, CLAUDE = "hermes", "claude"
HOSTS = (HERMES, CLAUDE)

Server = Dict[str, Any]
# (exit code or None when the CLI never ran, stdout, a note for the human)
Outcome = Tuple[Optional[int], str, str]


# ── the shapes each CLI actually prints ─────────────────────────────────────

def parse_hermes_json_listing(stdout: str) -> List[Server]:
    """Servers from a `hermes mcp list --json` document, tools and all."""
    data = json.loads(stdout)
    return list(data.get("servers", []))


def parse_hermes_text_listing(stdout: str) -> List[Server]:
    """Servers from the `hermes mcp list` text table.

    The table's Tools column reads `all`, never the tool names — so `tools_known` is False
    and a caller must not read the empty list as "this server exposes nothing".
    """
    servers = []
    for line in stdout.splitlines():
        line = line.strip()
        if (not line or line.startswith("MCP Servers") or line.startswith("─")
            or line.startswith("Name")):
            continue
        parts = line.split()
        if len(parts) >= 4 and ("enabled" in line or "disabled" in line):
            servers.append({"name": parts[0], "tools": [], "tools_known": False,
                            "enabled": "disabled" not in line})
    return servers


# `<name>: <command or url> - <marker> <phrase>[ — detail]`, e.g.
#   `rider: http://127.0.0.1:64482/stream (HTTP) - ✔ Connected`
#   `plugin:github:github: https://api.githubcopilot.com/mcp/ (HTTP) - ✘ Failed to connect — …`
# The marker glyph is what separates a server line from the `Checking MCP server health…`
# banner, and `<detail>` is greedy so a command containing " - " cannot swallow the status.
_CLAUDE_LINE = re.compile(r"^(?P<name>\S.*?): (?P<detail>.*) - (?P<marker>[✔✘!⏸])\s*"
                          r"(?P<phrase>.+)$")
_URL = re.compile(r"^(?P<url>https?://\S+)")


def parse_claude_listing(stdout: str) -> List[Server]:
    """Servers from `claude mcp list`: a name, maybe a URL, and the host's own status phrase.

    No `claude mcp list` mode prints tool names, so `tools_known` is False for every server.
    The phrase is kept verbatim — `tool_descriptions.server_status` owns what it means.
    """
    servers = []
    for line in stdout.splitlines():
        match = _CLAUDE_LINE.match(line.rstrip())
        if not match:
            continue
        server: Server = {"name": match.group("name"), "tools": [], "tools_known": False,
                          "host_status": match.group("phrase").split(" — ")[0].strip()}
        url = _URL.match(match.group("detail").strip())
        if url:
            server["url"] = url.group("url")
        servers.append(server)
    return servers


# `  ✓ Tools discovered: 12`, then a blank line, then `    <name>   <description>` per tool.
_TEST_HEADER = re.compile(r"^\s*✓\s*Tools discovered:\s*\d+\s*$")
_TEST_TOOL = re.compile(r"^\s{3,}(?P<name>\S+)\s{2,}(?P<description>\S.*)$")


def parse_hermes_test_tools(stdout: str) -> Optional[List[Server]]:
    """Tools from one `hermes mcp test <server>` run, or None when it did not answer.

    None and `[]` are different answers and the caller must not conflate them: the command
    exits 0 whether it connected or printed `✗ Server '<name>' not found in config`, so only
    the `Tools discovered` header distinguishes "enumerated nothing" from "never enumerated".
    Descriptions arrive truncated with an ellipsis; the catalog outranks them anyway.
    """
    lines = stdout.splitlines()
    for position, line in enumerate(lines):
        if not _TEST_HEADER.match(line):
            continue
        tools = []
        for entry in lines[position + 1:]:
            if not entry.strip():
                continue
            match = _TEST_TOOL.match(entry)
            if not match:
                break
            tools.append({"name": match.group("name"),
                          "description": match.group("description").strip()})
        return tools
    return None


def enrich_with_hermes_test(servers: List[Server],
                            run: Optional[Callable[[List[str]], Outcome]] = None) -> List[str]:
    """Ask `hermes mcp test` for the tools of every server the listing left unenumerated.

    One subprocess per server, so callers gate it behind an opt-in. A server it cannot test —
    one hermes does not have in its config, such as a plugin- or connector-provided server —
    keeps `tools_known` False and is named in the returned notes.
    """
    run = run or run_cli
    unanswered = []
    for server in servers:
        if server.get("tools_known", True):
            continue
        name = server.get("name", "")
        code, stdout, _ = run(["hermes", "mcp", "test", name])
        tools = parse_hermes_test_tools(stdout) if code is not None else None
        if tools is None:
            unanswered.append(name)
            continue
        server["tools"] = tools
        server["tools_known"] = True
    if unanswered:
        return [f"hermes mcp test could not enumerate: {', '.join(unanswered)}"]
    return []


def carries_tool_detail(servers: List[Server]) -> bool:
    """Whether this listing named any server's tools at all.

    False means a server the listing does not mention is not evidence of removal: the listing
    may be a different host's, or the same host answering without tool detail (issue #188).
    """
    return any(server.get("tools_known", True) for server in servers)


# ── asking the hosts, in order ──────────────────────────────────────────────

class HostSource(NamedTuple):
    """One host CLI invocation and the parser for what it prints."""

    host: str
    label: str
    argv: Tuple[str, ...]
    parse: Callable[[str], List[Server]]


SOURCES = (
    HostSource(HERMES, "hermes mcp list --json",
               ("hermes", "mcp", "list", "--json"), parse_hermes_json_listing),
    HostSource(CLAUDE, "claude mcp list",
               ("claude", "mcp", "list"), parse_claude_listing),
    HostSource(HERMES, "hermes mcp list",
               ("hermes", "mcp", "list"), parse_hermes_text_listing),
)


class Listing(NamedTuple):
    """What discovery found: the servers, the source that answered, and who did not."""

    servers: List[Server]
    label: str
    notes: List[str]


def _refusal_line(text: str) -> str:
    """The last non-blank line of a CLI's stderr — argparse prints usage first, the error last.

    Measured: `hermes mcp list --json` answers with two lines of usage and then
    `error: unrecognized arguments: --json`, the only one worth showing a human.
    """
    for line in reversed(text.splitlines()):
        if line.strip():
            return line.strip()
    return ""


def run_cli(argv: List[str]) -> Outcome:
    """Run a host CLI. A None exit code means it never ran, and the note says why."""
    try:
        proc = subprocess.run(list(argv), capture_output=True, text=True,
                              timeout=TIMEOUT_SECONDS, check=False)
    except FileNotFoundError:
        return None, "", "not installed"
    except subprocess.TimeoutExpired:
        return None, "", f"timed out after {TIMEOUT_SECONDS}s"
    except OSError as exc:
        return None, "", f"could not run: {exc}"
    return proc.returncode, proc.stdout, _refusal_line(proc.stderr)


def discover(host: Optional[str] = None,
             run: Optional[Callable[[List[str]], Outcome]] = None) -> Listing:
    """The first source that lists a server wins; every source that did not gets a note.

    `host` restricts the chain to one CLI. An empty `servers` is a refusal, not an empty
    host: a host with no MCP servers has nothing to index either way.
    """
    run = run or run_cli
    notes: List[str] = []
    for source in SOURCES:
        if host and source.host != host:
            continue
        code, stdout, note = run(list(source.argv))
        if code is None:
            notes.append(f"{source.label}: {note}")
            continue
        if code != 0:
            notes.append(f"{source.label}: exit {code}" + (f" ({note})" if note else ""))
            continue
        try:
            servers = source.parse(stdout)
        except ValueError as exc:
            notes.append(f"{source.label}: unreadable output ({exc})")
            continue
        if servers:
            return Listing(servers, source.label, notes)
        notes.append(f"{source.label}: listed no servers")
    return Listing([], "", notes)
