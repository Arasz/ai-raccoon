"""Status words — the caller-side observability.

The server cannot attribute calls to a client ("who calls it"), so the
provider — which knows the tool, project, agent and session — emits
one-word status cues on stderr as each call starts ("searching",
"remembering", ...).

The JSONL operation log (AIRACCOON_MEMORY_LOG) was removed 2026-08-11
(task mem-cleanup): file-based memory-operation logging is gone from
ai-raccoon and ai-badger; a new approach replaces it later.
"""

from __future__ import annotations

from typing import Dict

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
    "memory_set_ttl": "expiring",
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
