"""The once-per-project bus identity (D2): .ai-badger/project-id.

Minted at scaffold time so the message bus can resolve "where there is .ai-badger,
there is a project" without any external registry. One uuid4 per directory — a
worktree is its own project. Existing ids are never regenerated.
"""
from __future__ import annotations

import uuid
from pathlib import Path


def mint_project_id(aib: Path) -> None:
    """Write <aib>/project-id (uuid4 + newline) when absent; existing ids are preserved."""
    path = aib / "project-id"
    if not path.exists():
        path.write_text(f"{uuid.uuid4()}\n", encoding="utf-8")
