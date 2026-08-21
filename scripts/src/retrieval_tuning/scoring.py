"""Pure scoring functions for the retrieval-tuning harness (plan section 7.1).

Relevance is binary: a result gains 1 iff it is the query's target chunk
(expectedHash prefix match, or source_file suffix match against expectedSource
with an optional #section anchor), otherwise 0. No I/O, no server knowledge —
everything here is unit-testable with hand-computed values.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import Optional

# nDCG@5 uses the log2 discount over ranks 1..5 (rank i counts 1/log2(i+1)).
_TOP_K = 5
_DISCOUNTS = [1.0 / math.log2(i + 2) for i in range(_TOP_K)]  # [1, 1/log2(3), 1/2, 1/log2(5), 1/log2(6)]

# Keys a search result may carry a heading under. The MCP result record has no
# heading field today (hash/ranking/path/snippet/sourceFile/chunkIndex/totalChunks),
# so an anchor is verified only when one of these is present; otherwise the
# file-level match stands (the corpus validator resolves anchors against the copy).
_HEADING_KEYS = ("headingPath", "heading", "section", "path")


def ndcg_at_5(gains: list[int]) -> float:
    """nDCG@5 over binary gains with the log2 discount (plan 7.1)."""
    top = gains[:_TOP_K]
    if not top:
        return 0.0
    dcg = sum(g * d for g, d in zip(top, _DISCOUNTS))
    relevant = sum(top)
    if relevant == 0:
        return 0.0
    idcg = sum(_DISCOUNTS[:relevant])
    return dcg / idcg


def mrr_at_5(gains: list[int]) -> float:
    """Reciprocal rank of the first relevant result within the top 5, else 0."""
    for i, g in enumerate(gains[:_TOP_K]):
        if g:
            return 1.0 / (i + 1)
    return 0.0


def hit_at_k(gains: list[int], k: int) -> int:
    """1 when any of the first k results is relevant, else 0."""
    return 1 if any(gains[:k]) else 0


def rank_of_first_relevant(gains: list[int]) -> Optional[int]:
    """1-based rank of the first relevant result within the top 5, or None."""
    for i, g in enumerate(gains[:_TOP_K]):
        if g:
            return i + 1
    return None


def _source_pattern(expected_source: str) -> re.Pattern:
    """Regex for the expectedSource path: ':' separators become '/', '*' becomes '.*'.

    The pattern is anchored at the end (suffix match) — a result's source_file
    must END with the expected path so a sibling file with the same tail cannot
    satisfy a more specific expectation.
    """
    path = expected_source.split("#", 1)[0].strip()
    normalized = path.replace(":", "/")
    return re.compile(re.escape(normalized).replace(r"\*", ".*") + r"$", re.IGNORECASE)


def source_file_matches(source_file: str, expected_source: str) -> bool:
    """True when source_file's suffix matches the expectedSource path (with '*' wildcards)."""
    pattern = _source_pattern(expected_source)
    return bool(pattern.search(source_file))


def anchor_of(expected_source: str) -> Optional[str]:
    """The #section anchor of an expectedSource (e.g. 'decision'), or None."""
    if "#" not in expected_source:
        return None
    anchor = expected_source.split("#", 1)[1].strip()
    return anchor.lower() or None


def _heading_segments_match(result: dict, anchor: str) -> bool:
    """Any-segment heading match (skill: last-segment vs any-segment divergence)."""
    for key in _HEADING_KEYS:
        heading = result.get(key)
        if isinstance(heading, str) and heading.strip():
            return any(seg.strip().lower() == anchor for seg in heading.split(">"))
    return False


def _hash_matches(result: dict, expected_hash: str) -> bool:
    value = result.get("hash")
    return isinstance(value, str) and value.lower().startswith(expected_hash.lower())


def _source_matches(result: dict, expected_source: str) -> bool:
    source_file = result.get("sourceFile")
    if not isinstance(source_file, str):
        return False
    if not source_file_matches(source_file, expected_source):
        return False
    # Section-anchor matching is best-effort: MCP search results carry a plain
    # file `path`/`sourceFile`, not heading segments, so _heading_segments_match
    # cannot fire on real results (review F3, 2026-08-21). Every corpus entry in
    # the shipped corpora carries expectedHash, which resolve_gain checks first —
    # this branch exists for corpora without hashes and stays honest (matches the
    # source file only, never a fabricated section hit).
    anchor = anchor_of(expected_source)
    if anchor is not None and any(isinstance(result.get(k), str) for k in _HEADING_KEYS):
        return _heading_segments_match(result, anchor)
    return True


