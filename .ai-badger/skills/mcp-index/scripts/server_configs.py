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
  - synced:        `~/.claude/plugins/synced/<org>_<user>/` claude.ai-synced plugins, read the same
                   way from the manifest's `<plugin>~g<generation>` dir; installed plugins win
A bare name takes the first scope above that has it. `claude.ai <Name>` connectors are OAuth and
host-managed, and managed enterprise config is not read: both resolve to no spec.
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any, Dict, NamedTuple, Optional, Set, Tuple

HOST_MANAGED = "host-managed connector (OAuth)"
NO_CONFIG = "no config"
NOT_APPROVED = "not run: project server not approved in Claude Code"
CONNECTOR_PREFIX = "claude.ai "
PLUGIN_ROOT_VAR = "CLAUDE_PLUGIN_ROOT"
PLUGIN_DATA_VAR = "CLAUDE_PLUGIN_DATA"
SYNCED_DATA_SUFFIX = "synced"
GENERATION_MARK = "~g"

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


def _substitute(value: Any, plugin_vars: Dict[str, str]) -> Any:
    """`${CLAUDE_PLUGIN_ROOT}` and `${CLAUDE_PLUGIN_DATA}` expanded through strings, lists, maps."""
    if isinstance(value, str):
        for name, text in plugin_vars.items():
            value = value.replace("${" + name + "}", text)
        return value
    if isinstance(value, list):
        return [_substitute(item, plugin_vars) for item in value]
    if isinstance(value, dict):
        return {key: _substitute(item, plugin_vars) for key, item in value.items()}
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


def _data_dir(home: Path, label: str) -> str:
    """`~/.claude/plugins/data/<id>`, never created here.

    Claude Code's id is `<plugin>@<marketplace>` for an installed plugin and `<plugin>-synced` for
    a claude.ai-synced one, with every character outside [A-Za-z0-9_-] turned into `-`:
    `ai-badger@ai-badger` → `ai-badger-ai-badger`, `formatter@acme.tools` → `formatter-acme-tools`.
    """
    return str(home / ".claude" / "plugins" / "data" / re.sub(r"[^A-Za-z0-9_-]", "-", label))


def _add_plugin(configs: Dict[str, Spec], plugin: str, root: str, data: str) -> None:
    """The plugin's servers under `plugin:<plugin>:<server>`; a name already present stays.

    The host launches plugin servers with CLAUDE_PLUGIN_ROOT and CLAUDE_PLUGIN_DATA set; the
    config's own env wins on a clash.
    """
    plugin_vars = {PLUGIN_ROOT_VAR: root, PLUGIN_DATA_VAR: data}
    for name, spec in _plugin_servers(Path(root)).items():
        spec = _substitute(spec, plugin_vars)
        env = spec.get("env")
        spec["env"] = {**plugin_vars, **(env if isinstance(env, dict) else {})}
        configs.setdefault(f"plugin:{plugin}:{name}", spec)


def _installed_roots(home: Path) -> Dict[str, Tuple[str, str]]:
    """`installed_plugins.json`: plugin name (marketplace dropped) → (first install path, data dir)."""
    installed = _read_json(home / ".claude" / "plugins" / "installed_plugins.json")
    plugins = installed.get("plugins") if isinstance(installed, dict) else None
    roots: Dict[str, Tuple[str, str]] = {}
    for key, installs in (plugins or {}).items() if isinstance(plugins, dict) else ():
        install = next((i for i in installs or [] if isinstance(i, dict)
                        and i.get("installPath")), None) if isinstance(installs, list) else None
        if install is not None:
            roots.setdefault(str(key).split("@", 1)[0], (
                str(install["installPath"]), _data_dir(home, str(key))))
    return roots


def _generation(directory: Path, plugin: str) -> Optional[int]:
    """N for a `<plugin>~gN` dir, else None."""
    prefix = plugin + GENERATION_MARK
    suffix = directory.name[len(prefix):]
    return int(suffix) if directory.name.startswith(prefix) and suffix.isdigit() else None


def _plain_name(name: str) -> bool:
    """A single path segment: the synced manifest is untrusted, so no `..`, separator or root."""
    return (bool(name) and name not in (".", "..") and name == Path(name).name
            and os.sep not in name and not (os.altsep and os.altsep in name))


def _inside(account: Path, directory: Path) -> bool:
    """A real dir that, symlinks resolved, still sits inside the account dir."""
    try:
        return directory.is_dir() and directory.resolve().is_relative_to(account.resolve())
    except (OSError, RuntimeError):
        return False


def _synced_root(account: Path, plugin: str, generation: Any) -> Optional[Path]:
    """The manifest's `<plugin>~g<generation>` dir, else the highest `~gN`, else bare `<plugin>`.

    Every candidate must resolve inside `account`; one that escapes it is skipped.
    """
    if not _plain_name(plugin):
        return None
    if isinstance(generation, int) and not isinstance(generation, bool):
        named = account / f"{plugin}{GENERATION_MARK}{generation}"
        if _inside(account, named):
            return named
    generations = [(n, d) for d in account.iterdir() if _inside(account, d)
                   for n in [_generation(d, plugin)] if n is not None]
    if generations:
        return max(generations)[1]
    bare = account / plugin
    return bare if _inside(account, bare) else None


def _synced_roots(home: Path) -> Dict[str, Tuple[str, str]]:
    """claude.ai-synced plugins (`synced/<org>_<user>/`): plugin name → its live generation dir.

    `manifest.json` names each plugin and the generation in use; a dir with no manifest entry
    still counts, by its own name. Listings key these `plugin:<plugin>:<server>`, no marketplace.
    """
    roots: Dict[str, Tuple[str, str]] = {}
    synced = home / ".claude" / "plugins" / "synced"
    try:
        accounts = sorted(d for d in synced.iterdir() if d.is_dir())
    except OSError:
        return roots
    for account in accounts:
        manifest = _read_json(account / "manifest.json")
        entries = manifest.get("plugins") if isinstance(manifest, dict) else None
        generations = {str(e["name"]): e.get("generation") for e in entries or []
                       if isinstance(e, dict) and e.get("name")} if isinstance(entries, list) else {}
        names = set(generations) | {d.name.split(GENERATION_MARK, 1)[0]
                                    for d in account.iterdir() if d.is_dir()
                                    and not d.name.startswith(".")}
        for plugin in sorted(names):
            root = _synced_root(account, plugin, generations.get(plugin))
            if root is not None:
                roots.setdefault(plugin, (str(root), _data_dir(
                    home, f"{plugin}-{SYNCED_DATA_SUFFIX}")))
    return roots


def _plugins(home: Path) -> Dict[str, Spec]:
    """Every plugin's servers keyed `plugin:<plugin>:<server>`, root expanded.

    Installed plugins go first, so on a name clash the one in `installed_plugins.json` wins over
    a claude.ai-synced plugin of the same name.
    """
    configs: Dict[str, Spec] = {}
    for roots in (_installed_roots(home), _synced_roots(home)):
        for plugin, (root, data) in roots.items():
            _add_plugin(configs, plugin, root, data)
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
