#!/usr/bin/env python3
"""UserPromptSubmit hook: recommend MCP tools relevant to the prompt (docs/adr/0012, issue #147).

Standalone, stdlib-only. Loads `.ai-badger/mcp-tools.json`, ranks it via the BM25 matcher
(`context_enrichment.py` + `mcp_matcher.py`, copied alongside by the Claude/Copilot retrieval
adjustment — `features/claude/adjustments/adjust_retrieval.py` and its Copilot equivalent), and
emits the top matches as `additionalContext`.

Telemetry lands under the same `ai_badger_hooks/mcp_retrieval` component the Hermes plugin
writes (docs/retrieval.md §6), so an audit record reads the same regardless of which agent
produced it.

Silent (exit 0, no output) when: no prompt, the retrieval modules did not land, no index, nothing
clears the coverage gate, or any internal error — a broken hook must never block a prompt.
"""
from __future__ import annotations

import importlib.util
import json
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "ai_badger_hooks/mcp_retrieval"

# A distinctive sys.modules key, not the bare "context_enrichment" a plain `import` would use:
# several test modules load features/common/retrieval/context_enrichment.py directly (and,
# transitively, mcp_matcher.py) via sys.path, which would otherwise let a bare import here
# silently pick up whatever copy another test happened to load first. Mirrors
# ai_badger_hooks.py's own MCP_MATCHER_MODULE_NAME lazy sibling-import.
CONTEXT_ENRICHMENT_MODULE_NAME = "ai_badger_context_enrichment"


def _load_context_enrichment() -> Optional[Any]:
    """Import the sibling module lazily; None when the retrieval adjustment never ran.

    `context_enrichment.py` (and the `mcp_matcher.py`/`bm25.py`/`tokenizer.py` it depends on)
    is copied beside this hook by the Claude/Copilot retrieval adjustment, not by the plain
    skill scaffold — an older or partial scaffold can lack it, and this hook must degrade to
    silence rather than break the session.
    """
    cached = sys.modules.get(CONTEXT_ENRICHMENT_MODULE_NAME)
    if cached is not None:
        return cached
    path = Path(__file__).resolve().parent / "context_enrichment.py"
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location(CONTEXT_ENRICHMENT_MODULE_NAME, path)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[CONTEXT_ENRICHMENT_MODULE_NAME] = module
    try:
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught
        sys.modules.pop(CONTEXT_ENRICHMENT_MODULE_NAME, None)
        return None
    return module


# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: Dict[str, Any] = {}


def _debug(event: str, **fields: Any) -> None:
    """Record what retrieval did. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    project = fields.pop("project", None) or debug_log.resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=project, **fields)


def _record_retrieval(context_enrichment: Any, project: Optional[str], query: str,
                      index: Dict[str, Any], ranked) -> None:
    """Record what the BM25 retrieval did — mirrors ai_badger_hooks.py's `_record_retrieval`.

    `no_terms` is not `gate`: when the tokenizer yields no usable terms, no candidate is ever
    scored against the coverage threshold, so it carries no threshold field for the same reason
    one was never applied.
    """
    if debug_log is None or not debug_log.enabled_for(project):
        return
    query_terms = context_enrichment.tokenize(query)
    scored = context_enrichment.score_all_tools(query, index)
    common = {
        debug_log.KEY_QUERY: query,
        debug_log.KEY_CANDIDATES: context_enrichment.index_tool_count(index),
        debug_log.KEY_TOP: context_enrichment.format_top_candidates(scored),
    }
    if not query_terms:
        _debug("no_terms", project=project, **{**common, debug_log.KEY_RETURNED: ""})
        return
    scoring = {
        **common,
        debug_log.KEY_TERMS: ",".join(sorted(set(query_terms))),
        debug_log.KEY_THRESHOLD: context_enrichment.DEFAULT_COVERAGE_THRESHOLD,
    }
    if ranked:
        _debug("hit", project=project,
               **{**scoring, debug_log.KEY_RETURNED: ", ".join(name for name, _ in ranked)})
    else:
        _debug("gate", project=project, **{**scoring, debug_log.KEY_RETURNED: ""})


def main() -> int:
    """Read the hook payload from stdin and emit additionalContext when something recommends."""
    payload = json.load(sys.stdin)
    _PAYLOAD.update(payload)
    prompt = payload.get("prompt", "")
    if not prompt:
        _debug("skip", reason="no_prompt")
        return 0
    context_enrichment = _load_context_enrichment()
    if context_enrichment is None:
        _debug("skip", reason="matcher_unavailable")
        return 0

    project = (debug_log.resolve_project_root(payload) if debug_log
               else (payload.get("cwd") or None))

    index = context_enrichment.load_mcp_index(project)
    if index is None:
        event = "legacy" if context_enrichment.has_legacy_unmigrated_index(project) else "absent"
        query_key = debug_log.KEY_QUERY if debug_log else "q"
        _debug(event, project=project, **{query_key: prompt})
        return 0

    ranked = context_enrichment.find_relevant_tools(prompt, index, top_n=context_enrichment.TOP_N)
    _record_retrieval(context_enrichment, project, prompt, index, ranked)
    if not ranked:
        return 0

    hint = context_enrichment.build_hint(ranked, index)
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "UserPromptSubmit",
            "additionalContext": hint,
        }
    }))
    return 0


HOOK_ERRORS_FILE = Path.home() / ".ai-badger" / "hook-errors.log"
MAX_ERROR_LOG_BYTES = 1_000_000


def record_hook_failure(where):
    """Leave one content-free line behind before a hook swallows an exception.

    Type and location only: an exception message can quote scanned input.
    """
    exc_type, _, tb = sys.exc_info()
    frame = traceback.extract_tb(tb)[-1] if tb else None
    at = f"{Path(frame.filename).name}:{frame.lineno}" if frame else "unknown"
    name = exc_type.__name__ if exc_type else "Unknown"
    print(f"[ai-badger] {where} hook failed: {name} at {at}", file=sys.stderr)
    try:
        HOOK_ERRORS_FILE.parent.mkdir(parents=True, exist_ok=True)
        if HOOK_ERRORS_FILE.exists() and HOOK_ERRORS_FILE.stat().st_size > MAX_ERROR_LOG_BYTES:
            HOOK_ERRORS_FILE.unlink()
        with HOOK_ERRORS_FILE.open("a", encoding="utf-8") as fh:
            fh.write(f"{datetime.now(timezone.utc).isoformat()} {where} {name} at {at}\n")
    except OSError:
        pass


def guarded_main():
    """Run main(): a hook never breaks the session, but never fails invisibly either."""
    try:
        return main() or 0
    except Exception:  # pylint: disable=broad-exception-caught
        record_hook_failure("context_enrichment_hook")
        return 0


if __name__ == "__main__":
    sys.exit(guarded_main())
