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
    "add_memory": "remembering",
    "query": "searching",
    "get_index": "listing",
    "get_store_statistics": "counting",
    "share_memory": "sharing",
    "trigger_memory_sharing": "extracting",
    "delete_entry": "forgetting",
    "delete_context": "forgetting",
    "ingest_file": "ingesting",
    "ingest_directory": "ingesting",
    "embed_deferred_entries": "embedding",
    "initialize_workspace": "opening",
    "get_workspace_status": "checking",
    "close_workspace": "consolidating",
    "discard_workspace": "discarding",
    "trigger_memory_cleanup": "sweeping",
    "set_memory_ttl": "expiring",
    "sync_to_cloud": "syncing",
    "add_watch": "watching",
    "get_watch_status": "watching",
    "remove_watch": "watching",
}


def status_word(tool: str) -> str:
    """One word for the tool; an unknown tool falls back to its first underscore-separated part."""
    if tool in STATUS_WORDS:
        return STATUS_WORDS[tool]
    return tool.split("_", 1)[0] or "working"
