"""MCP index status notices for Hermes Agent.

Advisory only: these hooks report a missing .ai-badger/mcp-tools.json (or a not-yet-
migrated .ai-badger/mcp-tools.yaml) and never execute anything resolved from the
project tree.
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Any, Optional

logger = logging.getLogger("mcp_index_hook")

_MISSING_INDEX_NOTICE = (
    "MCP index not found at %s/.ai-badger/mcp-tools.json; run the mcp-index skill to build it"
)


def _find_project_root(cwd: Optional[str] = None) -> Optional[Path]:
    """Find the project root by looking for .ai-badger/ directory."""
    start = Path(cwd) if cwd else Path.cwd()
    for d in [start, *start.parents]:
        if (d / ".ai-badger").is_dir():
            return d
    return None


def _has_mcp_index(project_root: Path) -> bool:
    """Check if an MCP tool index exists, current (.json) or not-yet-migrated (.yaml).

    A curated project mid-migration (issue #145) must never be told to run `init` —
    `mcp_index.py`'s own reader is dual-format for the same reason.
    """
    aib = project_root / ".ai-badger"
    return (aib / "mcp-tools.json").exists() or (aib / "mcp-tools.yaml").exists()


def _notice_if_index_missing(cwd: Optional[str]) -> None:
    """Log a notice when the project has no MCP tool index."""
    project_root = _find_project_root(cwd)
    if project_root is None or _has_mcp_index(project_root):
        return
    logger.info(_MISSING_INDEX_NOTICE, project_root)


def on_session_start(ctx: Any = None) -> None:
    """Report a missing MCP index at session start."""
    try:
        _notice_if_index_missing(getattr(ctx, "cwd", None) if ctx else None)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.debug("MCP index check skipped due to error", exc_info=True)


def post_tool_call(tool_name: str, args: Any = None, result: Any = None,
                   duration_ms: float = 0, ctx: Any = None) -> None:
    """Report a missing MCP index when an MCP tool is used."""
    try:
        is_mcp = bool(tool_name) and "mcp" in tool_name.lower()
        if not is_mcp and isinstance(args, dict) and "command" in args:
            is_mcp = "mcp" in str(args.get("command", "")).lower()
        if not is_mcp:
            return
        _notice_if_index_missing(getattr(ctx, "cwd", None) if ctx else None)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.debug("MCP index check skipped due to error", exc_info=True)
