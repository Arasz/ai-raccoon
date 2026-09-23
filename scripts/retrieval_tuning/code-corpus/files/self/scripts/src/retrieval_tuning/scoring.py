"""Pure scoring functions for the retrieval-tuning harness (plan section 7.1).

Relevance is binary: a result gains 1 iff it is the query's target chunk
(expectedHash prefix match, or source_file suffix match against expectedSource
with an optional #section anchor), otherwise 0. No I/O, no server knowledge —
everything here is unit-testable with hand-computed values.

The code-eval runner (E3) extends this module rather than forking it: nDCG/MRR
gain generalized to an arbitrary k (ndcg_at_k/mrr_at_k, with ndcg_at_5/mrr_at_5
kept as k=5 call-throughs so every existing caller is untouched byte-for-byte),
a span-level gain (line_overlap_gain + span_gain) for code chunks whose answer
is a line range rather than a whole file, and QueryScore/Metrics grow optional
grouping fields (language/sizeBand/split/family/negativeTest) so code results
can be broken down the way the plan's Keep/drop rule needs, without changing
the shape any memory-eval caller already depends on.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import Optional

# nDCG@5 uses the log2 discount over ranks 1..5 (rank i counts 1/log2(i+1)).
_TOP_K = 5
_TOP_K_EXTENDED = 10
_DISCOUNTS = [1.0 / math.log2(i + 2) for i in range(_TOP_K)]  # [1, 1/log2(3), 1/2, 1/log2(5), 1/log2(6)]

# Keys a search result may carry a heading under. The MCP result record has no
# heading field today (hash/ranking/path/snippet/sourceFile/chunkIndex/totalChunks),
# so an anchor is verified only when one of these is present; otherwise the
# file-level match stands (the corpus validator resolves anchors against the copy).
_HEADING_KEYS = ("headingPath", "heading", "section", "path")


def _discounts(k: int) -> list[float]:
    return [1.0 / math.log2(i + 2) for i in range(k)]


def ndcg_at_k(gains: list[int], k: int) -> float:
    """nDCG@k over binary gains with the log2 discount (generalizes plan 7.1's nDCG@5)."""
    top = gains[:k]
    if not top:
        return 0.0
    discounts = _DISCOUNTS if k == _TOP_K else _discounts(k)
    dcg = sum(g * d for g, d in zip(top, discounts))
    relevant = sum(top)
    if relevant == 0:
        return 0.0
    idcg = sum(discounts[:relevant])
    return dcg / idcg


def ndcg_at_5(gains: list[int]) -> float:
    """nDCG@5 over binary gains with the log2 discount (plan 7.1)."""
    return ndcg_at_k(gains, _TOP_K)


def mrr_at_k(gains: list[int], k: int) -> float:
    """Reciprocal rank of the first relevant result within the top k, else 0."""
    for i, g in enumerate(gains[:k]):
        if g:
            return 1.0 / (i + 1)
    return 0.0


def mrr_at_5(gains: list[int]) -> float:
    """Reciprocal rank of the first relevant result within the top 5, else 0."""
    return mrr_at_k(gains, _TOP_K)


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
        # Code results (kind=code) carry no sourceFile field at all — CodeSearchResult
        # exposes 'path' instead (src/AiRaccoon.Core/Memory/Code/CodeSearchResult.cs).
        # Falling back here is additive: a memory result's sourceFile always wins first.
        source_file = result.get("path")
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


def gains_for(results: list[dict], entry: dict, k: int = _TOP_K) -> list[int]:
    """Binary gains of a ranked result list (first k, default 5) for one corpus entry."""
    return [resolve_gain(r, entry) for r in results[:k]]


def line_overlap_gain(hit_lines: tuple, expected_lines: tuple) -> int:
    """1 iff the closed line intervals [hit] and [expected] share at least one line.

    True interval overlap, not equality/containment special-cased: two intervals
    overlap iff each one starts at or before the other ends. Touching at a shared
    boundary line counts as overlap (that line IS shared); disjoint intervals with
    a gap between them do not.
    """
    hit_start, hit_end = hit_lines
    expected_start, expected_end = expected_lines
    return 1 if hit_start <= expected_end and expected_start <= hit_end else 0


def _line_range(result: dict) -> Optional[tuple]:
    start, end = result.get("lineStart"), result.get("lineEnd")
    if isinstance(start, int) and isinstance(end, int):
        return start, end
    return None


def span_gain(result: dict, entry: dict) -> int:
    """Span-hit gain: the file matches (resolve_gain) AND the hit's lines overlap
    the entry's expectedLines [start, end]. 0 when either the file doesn't match,
    the entry carries no expectedLines, or the result carries no line range."""
    if resolve_gain(result, entry) == 0:
        return 0
    expected_lines = entry.get("expectedLines")
    if not (isinstance(expected_lines, (list, tuple)) and len(expected_lines) == 2):
        return 0
    hit_lines = _line_range(result)
    if hit_lines is None:
        return 0
    return line_overlap_gain(hit_lines, tuple(expected_lines))


def span_gains_for(results: list[dict], entry: dict, k: int = _TOP_K) -> list[int]:
    """Span-hit gains of a ranked result list (first k, default 5) for one corpus entry."""
    return [span_gain(r, entry) for r in results[:k]]


@dataclass(frozen=True)
class QueryScore:
    """One query's scored outcome (plan 7.1 per-query rows).

    ndcg10/mrr10/hit5/span_hit5 and the grouping fields (language/size_band/
    split/family/negative_test) are additive: every field before
    first_relevant_rank is the original plan-7.1 shape, unchanged in name,
    order and value, so a positional construction with exactly those 7 args
    (as the existing tests use) still works byte-for-byte.
    """

    entry_id: str
    category: str
    ndcg5: float
    mrr5: float
    hit3: int
    hit1: int
    first_relevant_rank: Optional[int]
    ndcg10: float = 0.0
    mrr10: float = 0.0
    hit5: int = 0
    span_hit5: int = 0
    language: Optional[str] = None
    size_band: Optional[str] = None
    split: Optional[str] = None
    family: Optional[str] = None
    negative_test: bool = False

    def as_dict(self) -> dict:
        return {
            "entry_id": self.entry_id,
            "category": self.category,
            "ndcg5": self.ndcg5,
            "mrr5": self.mrr5,
            "hit3": self.hit3,
            "hit1": self.hit1,
            "first_relevant_rank": self.first_relevant_rank,
            "ndcg10": self.ndcg10,
            "mrr10": self.mrr10,
            "hit5": self.hit5,
            "spanHit5": self.span_hit5,
            "language": self.language,
            "sizeBand": self.size_band,
            "split": self.split,
            "family": self.family,
            "negativeTest": self.negative_test,
        }


@dataclass(frozen=True)
class CategoryMetrics:
    """Aggregate over one group bucket (category, language, sizeBand or split)."""

    category: str
    count: int
    mean_ndcg5: float
    mean_mrr5: float
    hit3_rate: float
    hit1_rate: float
    mean_ndcg10: float = 0.0
    mean_mrr10: float = 0.0
    hit5_rate: float = 0.0
    span_hit5_rate: float = 0.0

    def as_dict(self) -> dict:
        return {
            "count": self.count,
            "mean_ndcg5": self.mean_ndcg5,
            "mean_mrr5": self.mean_mrr5,
            "hit3_rate": self.hit3_rate,
            "hit1_rate": self.hit1_rate,
            "mean_ndcg10": self.mean_ndcg10,
            "mean_mrr10": self.mean_mrr10,
            "hit5_rate": self.hit5_rate,
            "spanHit5Rate": self.span_hit5_rate,
        }


@dataclass(frozen=True)
class Metrics:
    """Evaluation outcome: means + per-query + per-group records.

    per_language/per_size_band/per_split are additive groupings alongside the
    original per_category (plan E3): each is empty when the scored entries
    carry none of that field, so a memory-only corpus's Metrics is unchanged.
    """

    mean_ndcg5: float
    mean_mrr5: float
    hit3_rate: float
    hit1_rate: float
    per_query: list[QueryScore] = field(default_factory=list)
    per_category: dict[str, CategoryMetrics] = field(default_factory=dict)
    config: dict = field(default_factory=dict)
    mean_ndcg10: float = 0.0
    mean_mrr10: float = 0.0
    hit5_rate: float = 0.0
    span_hit5_rate: float = 0.0
    per_language: dict[str, CategoryMetrics] = field(default_factory=dict)
    per_size_band: dict[str, CategoryMetrics] = field(default_factory=dict)
    per_split: dict[str, CategoryMetrics] = field(default_factory=dict)

    def as_dict(self) -> dict:
        return {
            "mean_ndcg5": self.mean_ndcg5,
            "mean_mrr5": self.mean_mrr5,
            "hit3_rate": self.hit3_rate,
            "hit1_rate": self.hit1_rate,
            "mean_ndcg10": self.mean_ndcg10,
            "mean_mrr10": self.mean_mrr10,
            "hit5_rate": self.hit5_rate,
            "spanHit5Rate": self.span_hit5_rate,
            "per_query": [q.as_dict() for q in self.per_query],
            "per_category": {name: cm.as_dict() for name, cm in sorted(self.per_category.items())},
            "per_language": {name: cm.as_dict() for name, cm in sorted(self.per_language.items())},
            "per_size_band": {name: cm.as_dict() for name, cm in sorted(self.per_size_band.items())},
            "per_split": {name: cm.as_dict() for name, cm in sorted(self.per_split.items())},
            "config": self.config,
        }


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def score_query(results: list[dict], entry: dict) -> QueryScore:
    """Score one query's ranked results against its corpus entry."""
    gains5 = gains_for(results, entry, _TOP_K)
    gains10 = gains_for(results, entry, _TOP_K_EXTENDED)
    span_gains5 = span_gains_for(results, entry, _TOP_K)
    return QueryScore(
        entry_id=str(entry.get("id", "?")),
        category=str(entry.get("category", "uncategorized")),
        ndcg5=ndcg_at_k(gains5, _TOP_K),
        mrr5=mrr_at_k(gains5, _TOP_K),
        hit3=hit_at_k(gains5, 3),
        hit1=hit_at_k(gains5, 1),
        first_relevant_rank=rank_of_first_relevant(gains5),
        ndcg10=ndcg_at_k(gains10, _TOP_K_EXTENDED),
        mrr10=mrr_at_k(gains10, _TOP_K_EXTENDED),
        hit5=hit_at_k(gains5, 5),
        span_hit5=hit_at_k(span_gains5, 5),
        language=entry.get("language"),
        size_band=entry.get("sizeBand"),
        split=entry.get("split"),
        family=entry.get("family"),
        negative_test=bool(entry.get("negativeTest")),
    )


def _group_metrics(name: str, scores: list[QueryScore]) -> CategoryMetrics:
    return CategoryMetrics(
        category=name,
        count=len(scores),
        mean_ndcg5=_mean([q.ndcg5 for q in scores]),
        mean_mrr5=_mean([q.mrr5 for q in scores]),
        hit3_rate=_mean([float(q.hit3) for q in scores]),
        hit1_rate=_mean([float(q.hit1) for q in scores]),
        mean_ndcg10=_mean([q.ndcg10 for q in scores]),
        mean_mrr10=_mean([q.mrr10 for q in scores]),
        hit5_rate=_mean([float(q.hit5) for q in scores]),
        span_hit5_rate=_mean([float(q.span_hit5) for q in scores]),
    )


def _group_by(query_scores: list[QueryScore], key_fn) -> dict[str, CategoryMetrics]:
    """Bucket query scores by key_fn; entries whose key is None/empty are excluded
    (a memory-only corpus scores no language/sizeBand/split, so those groupings
    come back empty rather than collecting everything under a fake 'None' bucket)."""
    buckets: dict[str, list[QueryScore]] = {}
    for qs in query_scores:
        key = key_fn(qs)
        if not key:
            continue
        buckets.setdefault(str(key), []).append(qs)
    return {name: _group_metrics(name, scores) for name, scores in buckets.items()}


def summarize(query_scores: list[QueryScore], config: Optional[dict] = None) -> Metrics:
    """Means + per-query + per-group records over scored queries."""
    per_category = _group_by(query_scores, lambda q: q.category)
    per_language = _group_by(query_scores, lambda q: q.language)
    per_size_band = _group_by(query_scores, lambda q: q.size_band)
    per_split = _group_by(query_scores, lambda q: q.split)

    return Metrics(
        mean_ndcg5=_mean([q.ndcg5 for q in query_scores]),
        mean_mrr5=_mean([q.mrr5 for q in query_scores]),
        hit3_rate=_mean([float(q.hit3) for q in query_scores]),
        hit1_rate=_mean([float(q.hit1) for q in query_scores]),
        mean_ndcg10=_mean([q.ndcg10 for q in query_scores]),
        mean_mrr10=_mean([q.mrr10 for q in query_scores]),
        hit5_rate=_mean([float(q.hit5) for q in query_scores]),
        span_hit5_rate=_mean([float(q.span_hit5) for q in query_scores]),
        per_query=list(query_scores),
        per_category=per_category,
        per_language=per_language,
        per_size_band=per_size_band,
        per_split=per_split,
        config=dict(config or {}),
    )
