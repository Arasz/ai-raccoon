"""Constants for the JSAA docs ingestion pipeline (moved verbatim from scripts/ingest-jsaa-docs.py)."""

from __future__ import annotations

import os
from pathlib import Path

JSAA_ROOT = Path("/Users/arasz/RiderProjects/job-search-ai-assistant")
# Wave 0 reproducibility: ingest only the pinned jsaa tree (plan C §0/§3 Wave 0).
JSAA_PINNED_COMMIT = "9397bbef504b5b30a31003c84e8c5c316641adb6"
# Port 5000 is taken by macOS ControlCenter; override via MCP_URL env var.
MCP_BASE = os.environ.get("MCP_URL", "http://localhost:5000/mcp")
PROJECT_ID = "job-search-ai-assistant"
# src/ is one level below scripts/; the map itself stays at scripts/chunk-hash-map.json (C# contract).
HASH_MAP_PATH = Path(__file__).resolve().parent.parent / "chunk-hash-map.json"
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
