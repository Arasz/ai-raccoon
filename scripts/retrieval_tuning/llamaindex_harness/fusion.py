"""Fusion-port of ReciprocalRankFusion.cs, SourceAffinityRanker.cs,
StructureFusion.cs and SearchResultMerger.cs (pure, stdlib-only).

The C# source wins on any conflict with the plan. Port notes:
- RRF leg order is [fts, vector]; ranks are 1-based; ``weight/(k+rank)``;
  max-normalize; floor; ``OrderByDescending(Ranking).ThenBy(Path)``; Take.
  A skipped leg (empty candidates or weight 0) is absent, never empty.
- Affinity: ``""`` source_file counts as null (M7 coercion); ``chunk_index < 0``
  never counts as adjacent (GH #371); doc scores use BOOSTED scores with the
  formula from params (Max/Sum); consolidation drops only siblings adjacent to
  the boosted-order best with gap >= threshold; re-normalize to boosted max.
- SearchResultMerger.Merge is ported verbatim INCLUDING the single-list re-fuse
  (a monotonic remap that still feeds the threshold/gap comparisons).
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class DocScoreFormula(str, Enum):
    MAX = "max"
    SUM = "sum"


@dataclass(frozen=True)
class RankedHit:
    """One candidate flowing through fuse -> affinity -> floor -> take."""

    hash: str
    ranking: float
    path: str = ""
    source_file: str | None = None
    chunk_index: int = 0
    total_chunks: int = 0


def _has_source(source_file: str | None) -> bool:
    return source_file is not None and source_file != ""


def fuse_rrf(
    legs: list[tuple[str, float, list[str]]],
    paths: dict[str, str],
    k: int,
    min_relative_score: float,
    limit: int,
) -> list[tuple[str, float]]:
    """Port of ReciprocalRankFusion.FuseWithEvidence minus the evidence sidecar."""
    scores: dict[str, float] = {}
    for _name, weight, candidates in legs:
        if not candidates or weight == 0:
            continue
        for rank, h in enumerate(candidates, start=1):
            scores[h] = scores.get(h, 0.0) + weight / (k + rank)
    if not scores:
        return []
    top = max(scores.values())
    fused = [(h, s / top) for h, s in scores.items() if s / top >= min_relative_score]
    fused.sort(key=lambda item: (-item[1], paths.get(item[0], "")))
    return fused[:limit]


def rank_affinity(
    candidates: list[RankedHit],
    source_lambda: float,
    consolidation_threshold: float,
    formula: DocScoreFormula,
) -> list[RankedHit]:
    """Port of SourceAffinityRanker.Rank."""
    if source_lambda <= 0.0 or not candidates:
        return list(candidates)

    max_raw = max(c.ranking for c in candidates)
    by_source: dict[str, list[RankedHit]] = {}
    for c in candidates:
        if _has_source(c.source_file):
            by_source.setdefault(c.source_file, []).append(c)  # type: ignore[arg-type]

    def sibling_count(c: RankedHit) -> int:
        if not _has_source(c.source_file) or c.chunk_index < 0:
            return 0
        count = 0
        for sib in by_source[c.source_file]:  # type: ignore[index]
            if (
                sib.chunk_index >= 0
                and abs(sib.chunk_index - c.chunk_index) == 1
                and sib.ranking >= max_raw - consolidation_threshold
            ):
                count += 1
        return count

    scores = {c.hash: c.ranking + source_lambda * sibling_count(c) for c in candidates}

    doc_score: dict[str, float] = {}
    for c in candidates:
        if not _has_source(c.source_file):
            continue
        key = c.source_file  # type: ignore[assignment]
        if formula == DocScoreFormula.SUM:
            doc_score[key] = doc_score.get(key, 0.0) + scores[c.hash]
        else:
            doc_score[key] = max(doc_score.get(key, 0.0), scores[c.hash])

    def doc_key(c: RankedHit) -> float:
        if not _has_source(c.source_file):
            return scores[c.hash]
        return doc_score[c.source_file]  # type: ignore[index]

    order = sorted(candidates, key=lambda c: (-scores[c.hash], -doc_key(c), c.path, c.chunk_index))

    best_by_source: dict[str, RankedHit] = {}
    for c in order:
        if _has_source(c.source_file) and c.source_file not in best_by_source:
            best_by_source[c.source_file] = c  # type: ignore[index]

    merged: set[str] = set()
    for c in order:
        if not _has_source(c.source_file):
            continue
        best = best_by_source.get(c.source_file)  # type: ignore[arg-type]
        if best is None or best.hash == c.hash:
            continue
        if best.chunk_index < 0 or c.chunk_index < 0:
            continue
        if abs(best.chunk_index - c.chunk_index) != 1:
            continue
        if scores[best.hash] - scores[c.hash] >= consolidation_threshold:
            merged.add(c.hash)

    kept = [c for c in order if c.hash not in merged]
    peak = max((scores[c.hash] for c in kept), default=1.0)
    return [
        RankedHit(c.hash, scores[c.hash] / peak, c.path, c.source_file, c.chunk_index, c.total_chunks)
        for c in kept
    ]


def structure_fused(content_sim: float, structure_sim: float | None, alpha: float) -> float:
    """Port of StructureFusion.Fused: absent structure scores as zero (deliberate cap)."""
    return alpha * content_sim + (1.0 - alpha) * (structure_sim if structure_sim is not None else 0.0)


def structure_rank(
    content: dict[str, float],
    structure: dict[str, float],
    alpha: float,
    limit: int,
) -> list[tuple[str, float]]:
    """Port of StructureFusion.Rank: union of both modalities, hash-ordinal tiebreak."""
    ranked = [
        (h, structure_fused(content.get(h, 0.0), structure.get(h), alpha))
        for h in set(content) | set(structure)
    ]
    ranked.sort(key=lambda item: (-item[1], item[0]))
    return ranked[:limit]


def apply_relative_floor(
    pairs: list[tuple[str, float]], min_relative_score: float
) -> list[tuple[str, float]]:
    """The merger's floor: ranking >= fraction of this response's top hit (ADR-0047)."""
    return [(h, r) for h, r in pairs if r >= min_relative_score]


def merge_results(
    candidates: list[RankedHit],
    limit: int,
    min_relative_score: float,
    rrf_k: int,
    source_lambda: float,
    consolidation_threshold: float,
    formula: DocScoreFormula,
) -> list[RankedHit]:
    """Port of SearchResultMerger.Merge: single-list re-fuse, affinity, floor, Take."""
    fused = fuse_rrf(
        [("fused", 1.0, [c.hash for c in candidates])],
        {c.hash: c.path for c in candidates},
        rrf_k,
        0.0,
        2**31 - 1,
    )
    by_hash = {c.hash: c for c in candidates}
    rescored = [
        RankedHit(h, r, by_hash[h].path, by_hash[h].source_file, by_hash[h].chunk_index,
                  by_hash[h].total_chunks)
        for h, r in fused
    ]
    ranked = rank_affinity(rescored, source_lambda, consolidation_threshold, formula)
    floored = [c for c in ranked if c.ranking >= min_relative_score]
    return floored[:limit]
