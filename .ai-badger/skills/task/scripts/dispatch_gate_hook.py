#!/usr/bin/env python3
"""PreToolUse gate on Agent dispatches: no subagent inherits the session model, and no
write-capable lane joins a parallel fan-out sharing one tree.

Denies rather than injecting a default via `updatedInput`: a per-invocation `model` outranks
the agent definition's `model:` frontmatter, so injecting would override the persona lanes that
are the framework's preferred defaulting layer. A denial is shown to the model, which retries.

The isolation half enforces prompting-rules.md's "dispatch using your agent tool's native
isolation", but only under this session's own fan-out, counted by dispatch_ledger. Read-only
lanes are exempt, derived from `disallowedTools` frontmatter rather than a persona list.

Why parallelism-only, why the machine-wide session count was cut, and why gating needs
positive proof that a lane writes: `docs/changelog/0.138.0-a-contract-with-no-gate-behind-it.md`.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))

# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members
try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

import dispatch_ledger  # pylint: disable=wrong-import-position

COMPONENT = "dispatch_gate_hook"
DISPATCH_TOOLS = ("Agent",)
FRONTMATTER_FENCE = "---"
MODEL_KEY = "model:"

DISALLOWED_KEY = "disallowedTools:"

# Mirrors the harness's file-touching tools. A hand-kept list with nothing to derive it from
# and nothing to compare it against — a conscious exception to derive-or-delete-the-list.
# Consumed with all(), so a MISSING entry widens the read-only exemption rather than
# narrowing it; tests/test_dispatch_gate_isolation.py pins that direction.
WRITE_TOOLS = ("Write", "Edit", "MultiEdit", "NotebookEdit")

DENY_REASON = (
    "Dispatch declares no model and subagent type '{subagent_type}' has no model lane. "
    "Pass model explicitly ('haiku' for mechanical work, 'sonnet' for spec-driven work, "
    "'opus' for derivation) — see .ai-badger/delegation.md."
)

ISOLATION_DENY_REASON = (
    "{lanes} agent lanes are live and this dispatch to write-capable '{subagent_type}' names "
    "no isolation, so it would share a tree — and a build output — with them. Pass "
    "isolation=\"worktree\". If these lanes were meant to run one after another, run them "
    "sequentially instead. See .ai-badger/skills/worktree-agent-isolation."
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


def is_read_only(path: Path) -> bool:
    """True when the lane's frontmatter disallows *every* tool that could touch a file.

    All of WRITE_TOOLS, not any: `architect` bans Edit/MultiEdit/NotebookEdit but keeps
    Write, and a lane that can still create a file is a writer. Only the single-line
    comma form of `disallowedTools:` is understood, which is what every lane here uses.
    """
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:  # pragma: no cover - an unreadable lane declares nothing
        return False
    if not lines or lines[0].strip() != FRONTMATTER_FENCE:
        return False
    for line in lines[1:]:
        if line.strip() == FRONTMATTER_FENCE:
            return False
        if line.startswith(DISALLOWED_KEY):
            declared = {tool.strip() for tool in line[len(DISALLOWED_KEY):].split(",")}
            return all(tool in declared for tool in WRITE_TOOLS)
    return False


def isolation_verdict(payload: Dict[str, Any], tool_input: Dict[str, Any],
                      root: Optional[str], subagent_type: str) -> Optional[str]:
    """The deny reason when this dispatch would join a fan-out unisolated, else None.

    Records only a dispatch that will really enter the shared tree: not an isolated lane
    (own worktree) and not a denied one (never starts). Gates only a lane a lane file proves
    writes; an unknown type is left alone but still counts as an occupant.

    What each of those three rules was getting wrong before: `docs/changelog/0.138.0-a-contract-with-no-gate-behind-it.md`.
    """
    isolation = tool_input.get("isolation")
    if isinstance(isolation, str) and isolation.strip():
        return None

    session_id = payload.get("session_id")
    tool_use_id = payload.get("tool_use_id") or ""

    lane = lane_file(root, subagent_type)
    writes = lane is not None and not is_read_only(lane)
    lanes = dispatch_ledger.concurrent(session_id, tool_use_id) + 1
    if writes and lanes >= 2:
        return ISOLATION_DENY_REASON.format(lanes=lanes, subagent_type=subagent_type)

    dispatch_ledger.record(session_id, tool_use_id)
    return None


def _deny(reason: str) -> None:
    """Emit the PreToolUse deny decision the model is shown."""
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }))


def decide(payload: Dict[str, Any]) -> int:
    """Print a deny decision iff the dispatch names no model, or joins a fan-out unisolated."""
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
    lane = lane_file(root, subagent_type)
    has_model = (isinstance(model, str) and model.strip()) or (
        lane is not None and declares_model(lane))
    if not has_model:
        _debug("deny", project=root, subagentType=subagent_type, why="no_model")
        _deny(DENY_REASON.format(subagent_type=subagent_type))
        return 0

    reason = isolation_verdict(payload, tool_input, root, subagent_type)
    if reason is not None:
        _debug("deny", project=root, subagentType=subagent_type, why="no_isolation")
        _deny(reason)
        return 0

    _debug("allow", project=root, subagentType=subagent_type,
           why="explicit_model" if (isinstance(model, str) and model.strip()) else "lane_file")
    return 0


def main() -> int:
    """Read the hook payload from stdin. Fails open: a broken gate must never brick dispatch."""
    try:
        return decide(json.load(sys.stdin))
    except Exception:  # pylint: disable=broad-exception-caught
        return 0


if __name__ == "__main__":
    sys.exit(main())
