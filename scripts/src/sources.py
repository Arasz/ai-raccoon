"""File enumeration for the JSAA docs ingestion pipeline (moved verbatim from scripts/ingest-jsaa-docs.py)."""

from __future__ import annotations

import fnmatch
from pathlib import Path

from jsaa_config import EXCLUDE_GLOBS, INCLUDE_GLOBS, JSAA_ROOT


def _matches_exclude(rel: str) -> bool:
    """Check if a relative path matches any exclusion rule."""
    for pattern in EXCLUDE_GLOBS:
        if pattern.endswith("/"):
            # Directory prefix exclusion
            if rel.startswith(pattern) or rel + "/" == pattern:
                return True
        elif pattern.endswith("/*"):
            # Glob-style: directory/* (but not subdirs)
            prefix = pattern[:-1]  # e.g. ".remember/today-*" → we keep the *
            if "*" in prefix:
                # Wildcard match
                if fnmatch.fnmatch(rel, pattern):
                    return True
            else:
                dir_prefix = pattern[:-2]  # e.g. ".ai-badger/task-tracking/"
                if rel.startswith(dir_prefix):
                    return True
        else:
            # Exact file match
            if rel == pattern:
                return True
    return False


def classify_file(rel: str) -> tuple[str, str]:
    """Return (type_key, context_label) for a relative path.

    Type keys: adr, architecture, explanation, howto, reference, rules,
               invariants, skills, agents, instructions, config, agent-model,
               remember, tutorials, legacy, meta, root-md, infra
    """
    # ADRs
    if rel.startswith("docs/adr/"):
        return ("adr", "docs:adr")

    # Architecture docs (top-level docs/*.md except root entries)
    architecture_docs = {
        "docs/architecture.md",
        "docs/data-model.md",
        "docs/flows.md",
        "docs/requirements.md",
        "docs/CHANGELOG.md",
    }
    if rel in architecture_docs:
        return ("architecture", "docs:architecture")

    # Explanation
    if rel.startswith("docs/explanation/"):
        return ("explanation", "docs:explanation")

    # How-to
    if rel.startswith("docs/how-to/"):
        return ("howto", "docs:how-to")

    # Reference
    if rel.startswith("docs/reference/"):
        return ("reference", "docs:reference")

    # Rules
    if rel.startswith("docs/rules/"):
        return ("rules", "docs:rules")

    # Tutorials
    if rel.startswith("docs/tutorials/"):
        return ("tutorials", "docs:tutorials")

    # Legacy
    if rel.startswith("docs/legacy/"):
        return ("legacy", "docs:legacy")

    # Meta
    if rel.startswith("docs/meta/"):
        return ("meta", "docs:meta")

    # Top-level docs/ (catch-all for architecture-like docs: README.md etc.)
    if rel.startswith("docs/") and rel != "docs/README.md":
        return ("architecture", "docs:architecture")

    # Invariants
    if rel.startswith(".ai-badger/invariants/"):
        return ("invariants", "ai-badger:invariants")

    # Skills (SKILL.md files and their reference files)
    if rel.startswith(".ai-badger/skills/"):
        return ("skills", "ai-badger:skills")

    # Agents
    if rel.startswith(".ai-badger/agents/"):
        return ("agents", "ai-badger:agents")

    # Instructions
    if rel.startswith(".ai-badger/instructions/"):
        return ("instructions", "ai-badger:instructions")

    # Agent-instructions (model)
    if rel.startswith(".ai-badger/agent-instructions/"):
        return ("agent-model", "ai-badger:instructions")

    # Config / delegation / copilot-instructions
    config_files = {
        ".ai-badger/config.json",
        ".ai-badger/delegation.md",
        ".ai-badger/copilot-instructions.md",
    }
    if rel in config_files:
        return ("config", "ai-badger:instructions")

    # Remember
    if rel.startswith(".remember/"):
        return ("remember", "remember:operational")

    # Root markdown (also catches docs/README.md)
    root_md = {"README.md", "CLAUDE.md", "HERMES.md", "REVIEW.md", "docs/README.md"}
    if rel in root_md:
        return ("root-md", "docs:architecture")

    # Infra
    if rel.startswith("infra/"):
        return ("infra", "docs:architecture")

    # Default fallback
    return ("unknown", "docs:architecture")


def enumerate_files(root: Path = JSAA_ROOT) -> list[tuple[Path, str, str]]:
    """Walk root and return [(absolute_path, relative_path, type_key), ...].

    Sorted for determinism.
    """
    # Collect all include matches
    included: dict[str, Path] = {}  # rel_path → abs_path

    for pattern in INCLUDE_GLOBS:
        if "*" in pattern:
            # Glob expansion
            for p in root.glob(pattern):
                if p.is_file():
                    rel = str(p.relative_to(root))
                    if not _matches_exclude(rel):
                        included[rel] = p
        elif pattern.endswith("/*"):
            # Directory/* : all files in dir (non-recursive)
            dir_glob = root / pattern
            for p in dir_glob.parent.glob(dir_glob.name):
                if p.is_file():
                    rel = str(p.relative_to(root))
                    if not _matches_exclude(rel):
                        included[rel] = p
        else:
            # Exact file path
            p = root / pattern
            if p.is_file():
                rel = str(p.relative_to(root))
                if not _matches_exclude(rel):
                    included[rel] = p

    # Also include skill reference files
    for skill_dir in (root / ".ai-badger/skills").iterdir():
        if not skill_dir.is_dir():
            continue
        refs_dir = skill_dir / "references"
        if refs_dir.is_dir():
            for ref_file in refs_dir.rglob("*"):
                if ref_file.is_file():
                    rel = str(ref_file.relative_to(root))
                    if not _matches_exclude(rel):
                        included[rel] = ref_file

    results: list[tuple[Path, str, str]] = []
    for rel in sorted(included):
        type_key, _ = classify_file(rel)
        results.append((included[rel], rel, type_key))

    return results
