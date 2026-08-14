#!/usr/bin/env python3
"""Emits the curated jsaa corpus selection as JSON: pinned commit, project id, and the
relative file list matching INCLUDE_GLOBS/EXCLUDE_GLOBS (jsaa_config.py), evaluated
against a caller-supplied extraction root — the pinned commit's tree, extracted
read-only via `git archive` so the live jsaa checkout is never touched or required to
be checked out to the pin.

Used by tests/AiRaccoon.Tests/Integration/JsaaCorpusRegenerationTool.cs (WP4,
docs/plans/2026-08-14-code-quality-improvement-plan.md) to regenerate
tests/AiRaccoon.Tests/Resources/jsaa-memory.db through the production FileIngestor —
this script only selects *which* files to feed it; chunking and DB writes are C#.

Usage: python3 scripts/list-jsaa-corpus-files.py <extracted-root>
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from jsaa_config import JSAA_PINNED_COMMIT, PROJECT_ID  # noqa: E402
from sources import enumerate_files  # noqa: E402


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: list-jsaa-corpus-files.py <extracted-root>")
    root = Path(sys.argv[1]).resolve()
    files = [rel for _, rel, _ in enumerate_files(root)]
    print(json.dumps({
        "pinnedCommit": JSAA_PINNED_COMMIT,
        "projectId": PROJECT_ID,
        "files": files,
    }))


if __name__ == "__main__":
    main()
