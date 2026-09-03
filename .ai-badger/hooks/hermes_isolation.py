#!/usr/bin/env python3
"""Whether Hermes will run delegated subagents in one shared working copy.

`delegate_task` cannot express isolation — Hermes 0.20.6's DELEGATE_TASK_SCHEMA carries
exactly goal, context and output_schema — so the only lever is the session-wide
`delegation.worktree_isolation` key, which defaults to false. Read-only: ai-badger does not
own ~/.hermes/config.yaml.

Scanned rather than parsed; `import yaml` is deliberately kept off the hook path.
Rationale: `docs/changelog/0.140.0-hermes-cannot-be-gated-per-dispatch.md`.
"""
from __future__ import annotations

import os
from pathlib import Path

HERMES_HOME_ENV = "HERMES_HOME"
DELEGATION_BLOCK = "delegation:"
ISOLATION_KEY = "worktree_isolation:"
TRUTHY = ("true", "yes", "on", "1")

ISOLATION_NOTICE = (
    "ai-badger: Hermes subagents share this working copy. "
    "delegation.worktree_isolation is not enabled in %s, so parallel delegate_task "
    "children all run in one checkout and a green run proves nothing about its own "
    "change. Add under `delegation:` in that file:  worktree_isolation: true"
)


def hermes_config_path() -> Path:
    """$HERMES_HOME/config.yaml, else ~/.hermes/config.yaml — the rule adjust_hooks uses."""
    override = os.environ.get(HERMES_HOME_ENV, "").strip()
    home = Path(override).expanduser() if override else Path.home() / ".hermes"
    return home / "config.yaml"


def delegation_block(lines: list) -> list:
    """The indented entries under a top-level `delegation:`; empty when there is no block.

    Stops at the next top-level key, so the same setting under another mapping is not read
    as this one. Comment lines are kept — no comment can start with the key being matched,
    and a guard no test can exercise is not a guard.
    """
    block: list = []
    inside = False
    for line in lines:
        stripped = line.strip()
        if not inside:
            if stripped == DELEGATION_BLOCK:
                inside = True
            continue
        if line[:1] not in (" ", "\t"):
            break
        if stripped:
            block.append(stripped)
    return block


def subagents_share_one_tree() -> bool:
    """True when Hermes is configured here and delegated children will share one checkout.

    False when the key is on, when there is no Hermes config, and on any read error — a
    notice that cannot read its input must not guess.
    """
    try:
        raw = hermes_config_path().read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    for entry in delegation_block(raw.splitlines()):
        if entry.startswith(ISOLATION_KEY):
            return entry[len(ISOLATION_KEY):].partition("#")[0].strip().lower() not in TRUTHY
    return True
