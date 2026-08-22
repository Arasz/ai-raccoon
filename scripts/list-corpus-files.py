#!/usr/bin/env python3
"""Emits the fixture corpus selection as JSON: project id and the relative file list
matching corpus_config's INCLUDE_GLOBS/EXCLUDE_GLOBS, evaluated against a caller-supplied
repository root.

Used by tests/AiRaccoon.Tests/Integration/DocsCorpusRegenerationTool.cs to regenerate
tests/AiRaccoon.Tests/Resources/docs-memory.db through the production FileIngestor — this
script only selects *which* files to feed it; chunking and DB writes are C# (ADR-0042).

The root is this repository (ADR-0090). There is no second checkout and no pinned foreign
commit: the corpus is the tree the tool runs in.

Usage: python3 scripts/list-corpus-files.py <repo-root>
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from corpus_config import EXCLUDE_GLOBS, INCLUDE_GLOBS, PROJECT_ID  # noqa: E402
from sources import enumerate_files  # noqa: E402


def select(root: Path) -> list[str]:
    return [
        rel
        for _, rel, _ in enumerate_files(
            root,
            include_globs=INCLUDE_GLOBS,
            exclude_globs=EXCLUDE_GLOBS,
            include_skill_references=False,
        )
    ]


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: list-corpus-files.py <repo-root>")
    root = Path(sys.argv[1]).resolve()
    print(json.dumps({"projectId": PROJECT_ID, "files": select(root)}))


if __name__ == "__main__":
    main()
