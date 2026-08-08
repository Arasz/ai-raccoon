"""Status words and the memory operation log — the caller-side observability.

The server cannot attribute calls to a client ("who calls it"), so the
provider — which knows the tool, project, agent and session — emits:

- one-word status cues on stderr as each call starts ("searching",
  "remembering", ...), and
- JSONL rows to the AIRACCOON_MEMORY_LOG file with caller attribution.
"""

from __future__ import annotations

import json
import threading
import time
from typing import Any, Dict, Optional

STATUS_WORDS: Dict[str, str] = {
    "memory_write": "remembering",
    "memory_search": "searching",
    "memory_list": "listing",
    "memory_stats": "counting",
    "memory_share": "sharing",
    "memory_share_extract": "extracting",
    "memory_delete": "forgetting",
    "memory_delete_context": "forgetting",
    "memory_ingest_file": "ingesting",
    "memory_ingest_directory": "ingesting",
    "memory_embed_pending": "embedding",
    "memory_workspace_begin": "opening",
    "memory_workspace_status": "checking",
    "memory_workspace_consolidate": "consolidating",
    "memory_workspace_discard": "discarding",
    "memory_sweep": "sweeping",
    "memory_sync": "syncing",
    "memory_watch_add": "watching",
    "memory_watch_status": "watching",
    "memory_watch_remove": "watching",
}


def status_word(tool: str) -> str:
    """One word for the tool; unknown tools fall back to the name minus the memory_ prefix."""
    if tool in STATUS_WORDS:
        return STATUS_WORDS[tool]
    if tool.startswith("memory_"):
        suffix = tool[len("memory_"):]
        return suffix or "working"
    return "working" if tool == "memory" else tool


class MemoryOperationLog:
    """Append-only JSONL record of memory operations, safe across threads."""

    def __init__(self, path: str) -> None:
        self._path = path
        self._gate = threading.Lock()

    def write(self, tool: str, project_id: str, status: str, duration_ms: float,
              error_type: Optional[str] = None, agent_id: Optional[str] = None,
              session_id: Optional[str] = None) -> None:
        row: Dict[str, Any] = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + "Z",
            "tool": tool,
            "project_id": project_id,
            "status": status,
            "duration_ms": round(duration_ms, 1),
        }
        if error_type is not None:
            row["error_type"] = error_type
        if agent_id is not None:
            row["agent_id"] = agent_id
        if session_id is not None:
            row["session_id"] = session_id
        with self._gate:
            with open(self._path, "a", encoding="utf-8") as f:
                f.write(json.dumps(row) + "\n")
