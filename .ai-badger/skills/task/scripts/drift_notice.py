"""Tier 1 drift comparison: scaffold `frameworkVersion` vs. a plugin's own `VERSION`.

Extracted to its own module (rather than left on `session_start_hook.py`, or duplicated) because
two independent scripts need it: `session_start_hook.py` (scaffolded into a consumer's
`.ai-badger/`, no drift responsibility since #24) previously computed it inline, and
`drift_notice_hook.py` (plugin-provided, registered via `hooks/hooks.json`, the only place this
now actually fires — see #24 and ADR-0001 decision 5) needs the same comparison. A shared module
keeps the comparison logic in one place without pulling `drift_notice_hook.py` into
`session_start_hook.py`'s unrelated import graph (`tracker_lib`, `subprocess`, ...).
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Optional


def scaffold_drift_notice(project_root: Path, plugin_root: Optional[str]) -> Optional[str]:
    """Return a one-line notice when the scaffold and the running plugin are different versions.

    Two local file reads, no network (ADR-0001 decision 5). Silent on match, on an
    unscaffolded project, and on any read error — a hook must never break session start, and a
    noisy hook gets ignored.
    """
    if not plugin_root:
        return None
    try:
        manifest = json.loads(
            (project_root / ".ai-badger" / "manifest.json").read_text(encoding="utf-8")
        )
        if not isinstance(manifest, dict):
            return None
        scaffold_version = manifest.get("frameworkVersion")
        plugin_version = (Path(plugin_root) / "VERSION").read_text(encoding="utf-8").strip()
    except (OSError, ValueError, AttributeError):
        return None
    if not scaffold_version or not plugin_version or scaffold_version == plugin_version:
        return None
    return (
        f"[ai-badger] .ai-badger/ was scaffolded by {scaffold_version} but the running "
        f"plugin is {plugin_version}. Re-scaffold with welcome-ai-badger to realign, "
        f"then review the diff."
    )
