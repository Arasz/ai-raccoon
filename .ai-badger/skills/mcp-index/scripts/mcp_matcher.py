"""BM25 matching over the MCP tool index (`.ai-badger/mcp-tools.json`).

Replaces `_KEYWORD_TAG_MAP` and its all-or-nothing gate (docs/adr/0012). Existing
tool `tags` are not discarded — they become a weighted field alongside `name` and
`intent`. The gate is BM25 coverage, not raw score: see
docs/changelog/0.47.0-retrieval-that-can-fail.md for why a fixed score threshold
does not work across corpora of very different size.
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterator, List, Tuple

# Sibling-import convention shared with ai_badger_hooks.py's own lazy loaders:
# these modules are copied flat, beside each other, at scaffold time.
_SIBLING_DIR = str(Path(__file__).resolve().parent)
if _SIBLING_DIR not in sys.path:
    sys.path.insert(0, _SIBLING_DIR)
from bm25 import Bm25Corpus, fuse_document  # pylint: disable=wrong-import-position
from tokenizer import tokenize  # pylint: disable=wrong-import-position

NAME_WEIGHT = 3.0
TAGS_WEIGHT = 2.0
INTENT_WEIGHT = 1.0

# Derived by sweeping this module's coverage gate against
# features/common/retrieval/eval/mcp_queries.jsonl over the real
# .ai-badger/mcp-tools.json corpus in this repo (98 tools) — see docs/adr/0012 for the
# sweep table. NOT the 0.30 value from the skill-index spec: that was derived on
# a 13-document corpus, and IDF behaves differently at this corpus size. 0.21 is
# where zero-result-on-positives first goes above 0 on this fixture set, so 0.20 is
# the highest value that keeps it at 0 — a 0.009 margin, not a comfortable one: the
# tightest true positive and a false fire on this fixture set sit at the same coverage
# (0.2089) to four decimal places.
DEFAULT_COVERAGE_THRESHOLD = 0.20
TOP_N = 3

# Coverage divides by the summed idf of the query's most informative terms, capped here
# rather than running over every term: uncapped, a sentence-length prompt diluted coverage
# to roughly 1/len(query) and the gate suppressed correct matches the ranker had already put
# first (issue #165). 6 is the longest query in the fixture set 0.20 was derived on, so every
# control fixture keeps its exact previous coverage; the sweep behind both numbers is in
# docs/changelog/0.50.0-the-coverage-gate-stops-scaling-with-query-length.md.
COVERAGE_TERM_CAP = 6


@dataclass(frozen=True)
class MatchResult:
    """One recommended tool: its full `server:tool` name, BM25 score, and coverage."""

    tool: str
    score: float
    coverage: float


def _iter_tools(index: Dict[str, Any]) -> Iterator[Tuple[str, List[str], str]]:
    """Yield (full_name, tags, intent) for every non-removed tool in the index."""
    for server in index.get("sources", []):
        sname = server.get("name", "")
        for tname, tool in server.get("tools", {}).items():
            if tool.get("status") == "removed":
                continue
            yield f"{sname}:{tname}", tool.get("tags", []), tool.get("intent", "")


def build_corpus(index: Dict[str, Any]) -> Bm25Corpus | None:
    """Build one fused BM25 document per tool, or None when the index has no tools."""
    documents = {}
    for full_name, tags, intent in _iter_tools(index):
        documents[full_name] = fuse_document([
            (tokenize(full_name.replace(":", " ")), NAME_WEIGHT),
            (tokenize(" ".join(tags)), TAGS_WEIGHT),
            (tokenize(intent), INTENT_WEIGHT),
        ])
    if not documents:
        return None
    return Bm25Corpus(documents)


def find_relevant_tools(
    query: str,
    index: Dict[str, Any],
    top_n: int = TOP_N,
    threshold: float = DEFAULT_COVERAGE_THRESHOLD,
) -> List[MatchResult]:
    """Rank tools by BM25, gate by coverage, return the top `top_n` (default 3).

    Deterministic tie-break (-score, -coverage, tool name) comes from
    Bm25Corpus.rank itself. Requires >=1 matched query term in addition to the
    coverage gate, so a single very-rare-but-irrelevant term can't clear it alone.
    """
    if not query:
        return []
    corpus = build_corpus(index)
    if corpus is None:
        return []
    query_terms = tokenize(query)
    if not query_terms:
        return []
    ranked = corpus.rank(query_terms, coverage_cap=COVERAGE_TERM_CAP)
    gated = [r for r in ranked if r.matched_terms >= 1 and r.coverage >= threshold]
    return [MatchResult(r.doc_id, r.score, r.coverage) for r in gated[:top_n]]
