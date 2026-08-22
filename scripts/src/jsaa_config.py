"""Constants for the owner-local job-search-ai-assistant ingest CLI.

This is NOT the fixture corpus — that is `corpus_config.py`, built from this repository's
own public docs (ADR-0090). This module drives `scripts/ingest-jsaa-docs.py`, which ingests
a private project on the owner's machine.

The tree location and the commit pin are read from the environment. They used to be literals:
an absolute path into a private checkout and a foreign repository's commit SHA, both committed
to a public repo. Neither is secret, but neither belongs here, and a pin left behind after the
corpus it pinned has gone is exactly the stale-list smell (ai-raccoon#414).
"""

from __future__ import annotations

import os
from pathlib import Path

JSAA_ROOT_ENV = "AIRACCOON_JSAA_ROOT"
JSAA_PINNED_COMMIT_ENV = "AIRACCOON_JSAA_PINNED_COMMIT"

_root = os.environ.get(JSAA_ROOT_ENV, "")
JSAA_ROOT = Path(_root) if _root else None
# Reproducibility: ingest only the pinned tree. Unset means "no pin configured" and the
# pipeline refuses to run rather than silently ingesting whatever is checked out.
JSAA_PINNED_COMMIT = os.environ.get(JSAA_PINNED_COMMIT_ENV, "")
# Port 5000 is taken by macOS ControlCenter; override via MCP_URL env var.
MCP_BASE = os.environ.get("MCP_URL", "http://localhost:5000/mcp")
PROJECT_ID = "job-search-ai-assistant"
BATCH_SIZE = 50

INCLUDE_GLOBS: list[str] = [
    "docs/adr/*.md",
    "docs/*.md",
    "docs/explanation/*.md",
    "docs/how-to/*.md",
    "docs/reference/*.md",
    "docs/rules/*.md",
    "docs/tutorials/*.md",
    "docs/legacy/*.md",
    "docs/meta/baseline.json",
    "docs/meta/trust-debt.md",
    "docs/meta/trust-index.json",
    ".ai-badger/invariants/*.md",
    ".ai-badger/skills/*/SKILL.md",
    ".ai-badger/agents/*.md",
    ".ai-badger/instructions/*.md",
    ".ai-badger/config.json",
    ".ai-badger/delegation.md",
    ".ai-badger/copilot-instructions.md",
    ".ai-badger/agent-instructions/*",
    ".remember/recent.md",
    ".remember/archive.md",
    "README.md",
    "CLAUDE.md",
    "HERMES.md",
    "REVIEW.md",
    "infra/README.md",
]

EXCLUDE_GLOBS: list[str] = [
    ".ai-badger/state.json",
    ".ai-badger/status-notes.json",
    ".ai-badger/status-history.json",
    ".ai-badger/task-tracking/",
    ".ai-badger/hooks/",
    ".ai-badger/worktrees/",
    ".ai-badger/prompt-markers/",
    ".ai-badger/skills-data/",
    ".ai-badger/mcp-tools.json",
    ".ai-badger/manifest.json",
    ".ai-badger/stack-ignore.json",
    ".ai-badger/mcp-tools.yaml.migrated",
    ".remember/now.md",
    ".remember/today-*.md",
    ".remember/tmp/",
    ".remember/logs/",
    "docs/work/",
    "docs/brand/",
    "docs/state.json",
    "docs/now.md",
    ".github/",
    "node_modules/",
    ".git/",
]

CONTEXTS_TO_DELETE = [
    "project:job-search-ai-assistant",
]

SPOT_CHECKS = [
    ("what is the frontend component library?", "ADR-0011"),
    ("TDD policy", "tdd-mandatory"),
    ("Cosmos DB partition key strategy", "partition-by-userid"),
    ("channel monitoring architecture", "ADR-0024 or ADR-0061"),
]
