"""Project-local invariants: .ai-badger/invariants/local/*.md render after framework ones.

The directory is project-owned — files are never copied, recorded in the manifest,
pruned, or overwritten; edit or delete them to change what renders (#313).
"""
from __future__ import annotations

from pathlib import Path
from typing import Callable, List, Set

NAME_COLLISION_NOTE = (
    "project-local invariant '{}' shares a name with the delivered invariant '{}' — "
    "both render; use config.exclude.invariants to drop the delivered one"
)
RENDERED_NOTE = (
    "rendered {} project-local invariant(s) from .ai-badger/invariants/local/ — edit "
    "or delete the files to change them"
)


def append_rendered(rendered: List[str], local_dir: Path, delivered: Set[str],
                    summarize: Callable[[str, str], str], notes: List[str]) -> None:
    """Append each non-empty local file's summary bullet, then report what happened.

    The link points into `local/`, where these files stay — they are project-owned and are never
    copied up beside the delivered ones.
    """
    before = len(rendered)
    for path in sorted(local_dir.glob("*.md")):
        if not path.is_file():
            continue
        text = path.read_text(encoding="utf-8").strip()
        if path.stem in delivered:
            notes.append(NAME_COLLISION_NOTE.format(path.stem, path.stem))
        if not text:
            continue
        rendered.append(summarize(text, f"local/{path.stem}"))
    if len(rendered) > before:
        notes.append(RENDERED_NOTE.format(len(rendered) - before))
