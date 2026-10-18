#!/usr/bin/env python3
"""Each MCP server's command or URL, read from the files Claude Code itself reads.

`claude mcp list` prints names only, so `--discover` resolves them here, in one pass:
  - local scope:   `~/.claude.json` `projects[<target or its nearest parent>].mcpServers`
  - project scope: `<target>/.mcp.json`, only servers the user approved — named in
                   `enabledMcpjsonServers` or covered by `enableAllProjectMcpServers`, and not in
                   `disabledMcpjsonServers` — read from the `~/.claude.json` project entry,
                   `<target>/.claude/settings{,.local}.json` and `~/.claude/settings.json`
  - user scope:    `~/.claude.json` `mcpServers`
  - plugins:       `~/.claude/plugins/installed_plugins.json` → each install path's `.mcp.json`
                   or `.claude-plugin/plugin.json` `mcpServers`, named `plugin:<plugin>:<server>`
A bare name takes the first scope above that has it. `claude.ai <Name>` connectors are OAuth and
host-managed, and managed enterprise config is not read: both resolve to no spec.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, NamedTuple, Optional, Set, Tuple

HOST_MANAGED = "host-managed connector (OAuth)"
NO_CONFIG = "no config"
NOT_APPROVED = "not run: project server not approved in Claude Code"
CONNECTOR_PREFIX = "claude.ai "
PLUGIN_ROOT_VAR = "CLAUDE_PLUGIN_ROOT"

Spec = Dict[str, Any]


class Configs(NamedTuple):
    """Runnable specs by listing name, and the project servers awaiting the user's approval."""

    specs: Dict[str, Spec]
    unapproved: Set[str]


def _read_json(path: Path) -> Any:
    """The parsed file, or None when it is absent or unreadable — a missing scope is not fatal."""
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _servers(data: Any) -> Dict[str, Spec]:
    """`{"mcpServers": {...}}` or a bare `{name: spec}` map; anything else is empty."""
    if isinstance(data, dict) and isinstance(data.get("mcpServers"), dict):
        data = data["mcpServers"]
    if not isinstance(data, dict):
        return {}
    return {str(k): v for k, v in data.items() if isinstance(v, dict)}


def _resolved(path: Path) -> Path:
    try:
        return path.expanduser().resolve()
    except (OSError, RuntimeError):
        return path


def _project_entry(claude_json: Any, target: Path) -> Dict[str, Any]:
    """The `projects[...]` entry for the target, or for its nearest parent (a worktree)."""
    projects = claude_json.get("projects") if isinstance(claude_json, dict) else None
    if not isinstance(projects, dict):
        return {}
    here = _resolved(target)
    best: Tuple[int, Dict[str, Any]] = (-1, {})
    for key, entry in projects.items():
        root = _resolved(Path(key))
        if isinstance(entry, dict) and (root == here or root in here.parents):
            depth = len(root.parts)
            if depth > best[0]:
                best = (depth, entry)
    return best[1]


def _approval(entry: Dict[str, Any], target: Path, home: Path
              ) -> Tuple[bool, Set[str], Set[str]]:
    """(enable all, enabled names, disabled names) across every file Claude Code reads them from."""
    sources = [entry] + [_read_json(path) for path in (
        target / ".claude" / "settings.json", target / ".claude" / "settings.local.json",
        home / ".claude" / "settings.json")]
    every, enabled, disabled = False, set(), set()
    for source in sources:
        if not isinstance(source, dict):
            continue
        every = every or source.get("enableAllProjectMcpServers") is True
        enabled.update(source.get("enabledMcpjsonServers") or [])
        disabled.update(source.get("disabledMcpjsonServers") or [])
    return every, enabled, disabled


def _admitted(servers: Dict[str, Spec], approval: Tuple[bool, Set[str], Set[str]]
              ) -> Tuple[Dict[str, Spec], Set[str]]:
    """(approved project servers, the names still awaiting approval); disabled ones are neither."""
    every, enabled, disabled = approval
    admitted = {name: spec for name, spec in servers.items()
                if name not in disabled and (every or name in enabled)}
    waiting = {name for name in servers if name not in disabled and name not in admitted}
    return admitted, waiting


def _substitute(value: Any, root: str) -> Any:
    """`${CLAUDE_PLUGIN_ROOT}` replaced by the install path, through strings, lists and maps."""
    if isinstance(value, str):
        return value.replace("${" + PLUGIN_ROOT_VAR + "}", root)
    if isinstance(value, list):
        return [_substitute(item, root) for item in value]
    if isinstance(value, dict):
        return {key: _substitute(item, root) for key, item in value.items()}
    return value


def _plugin_servers(root: Path) -> Dict[str, Spec]:
    """A plugin's servers: its `.mcp.json`, then `plugin.json` `mcpServers` (inline or a path)."""
    servers = _servers(_read_json(root / ".mcp.json"))
    manifest = _read_json(root / ".claude-plugin" / "plugin.json")
    declared = manifest.get("mcpServers") if isinstance(manifest, dict) else None
    if isinstance(declared, str):
        declared = _read_json(root / declared)
    for name, spec in _servers(declared).items():
        servers.setdefault(name, spec)
    return servers


def _plugins(home: Path) -> Dict[str, Spec]:
    """Every installed plugin's servers keyed `plugin:<plugin>:<server>`, root expanded."""
    installed = _read_json(home / ".claude" / "plugins" / "installed_plugins.json")
    plugins = installed.get("plugins") if isinstance(installed, dict) else None
    configs: Dict[str, Spec] = {}
    for key, installs in (plugins or {}).items():
        plugin = str(key).split("@", 1)[0]
        install = next((i for i in installs or [] if isinstance(i, dict)
                        and i.get("installPath")), None) if isinstance(installs, list) else None
        if install is None:
            continue
        root = str(install["installPath"])
        for name, spec in _plugin_servers(Path(root)).items():
            spec = _substitute(spec, root)
            spec["env"] = {**(spec.get("env") or {}), PLUGIN_ROOT_VAR: root}
            configs.setdefault(f"plugin:{plugin}:{name}", spec)
    return configs


def load(target: Path, home: Path) -> Configs:
    """Every server Claude Code would know for `target`, by the name its listing prints."""
    claude_json = _read_json(home / ".claude.json")
    entry = _project_entry(claude_json, target)
    project, waiting = _admitted(_servers(_read_json(target / ".mcp.json")),
                                 _approval(entry, target, home))
    scopes = (
        _servers(entry.get("mcpServers")),
        project,
        _servers(claude_json.get("mcpServers") if isinstance(claude_json, dict) else None),
        _plugins(home),
    )
    specs: Dict[str, Spec] = {}
    for scope in scopes:
        for name, spec in scope.items():
            specs.setdefault(name, dict(spec))
    return Configs(specs, waiting - set(specs))


def lookup(configs: Configs, name: str) -> Tuple[Optional[Spec], str]:
    """(spec, "") for a server we can run, else (None, why not)."""
    if name.startswith(CONNECTOR_PREFIX):
        return None, HOST_MANAGED
    spec = configs.specs.get(name)
    if spec is not None:
        return spec, ""
    return None, NOT_APPROVED if name in configs.unapproved else NO_CONFIG
