"""Selection rules for the committed retrieval fixture corpus (ADR-0090).

The fixture bank `tests/AiRaccoon.Tests/Resources/docs-memory.db` is built from
*this* repository's own public documentation. Nothing here points at another
checkout: the corpus root is the ai-raccoon working tree.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

PROJECT_ID = "ai-raccoon"

# Two document families on purpose: `docs/**` and `.ai-badger/**`. RetrievalTuningSetsTests
# asserts the corpus carries more than one generator, so a selection that collapsed to a
# single family would silently hollow that gate out.
INCLUDE_GLOBS: list[str] = [
    "docs/adr/*.md",
    "docs/*.md",
    "docs/explanation/*.md",
    "docs/how-to/*.md",
    "docs/reference/*.md",
    "docs/tutorials/*.md",
    ".ai-badger/invariants/*.md",
    ".ai-badger/agents/*.md",
    ".ai-badger/instructions/*.md",
    ".ai-badger/skills/*/SKILL.md",
    ".ai-badger/delegation.md",
    "README.md",
    "CLAUDE.md",
    "HERMES.md",
]

# Skill *reference* files are deliberately not selected. They are the long tail of
# `.ai-badger` — 148 files, 43% of the candidate selection's files and 30% of its bytes,
# and its churniest part. Dropping them takes the committed bank from ~19 MB to ~13 MB
# while leaving both document families and ~1700 chunks, comfortably clear of the
# 1000-vector floor Vec0PartitionKeyProbe asserts. Measured, see ADR-0090.

# Measured 2026-08-22: with these include globs the selection is byte-identical whether an
# exclude list is applied or empty — every candidate exclusion (docs/work/, docs/plans/,
# .ai-badger/skills/learned/, .github/, ...) is unreachable, because no include glob is
# recursive enough to reach into those trees in the first place. An 18-entry list where no
# entry does anything is the stale list the derive-or-delete-the-list invariant warns about,
# so it is not carried. Narrowness of the include globs IS the exclusion mechanism, and
# scripts/tests/test_corpus_config.py::test_excluded_trees_stay_out is what guards it —
# that test goes red if an include glob is ever widened to reach a working-document tree.
EXCLUDE_GLOBS: list[str] = []


def select(root: Path) -> list[str]:
    """The corpus selection for `root`: include globs, intersected with git-tracked files.

    The tracked-file intersection is load-bearing, not hygiene. Without it any untracked
    file sitting in the working tree that happens to match a glob is baked into the
    committed bank — this was observed: a sibling branch's unmerged ADR left 29 chunks in
    a regenerated fixture. Restricting to tracked files makes the corpus reproducible from
    a clean clone, which is the property the pinned retrieval numbers rest on.
    """
    from sources import enumerate_files  # local import: sources imports config modules

    tracked = set(
        subprocess.run(
            ["git", "-C", str(root), "ls-files"],
            capture_output=True, text=True, check=True, timeout=60,
        ).stdout.splitlines()
    )
    return [
        rel
        for _, rel, _ in enumerate_files(
            root,
            include_globs=INCLUDE_GLOBS,
            exclude_globs=EXCLUDE_GLOBS,
            include_skill_references=False,
        )
        if rel in tracked
    ]
