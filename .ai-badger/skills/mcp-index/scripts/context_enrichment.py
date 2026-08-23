"""MCP-tool-recommendation logic shared by every agent's context-enrichment hook (ADR-0012).

`ai_badger_hooks.py` (Hermes) inlines this same logic; this module exists so the Claude/Copilot
adapter (`context_enrichment_hook.py` in the `mcp-index` skill, issue #147) does not have to
duplicate index-loading, near-miss scoring and hint formatting on top of `mcp_matcher.py`.
Telemetry recording itself stays out of this module — that is the calling hook's job, the same
boundary `ai_badger_hooks.py` already draws between "call the matcher" and "log what happened".
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# Sibling-import convention shared with mcp_matcher.py itself: these modules are copied flat,
# beside each other, at scaffold time (RETRIEVAL_MODULES in the Hermes/Claude/Copilot
# adjustments).
_SIBLING_DIR = str(Path(__file__).resolve().parent)
if _SIBLING_DIR not in sys.path:
    sys.path.insert(0, _SIBLING_DIR)
from mcp_matcher import (  # noqa: E402  pylint: disable=wrong-import-position
    COVERAGE_TERM_CAP,
    DEFAULT_COVERAGE_THRESHOLD,
    build_corpus,
    tokenize,
)
from mcp_matcher import find_relevant_tools as _mm_find_relevant_tools  # noqa: E402  pylint: disable=wrong-import-position,line-too-long

TOP_N = 3
MAX_HINT_CHARS = 300
# debug_log's own PIPE_BUF-driven field clip (MAX_FIELD_CHARS); duplicated as a plain int here
# rather than imported, since this module must not depend on debug_log (see module docstring).
MAX_TOP_CANDIDATES_CHARS = 200

NUDGE_LINE = (
    "[ai-badger] Semantica is configured: record key decisions via record_decision and "
    "call export_graph(format=json) before finishing — dumps auto-save to .semantica/ "
    "and are indexed."
)

SEMANTICA_NUDGE_DIR = Path.home() / ".ai-badger" / "semantica-nudge"

__all__ = [
    "COVERAGE_TERM_CAP",
    "DEFAULT_COVERAGE_THRESHOLD",
    "TOP_N",
    "NUDGE_LINE",
    "SEMANTICA_NUDGE_DIR",
    "find_relevant_tools",
    "tokenize",
    "load_mcp_index",
    "has_legacy_unmigrated_index",
    "score_all_tools",
    "index_tool_count",
    "format_top_candidates",
    "tags_for_display",
    "build_hint",
    "semantica_indexed",
    "sanitize_path_segment",
    "semantica_nudge_marker_path",
    "semantica_nudge_already_shown",
    "record_semantica_nudge_shown",
]


def find_relevant_tools(
    query: str, index: Dict[str, Any], top_n: int = TOP_N
) -> List[Tuple[str, float]]:
    """Rank tools by relevance to `query`, gated by coverage; `(name, score)` pairs.

    Thin wrapper over `mcp_matcher.find_relevant_tools`: that function returns `MatchResult`
    dataclasses (`.tool`, `.score`, `.coverage`); callers here only need the name and score, the
    same shape `ai_badger_hooks.py`'s own `_find_relevant_tools` already exposes for Hermes.
    """
    return [(r.tool, r.score) for r in _mm_find_relevant_tools(query, index, top_n=top_n)]


def load_mcp_index(cwd: Optional[str]) -> Optional[Dict[str, Any]]:
    """Load .ai-badger/mcp-tools.json from the project, or None.

    JSON-only by design (docs/adr/0012 §4, issue #145): a project still on the legacy
    mcp-tools.yaml gets no recommendations until `mcp-index migrate` upgrades it.
    """
    if not cwd:
        return None
    index_path = Path(cwd) / ".ai-badger" / "mcp-tools.json"
    if not index_path.exists():
        return None
    try:
        return json.loads(index_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def has_legacy_unmigrated_index(cwd: Optional[str]) -> bool:
    """True when the project has a not-yet-migrated legacy mcp-tools.yaml.

    Distinguishes "not migrated yet" from "genuinely no index" so a caller's telemetry can
    tell the two apart (issue #145 review finding) instead of both reading as `absent`.
    """
    if not cwd:
        return False
    aib = Path(cwd) / ".ai-badger"
    return (aib / "mcp-tools.yaml").exists() and not (aib / "mcp-tools.json").exists()


def score_all_tools(query: str, index: Dict[str, Any]) -> List[Any]:
    """Every non-removed tool in the index, BM25-ranked against `query`, ungated.

    For near-miss telemetry: gated `find_relevant_tools` only returns winners, and a `gate`
    record needs the candidates that almost made it. `[]` when the index has no tools or the
    query tokenizes to nothing. Normalises coverage exactly as the gate does, so a logged
    near-miss is comparable to the threshold beside it in the record.
    """
    corpus = build_corpus(index)
    if corpus is None:
        return []
    query_terms = tokenize(query)
    if not query_terms:
        return []
    return corpus.rank(query_terms, coverage_cap=COVERAGE_TERM_CAP)


def index_tool_count(index: Dict[str, Any]) -> int:
    """Count of non-removed tools across every source in the index."""
    return sum(
        1
        for server in index.get("sources", [])
        for tool in server.get("tools", {}).values()
        if tool.get("status") != "removed"
    )


def format_top_candidates(scored: List[Any], limit: int = TOP_N) -> str:
    """`name:score:coverage` for the top candidates, or `name:score` if that doesn't fit
    debug_log's 200-char field clip.
    """
    top = scored[:limit]
    with_coverage = ",".join(f"{r.doc_id}:{r.score:.2f}:{r.coverage:.2f}" for r in top)
    if len(with_coverage) <= MAX_TOP_CANDIDATES_CHARS:
        return with_coverage
    return ",".join(f"{r.doc_id}:{r.score:.2f}" for r in top)


def tags_for_display(tool_name: str, index: Dict[str, Any]) -> List[str]:
    """Tags for one `server:tool` name, or `[]` when the tool isn't in the index."""
    if ":" in tool_name:
        sname, tname = tool_name.split(":", 1)
        for server in index.get("sources", []):
            if server.get("name") == sname:
                tool = server.get("tools", {}).get(tname, {})
                return tool.get("tags", [])
    return []


def build_hint(ranked: List[Tuple[str, float]], index: Dict[str, Any],
                max_chars: int = MAX_HINT_CHARS) -> str:
    """The `[ai-badger] Relevant MCP tools: ...` line, tags included unless that overflows."""
    tools_str = ", ".join(
        f"{name} ({', '.join(tags_for_display(name, index))})" for name, _ in ranked[:TOP_N]
    )
    hint = f"[ai-badger] Relevant MCP tools: {tools_str}"
    if len(hint) <= max_chars:
        return hint
    tools_str_short = ", ".join(name for name, _ in ranked[:TOP_N])
    return f"[ai-badger] Relevant MCP tools: {tools_str_short}"


def semantica_indexed(index: Optional[Dict[str, Any]]) -> bool:
    """True when any index source's last ':' token is 'semantica' (bare or decorated)."""
    return any(
        (source.get("name") or "").rsplit(":", 1)[-1] == "semantica"
        for source in (index or {}).get("sources", [])
    )


def sanitize_path_segment(value: Optional[str]) -> str:
    """Sanitize a string into a filesystem-safe single path segment.

    Keeps [A-Za-z0-9._-]; substitutes '_' for everything else.
    Guards dot-only traversal segments: '.' -> '_' and '..' -> '__'.
    Returns empty string for None or empty inputs.
    """
    if not value:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(value)
    )
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def _safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe; empty string is not a valid marker."""
    return sanitize_path_segment(session_id)


def semantica_nudge_marker_path(
    session_id: Optional[str],
    base_dir: Optional[Path] = None,
) -> Path:
    """The nudge marker file for a session (empty path when no session id)."""
    safe = _safe_session(session_id)
    if not safe:
        return Path("")
    target_dir = base_dir if base_dir is not None else SEMANTICA_NUDGE_DIR
    return target_dir / safe


def semantica_nudge_already_shown(
    session_id: Optional[str],
    base_dir: Optional[Path] = None,
) -> bool:
    """True when the session was already nudged for Semantica (marker exists)."""
    path = semantica_nudge_marker_path(session_id, base_dir=base_dir)
    return path.is_file() if path.name else False


def record_semantica_nudge_shown(
    session_id: Optional[str],
    base_dir: Optional[Path] = None,
) -> bool:
    """Touch the semantica nudge marker; False on a missing session id or IO failure."""
    path = semantica_nudge_marker_path(session_id, base_dir=base_dir)
    if not path.name:
        return False
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        return True
    except OSError:
        return False