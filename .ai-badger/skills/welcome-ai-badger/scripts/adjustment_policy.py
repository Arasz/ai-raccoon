"""Resolve the limited-environment policy passed to agent adjustments."""
from __future__ import annotations

from typing import Any, Dict, Tuple


def instruction_only_adjustment_context(
    config: Dict[str, Any], tools: Dict[str, Any], install: bool,
    agent_name: str, suppress_hooks: bool,
) -> Tuple[Dict[str, Any], Dict[str, Any], bool]:
    """Return adjustment inputs with project hook and tool registrations suppressed."""
    if not suppress_hooks:
        return config, tools, install

    adjustment_config = dict(config)
    adjustment_config["agents"] = [agent for agent in config.get("agents", [])
                                   if agent != agent_name]
    adjustment_tools = {name: dict(tool, agents={}) for name, tool in tools.items()}
    return adjustment_config, adjustment_tools, False


def build_adjustment_context(
    root: Any, config: Dict[str, Any], feature_dir: Any, target_dir: Any,
    target: Any, install: bool, skills: list, prune: bool, personas: list,
    index: Dict[str, Any], mcp_servers: list, mcp_declarations: Any,
    mcp_declined: Any, tools: Dict[str, Any],
) -> Dict[str, Any]:
    """Assemble the stable context contract passed to one agent adjustment."""
    return {
        "framework_root": root, "config": config, "feature_dir": feature_dir,
        "target_dir": target_dir, "target": target, "install": install,
        "skills": skills, "prune": prune, "personas": personas, "index": index,
        "mcp_servers": mcp_servers, "mcp_declarations": mcp_declarations,
        "mcp_declined": mcp_declined, "tools": tools,
    }
