"""The manifest's record of config files ai-badger generates but does not own (issue #194).

`.mcp.json`, `.github/mcp.json` and `.claude/settings.json` are merged into, never claimed as
manifest entries, so "did ai-badger write this file?" was a key-shape guess
(`mcp_tools.only_generated_entries`). This records the writes instead — bookkeeping, never
ownership: nothing reads a record back to overwrite, delete or drift-check the file.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any, Dict, List, Sequence, Tuple

from _shared import _within


class GeneratedConfigRecords:
    """The generated-but-not-owned config paths one scaffold run wrote, keyed by destination."""

    def __init__(self, target: Path, framework_version: str):
        self.target = target
        self.framework_version = framework_version
        self._records = {}  # type: Dict[Tuple[str, str], Dict[str, Any]]

    def record(self, path: Path, destination: str) -> None:
        """Record that this run wrote *path* on behalf of the destination labelled *destination*."""
        rel = path.relative_to(self.target).as_posix() if _within(self.target, path) \
            else path.as_posix()
        self._records[(rel, destination)] = {
            "path": rel,
            "destination": destination,
            "frameworkVersion": self.framework_version,
        }

    def all_records(self, prior: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """This run's records, plus *prior* runs' for the files still on disk, path-ordered.

        A record outlives the run that wrote it and dies with the file it names: "0.51.0 wrote
        here" is what makes a retirement provable, and a path nothing wrote any more is stale.
        """
        merged = {}  # type: Dict[Tuple[str, str], Dict[str, Any]]
        for rec in prior:
            path, destination = rec.get("path"), rec.get("destination")
            if isinstance(path, str) and isinstance(destination, str) \
                    and (self.target / path).exists():
                merged[(path, destination)] = rec
        merged.update(self._records)
        return [merged[key] for key in sorted(merged)]
