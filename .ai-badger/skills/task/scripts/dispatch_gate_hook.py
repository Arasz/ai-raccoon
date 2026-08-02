#!/usr/bin/env python3
"""PreToolUse gate on Agent dispatches: no subagent may inherit the session model by accident.

Denies rather than injecting a default via `updatedInput`: a per-invocation `model` outranks
the agent definition's `model:` frontmatter, so injecting would override the persona lanes that
are the framework's preferred defaulting layer. A denial is shown to the model, which retries.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "dispatch_gate_hook"
DISPATCH_TOOLS = ("Agent",)
FRONTMATTER_FENCE = "---"
MODEL_KEY = "model:"

DENY_REASON = (
    "Dispatch declares no model and subagent type '{subagent_type}' has no model lane. "
    "Pass model explicitly ('haiku' for mechanical work, 'sonnet' for spec-driven work, "
    "'opus' for derivation) — see .ai-badger/delegation.md."
)


def _debug(event: str, project: Optional[str] = None, **fields: Any) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def project_root(payload: Dict[str, Any]) -> Optional[str]:
    """`$CLAUDE_PROJECT_DIR` first, else the payload's own `cwd` — the shared hook convention."""
    if debug_log is not None:
        return debug_log.resolve_project_root(payload)
    return os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or None


def lane_file(root: Optional[str], subagent_type: str) -> Optional[Path]:
    """The `.claude/agents/<subagent_type>.md` nearest `root`, else the user-level one, else None.

    A namespaced or built-in subagent type owns no such file, so it never resolves one.
    """
    if not root or "/" in subagent_type or os.sep in subagent_type or subagent_type == "..":
        return None
    try:
        here = Path(root).resolve()
    except OSError:  # pragma: no cover - an unresolvable cwd is not a reason to block
        return None
    searched = [directory / ".claude" for directory in (here, *here.parents)]
    searched.append(Path.home() / ".claude")
    for claude_dir in searched:
        candidate = claude_dir / "agents" / f"{subagent_type}.md"
        if candidate.is_file():
            return candidate
    return None


def declares_model(path: Path) -> bool:
    """True when the agent file's YAML frontmatter carries a non-empty top-level `model:` key."""
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:  # pragma: no cover - an unreadable lane file declares nothing
        return False
    if not lines or lines[0].strip() != FRONTMATTER_FENCE:
        return False
    for line in lines[1:]:
        if line.strip() == FRONTMATTER_FENCE:
            return False
        if line.startswith(MODEL_KEY):
            return bool(line[len(MODEL_KEY):].strip())
    return False


def decide(payload: Dict[str, Any]) -> int:
    """Print a deny decision iff an Agent dispatch names no model and no lane covers it."""
    if not isinstance(payload, dict):
        return 0
    if (payload.get("tool_name") or payload.get("toolName")) not in DISPATCH_TOOLS:
        return 0

    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    if not isinstance(tool_input, dict):
        return 0
    subagent_type = tool_input.get("subagent_type")
    if not isinstance(subagent_type, str) or not subagent_type.strip():
        return 0

    root = project_root(payload)
    model = tool_input.get("model")
    if isinstance(model, str) and model.strip():
        _debug("allow", project=root, subagentType=subagent_type, why="explicit_model")
        return 0

    lane = lane_file(root, subagent_type)
    if lane is not None and declares_model(lane):
        _debug("allow", project=root, subagentType=subagent_type, why="lane_file")
        return 0

    _debug("deny", project=root, subagentType=subagent_type)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": DENY_REASON.format(subagent_type=subagent_type),
        }
    }))
    return 0


def main() -> int:
    """Read the hook payload from stdin. Fails open: a broken gate must never brick dispatch."""
    try:
        return decide(json.load(sys.stdin))
    except Exception:  # pylint: disable=broad-exception-caught
        return 0


if __name__ == "__main__":
    sys.exit(main())
