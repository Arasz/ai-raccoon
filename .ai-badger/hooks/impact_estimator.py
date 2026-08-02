"""Impact estimation for the commit-reminder skill: a pure, free default heuristic, and an
optional code-review-graph subprocess call that is slow (15-20s observed) and must only run
when a caller explicitly asks for it.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from typing import List, Optional

_LOW_MAX = 5
_MEDIUM_MAX = 15


def _severity(count: int) -> str:
    if count < _LOW_MAX:
        return "low"
    if count < _MEDIUM_MAX:
        return "medium"
    return "high"


def default_impact(files: List[str]) -> str:
    """Pure, no-I/O impact summary: file count, distinct parent directories, severity."""
    count = len(files)
    dir_count = len({str(Path(f).parent) for f in files})
    directory_word = "directory" if dir_count == 1 else "directories"
    severity = _severity(count)
    return (f"{count} file(s) changed across {dir_count} {directory_word} "
            f"({severity} impact)")


def graph_impact(files: List[str], root: str, timeout: float = 8.0) -> Optional[str]:
    """Ask the code-review-graph CLI for impact; `None` on any failure, never raises.

    Invoked only as an external subprocess — this module never imports
    ``code_review_graph`` itself, so it works even where that package isn't installed.
    """
    cmd = [sys.executable, "-m", "code_review_graph", "impact",
            "--files", *files, "--repo", root, "--max-results", "1"]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                                 check=False)
    except (subprocess.TimeoutExpired, OSError):
        return None
    if result.returncode != 0:
        return None
    try:
        data = json.loads(result.stdout)
    except ValueError:
        return None
    if not isinstance(data, dict) or data.get("status") != "ok":
        return None
    summary = data.get("summary")
    total_impacted = data.get("total_impacted")
    if not isinstance(summary, str) or not isinstance(total_impacted, int):
        return None
    return f"{summary} (total impacted: {total_impacted})"


def estimate_impact(files: List[str], root: str, use_graph: bool = False) -> str:
    """Cheap default unless ``use_graph`` is truthy; falls back to default on graph failure."""
    if not use_graph:
        return default_impact(files)
    graph_result = graph_impact(files, root)
    return graph_result if graph_result is not None else default_impact(files)