def resolve_gain(result: dict, entry: dict) -> int:
    """Binary relevance of one search result for one corpus entry (plan 7.1)."""
    expected_hash = entry.get("expectedHash")
    if isinstance(expected_hash, str) and expected_hash and _hash_matches(result, expected_hash):
        return 1
    expected_source = entry.get("expectedSource")
    if isinstance(expected_source, str) and expected_source and _source_matches(result, expected_source):
        return 1
    return 0


def gains_for(results: list[dict], entry: dict) -> list[int]:
    """Binary gains of a ranked result list (first 5) for one corpus entry."""
    return [resolve_gain(r, entry) for r in results[:_TOP_K]]


@dataclass(frozen=True)
class QueryScore:
    """One query's scored outcome (plan 7.1 per-query rows)."""

    entry_id: str
    category: str
    ndcg5: float
    mrr5: float
    hit3: int
    hit1: int
    first_relevant_rank: Optional[int]

    def as_dict(self) -> dict:
        return {
            "entry_id": self.entry_id,
            "category": self.category,
            "ndcg5": self.ndcg5,
            "mrr5": self.mrr5,
            "hit3": self.hit3,
            "hit1": self.hit1,
            "first_relevant_rank": self.first_relevant_rank,
        }


@dataclass(frozen=True)
class CategoryMetrics:
    """Aggregate over one category bucket."""

    category: str
    count: int
    mean_ndcg5: float
    mean_mrr5: float
    hit3_rate: float
    hit1_rate: float


@dataclass(frozen=True)
class Metrics:
    """Evaluation outcome: means + per-query + per-category records."""

    mean_ndcg5: float
    mean_mrr5: float
    hit3_rate: float
    hit1_rate: float
    per_query: list[QueryScore] = field(default_factory=list)
    per_category: dict[str, CategoryMetrics] = field(default_factory=dict)
    config: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return {
            "mean_ndcg5": self.mean_ndcg5,
            "mean_mrr5": self.mean_mrr5,
            "hit3_rate": self.hit3_rate,
            "hit1_rate": self.hit1_rate,
            "per_query": [q.as_dict() for q in self.per_query],
            "per_category": {
                name: {
                    "count": cm.count,
                    "mean_ndcg5": cm.mean_ndcg5,
                    "mean_mrr5": cm.mean_mrr5,
                    "hit3_rate": cm.hit3_rate,
                    "hit1_rate": cm.hit1_rate,
                }
                for name, cm in sorted(self.per_category.items())
            },
            "config": self.config,
        }


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def score_query(results: list[dict], entry: dict) -> QueryScore:
    """Score one query's ranked results against its corpus entry."""
    gains = gains_for(results, entry)
    return QueryScore(
        entry_id=str(entry.get("id", "?")),
        category=str(entry.get("category", "uncategorized")),
        ndcg5=ndcg_at_5(gains),
        mrr5=mrr_at_5(gains),
        hit3=hit_at_k(gains, 3),
        hit1=hit_at_k(gains, 1),
        first_relevant_rank=rank_of_first_relevant(gains),
    )


def summarize(query_scores: list[QueryScore], config: Optional[dict] = None) -> Metrics:
    """Means + per-query + per-category records over scored queries."""
    by_category: dict[str, list[QueryScore]] = {}
    for qs in query_scores:
        by_category.setdefault(qs.category, []).append(qs)

    per_category = {
        name: CategoryMetrics(
            category=name,
            count=len(scores),
            mean_ndcg5=_mean([q.ndcg5 for q in scores]),
            mean_mrr5=_mean([q.mrr5 for q in scores]),
            hit3_rate=_mean([float(q.hit3) for q in scores]),
            hit1_rate=_mean([float(q.hit1) for q in scores]),
        )
        for name, scores in by_category.items()
    }

    return Metrics(
        mean_ndcg5=_mean([q.ndcg5 for q in query_scores]),
        mean_mrr5=_mean([q.mrr5 for q in query_scores]),
        hit3_rate=_mean([float(q.hit3) for q in query_scores]),
        hit1_rate=_mean([float(q.hit1) for q in query_scores]),
        per_query=list(query_scores),
        per_category=per_category,
        config=dict(config or {}),
    )
