"""Which scope a declared MCP server belongs to.

The catalog's per-server ``scope`` is the framework's default; ``config.mcp.scope`` is the
project's own decision about its own repository, and it wins in either direction (ADR-0044).
One rule, read by the scaffold's three project files and by the per-agent adjustments alike.
"""
from __future__ import annotations

from typing import Any, Dict, Tuple

PROJECT = "project"
USER = "user"


def scope_overrides(config: Dict[str, Any]) -> Dict[str, str]:
    """The project's own scope decision per server name (``config.mcp.scope``).

    An absent block means the catalog decides. A name whose value is neither scope is
    dropped here: the schema refuses it, and an unreadable config must not widen a scope.
    """
    scope = (config.get("mcp") or {}).get("scope") or {}
    if not isinstance(scope, dict):
        return {}
    return {name: value for name, value in scope.items()
            if isinstance(name, str) and name and value in (PROJECT, USER)}


def effective_scope(name: str, server: Dict[str, Any], overrides: Dict[str, str]) -> str:
    """The scope one server is written under: the project's, else the catalog's."""
    return overrides.get(name, server.get("scope", PROJECT))


def split_by_scope(
    servers: Dict[str, Dict[str, Any]], overrides: Dict[str, str]
) -> Tuple[Dict[str, Dict[str, Any]], Dict[str, Dict[str, Any]]]:
    """Split *servers* into (project_servers, user_servers)."""
    project = {}  # type: Dict[str, Dict[str, Any]]
    user = {}  # type: Dict[str, Dict[str, Any]]
    for name, srv in servers.items():
        if effective_scope(name, srv, overrides) == USER:
            user[name] = srv
        else:
            project[name] = srv
    return project, user
