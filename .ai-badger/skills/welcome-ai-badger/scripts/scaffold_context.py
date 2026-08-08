"""The state a scaffold run shares between its collaborators.

One context object is the only channel between them: no collaborator reaches another
through `self`.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, List, Set


def _discard_template_record(source: Path, dest: Path, seed_once: bool = False) -> None:
    """Default provenance sink: a context with no manifest behind it records nothing."""


def _discard_record(feature: str, stack: str, name: str, source: Path, dest: Path,
                    **extra: Any) -> None:
    """Default provenance sink for a catalog item, for the same reason."""


def _discard_generated_config(path: Path, destination: str) -> None:
    """Default sink for a generated-but-not-owned config write, for the same reason."""


@dataclass
class ScaffoldContext:
    """Every attribute a scaffold collaborator reads or writes, and nothing else."""

    root: Path
    target: Path
    aib: Path
    config: Dict[str, Any]
    index: Dict[str, Any]
    stacks: List[str]
    skills: List[str]
    excluded: Dict[str, Set[str]]
    overwrite: bool = False
    # Reseed the SEED-ONCE files instead of preserving the project's copies (#15).
    reset_seed_files: bool = False
    notes: List[str] = field(default_factory=list)
    # Filled by McpTools and read by TemplateRendering — the cache lives here so neither
    # collaborator has to know the other exists. Every server a stack's stack-mcp.json names,
    # carrying the `instructions` its features/<stack>/mcp/<name>/server.md holds (ADR-0014).
    mcp_described: List[Dict[str, Any]] = field(default_factory=list)
    mcp_described_filled: bool = False
    # Whether the retired-declaration-file check has already spoken this run (ADR-0014 step 8).
    mcp_retired_files_noted: bool = False
    # Whether declared servers' install prerequisites have already been reported this run.
    mcp_prereqs_noted: bool = False
    # Manifest bookkeeping: shared state, not Scaffolder behaviour.
    record_template: Callable[..., None] = _discard_template_record
    record: Callable[..., None] = _discard_record
    # Called with (path, destination label) by whoever writes a config file ai-badger merges
    # into but does not own, so the manifest can answer "did ai-badger write here?" (#194).
    record_generated_config: Callable[[Path, str], None] = _discard_generated_config
