#!/usr/bin/env python3
"""What an MCP tool is for, and whether its server is talking.

Three sources of a tool's tags and intent, in descending authority: the mcp catalog
(`features/<stack>/mcp/<server>/tools.json`), a human (`mcp-index tag`/`intent`), and the name
heuristics below. Plus the status vocabulary for a server that reported no tools — ADR-0014
decision 7. Imported by mcp_index.py, which owns the index file and the CLI.
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any, Dict, List, Optional

# ── Tag taxonomy (loaded from features/common/mcp-tags.json or fallback) ──
DEFAULT_TAXONOMY: dict[str, Any] = {
    "categories": {
        "language": {
            "tags": ["csharp", "typescript", "javascript", "python", "sql", "css", "html"],
        },
        "action": {
            "tags": [
                "navigation", "diagnostic", "build", "run", "refactoring",
                "search", "read", "write", "terminal",
            ],
        },
        "domain": {
            "tags": [
                "database", "tracing", "opentelemetry", "browser",
                "dotnet", "semantic", "files",
            ],
        },
        "meta": {"tags": ["batch", "slow", "unsafe"]},
    },
}


def load_taxonomy(fw_root: Optional[Path]) -> dict[str, Any]:
    """Load the tag taxonomy, falling back to the built-in default."""
    if fw_root:
        tax_path = Path(fw_root) / "features" / "common" / "mcp-tags.json"
        if tax_path.exists():
            return json.loads(tax_path.read_text(encoding="utf-8"))
    return DEFAULT_TAXONOMY


def all_valid_tags(taxonomy: dict[str, Any]) -> set[str]:
    """Flatten all valid tags from the taxonomy."""
    return {t for cat in taxonomy["categories"].values() for t in cat["tags"]}


# ── Auto-tagging heuristics ──────────────────────────────────────────────────

# The action a tool name's *first* `_`/`-` token names, matched whole (so `budget` is not `get`).
# Actions only, never a technology (#171). These verbs led the 59 names the 2026-10-18 host-tools
# smoke left `general`.
_READ, _WRITE, _RUN = ("read",), ("write",), ("run",)
LEADING_VERBS = {
    **dict.fromkeys(("get", "list", "fetch", "download", "export", "whoami", "describe"), _READ),
    **dict.fromkeys(("query", "search"), ("read", "search")),
    **dict.fromkeys(("create", "update", "delete", "add", "upload", "set", "patch"), _WRITE),
    **dict.fromkeys(("cancel", "pause", "resume", "start", "stop"), _RUN),
}

def auto_tags(tool_name: str, server_name: str, valid_tags: set[str]) -> list[str]:
    """Assign tags from tool-name heuristics, or ["general"] when none match."""
    name = tool_name.lower()
    tags: set[str] = set()

    # Server-level heuristics
    if "playwright" in server_name.lower() or "browser" in server_name.lower():
        tags.add("browser")

    # Name-based heuristics (order matters — more specific first).
    # A name substring may infer an *action* (build, search, read, ...); a *technology* only
    # through a tight, unambiguous alias — see issue #171 for the false positives that drove
    # the split, and docs/changelog/0.51.0-a-build-is-not-a-dotnet.md for the two that stayed.
    if any(kw in name for kw in ("database", "schema", "db")):
        tags.add("database")
    if "sql" in name:
        tags.update(["sql", "database"])
    if "build" in name:
        tags.add("build")
    if "run" in name or "execute" in name:
        tags.add("run")
    if "search" in name or "find" in name:
        tags.add("search")
    if any(kw in name for kw in ("refactor", "rename", "reformat", "move_type")):
        tags.add("refactoring")
    if "symbol" in name:
        tags.update(["semantic", "search"])
    if any(kw in name for kw in ("problem", "error", "diagnostic")):
        tags.add("diagnostic")
    if "span" in name or "otel" in name or "service_map" in name:
        tags.update(["tracing", "opentelemetry"])
    if any(kw in name for kw in ("browser", "navigate", "screenshot", "click")):
        tags.add("browser")
    if any(kw in name for kw in ("file", "directory", "tree", "glob")):
        tags.add("files")
    if "editor" in name or "open_" in name:
        tags.add("navigation")
    if "navigate" in name:
        tags.add("navigation")
    if "read" in name:
        tags.add("read")
    if any(kw in name for kw in ("write", "create", "patch", "replace")):
        tags.add("write")
    if "terminal" in name or "shell" in name:
        tags.add("terminal")
    tags.update(LEADING_VERBS.get(re.split(r"[_-]", name, maxsplit=1)[0], ()))

    valid = sorted(tags & valid_tags)
    return valid if valid else ["general"]


# ── The mcp catalog: curation outranks the heuristics ────────────────────────

CATALOG, HEURISTIC, MANUAL = "catalog", "heuristic", "manual"

Catalog = Dict[str, Dict[str, Dict[str, Any]]]


def load_catalog(fw_root: Optional[Path]) -> Catalog:
    """Curated tool entries per server name.

    From every installable `features/*/mcp/<server>/tools.json` (with its `meta.json`), then
    every describe-only `features/*/mcp-catalog/<slug>/tools.json`: servers a host exposes but
    ai-badger never installs, so nothing that offers or delivers servers reads that dir. Stacks
    in directory order, last writer wins on a duplicate tool, and an installable entry outranks a
    describe-only one. An unreachable framework yields `{}` and the heuristics carry the index.
    """
    if fw_root is None:
        return {}
    catalog: Catalog = {}
    for pattern, overwrite in (("features/*/mcp/*/tools.json", True),
                               ("features/*/mcp-catalog/*/tools.json", False)):
        for path in sorted(Path(fw_root).glob(pattern)):
            if overwrite and not (path.parent / "meta.json").is_file():
                continue
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            tools = catalog.setdefault(data.get("server") or path.parent.name, {})
            for tool in data.get("tools", []):
                if tool.get("name") and (overwrite or tool["name"] not in tools):
                    tools[tool["name"]] = tool
    return catalog


# A host decorates a server name with where it routes the server from: `claude mcp list` prints
# `plugin:<plugin>:<server>` for a plugin-provided server and `claude.ai <Name>` for a connector,
# where `hermes mcp list` prints the bare name. Captured 2026-07-30: plugin:github:github,
# plugin:microsoft-docs:microsoft-learn, plugin:dotnet-claude-kit:cwm-roslyn-navigator,
# claude.ai Google Drive, rider. Issue #203.
_PLUGIN_PREFIX = re.compile(r"^plugin:[^:]+:(?P<server>.+)$")
_CONNECTOR_PREFIX = re.compile(r"^claude\.ai (?P<server>.+)$")


def catalog_keys(listing_name: str) -> List[str]:
    """The catalog keys one host listing name may match, most specific first.

    The decorated name leads, so a catalog naming a plugin explicitly outranks the bare server:
    two plugins may ship same-named servers, making the prefix identity rather than noise (#203).
    """
    keys = [listing_name]
    for pattern in (_PLUGIN_PREFIX, _CONNECTOR_PREFIX):
        match = pattern.match(listing_name)
        if not match:
            continue
        server = match.group("server")
        keys.append(server)
        slug = server.lower().replace(" ", "-")
        if slug != server:
            keys.append(slug)
    return keys


def catalog_tools(catalog: Catalog, server: str) -> Dict[str, Dict[str, Any]]:
    """The curated entries for a host listing name: the first `catalog_keys` match, else none.

    Matching widens the *listing* name only — a bare `foo` never borrows `plugin:a:foo`'s curation.
    """
    for key in catalog_keys(server):
        curated = catalog.get(key)
        if curated:
            return curated
    return {}


def describe_tool(server: str, name: str, description: str,
                  catalog: Catalog, valid_tags: set[str]) -> dict[str, Any]:
    """Tags, intent and origin for one tool — the catalog when it knows it, else heuristics."""
    curated = catalog_tools(catalog, server).get(name)
    if curated:
        return {
            "tags": list(curated.get("tags") or auto_tags(name, server, valid_tags)),
            "intent": curated["intent"],
            "origin": CATALOG,
        }
    return {
        "tags": auto_tags(name, server, valid_tags),
        "intent": heuristic_intent(name, description),
        "origin": HEURISTIC,
    }


# schemas/mcp-tools.schema.json bounds `intent`; a server's own description is often longer.
INTENT_MIN, INTENT_MAX = 10, 200


def heuristic_intent(name: str, description: str) -> str:
    """A server's description as an intent the schema accepts: one line, cut at a word, or a TODO."""
    text = " ".join(str(description or "").split())
    if len(text) > INTENT_MAX:
        text = text[:INTENT_MAX - 1].rsplit(" ", 1)[0].rstrip(" ,;:") + "…"
    return text if len(text) >= INTENT_MIN else PLACEHOLDER_INTENT.format(name=name)


# What `heuristic_intent` writes when it has nothing to say; `validate` refuses it.
PLACEHOLDER_INTENT = "TODO: describe what {name} does"
_PLACEHOLDER = re.compile(re.escape(PLACEHOLDER_INTENT).replace(re.escape("{name}"), ".+"))


def is_placeholder(intent: str) -> bool:
    """Whether an intent is the unwritten placeholder rather than a description."""
    return bool(_PLACEHOLDER.fullmatch(str(intent or "")))


# ── Server status: opposite remedies must not share one silence ──────────────

OK, DISABLED, EMPTY, UNKNOWN, ABSENT = "ok", "disabled", "empty", "unknown", "absent"
UNREACHABLE, UNAUTHENTICATED = "unreachable", "unauthenticated"
PENDING_APPROVAL = "pending_approval"

ALL_STATUSES = {OK, DISABLED, EMPTY, UNKNOWN, ABSENT,
                UNREACHABLE, UNAUTHENTICATED, PENDING_APPROVAL}

# `claude mcp list`'s phrase -> our status. Only phrases seen first-hand are here: the first
# three were measured on a 2026-07 install, the fourth is quoted in `claude mcp list --help`
# ("shown as ⏸ Pending approval and not connected to"). Its other documented phrases stay out
# until someone captures them — an unmapped phrase degrades to `unknown` (issue #188).
HOST_STATUS_PHRASES = {
    "connected": None,  # reachable, but the listing still carries no tool detail
    "needs authentication": UNAUTHENTICATED,
    "failed to connect": UNREACHABLE,
    "pending approval": PENDING_APPROVAL,
}


def server_status(server: dict[str, Any]) -> str:
    """The status a host listing actually supports for one server (ADR-0014 decision 7).

    A `host_status` phrase from `claude mcp list` outranks the inference, because it reports a
    connection failure distinctly from an empty tool list; every other listing has to infer.
    """
    phrase = str(server.get("host_status") or "").strip().lower()
    reported = HOST_STATUS_PHRASES.get(phrase)
    if reported:
        return reported
    if server.get("enabled") is False:
        return DISABLED
    if not server.get("tools_known", True):
        return UNKNOWN
    return OK if server.get("tools") else EMPTY
