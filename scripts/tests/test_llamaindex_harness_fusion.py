"""Fusion-port goldens — hand-computed from ReciprocalRankFusion.cs,
SourceAffinityRanker.cs, StructureFusion.cs and SearchResultMerger.cs.

Every expected value below was worked by hand (see comments); the Python port
must reproduce the C# arithmetic exactly.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

import pytest

from llamaindex_harness.fusion import (
    DocScoreFormula,
    RankedHit,
    apply_relative_floor,
    fuse_rrf,
    merge_results,
    rank_affinity,
    structure_fused,
    structure_rank,
)


def _hit(h, ranking, path="p.md", source_file="s.md", chunk_index=0, total_chunks=1):
    return RankedHit(hash=h, ranking=ranking, path=path, source_file=source_file,
                     chunk_index=chunk_index, total_chunks=total_chunks)


# --- RRF (ReciprocalRankFusion.FuseWithEvidence, k=60, legs ordered [fts, vector]) ---
# fts=[A,B,C], vector=[B,A,D]:
#   raw A = 1/61 + 1/62 = 123/3782 ; raw B = same (tie) ; raw C = 1/63 ; raw D = 1/63 (tie)
#   max = 123/3782 ; A = B = 1.0 ; C = D = (1/63)/(123/3782) = 3782/7749 ~= 0.48806


def test_rrf_hand_worked_scores_and_path_tiebreak():
    legs = [
        ("fts", 1.0, ["A", "B", "C"]),
        ("vector", 1.0, ["B", "A", "D"]),
    ]
    paths = {"A": "b.md", "B": "a.md", "C": "d.md", "D": "c.md"}
    fused = fuse_rrf(legs, paths, k=60, min_relative_score=0.0, limit=100)
    by_hash = {h: r for h, r in fused}
    assert by_hash["A"] == pytest.approx(1.0)
    assert by_hash["B"] == pytest.approx(1.0)
    assert by_hash["C"] == pytest.approx(3782 / 7749)
    assert by_hash["D"] == pytest.approx(3782 / 7749)
    # Ties break by path ordinal: B(a.md) before A(b.md); D(c.md) before C(d.md).
    assert [h for h, _ in fused] == ["B", "A", "D", "C"]


def test_rrf_skipped_leg_is_absent_never_empty():
    # Stopword-only query -> FTS leg skipped -> vector-only fusion, ranks rebase to 1..
    legs = [("vector", 1.0, ["X", "Y"])]
    fused = fuse_rrf(legs, {"X": "x", "Y": "y"}, k=60, min_relative_score=0.0, limit=100)
    by_hash = {h: r for h, r in fused}
    assert by_hash["X"] == pytest.approx(1.0)
    assert by_hash["Y"] == pytest.approx((1 / 62) / (1 / 61))


def test_rrf_weight_zero_leg_is_absent():
    legs = [("fts", 0.0, ["A"]), ("vector", 1.0, ["A"])]
    fused = fuse_rrf(legs, {"A": "a"}, k=60, min_relative_score=0.0, limit=100)
    assert fused == [("A", pytest.approx(1.0))]


def test_rrf_empty_inputs_yield_empty():
    assert fuse_rrf([], {}, k=60, min_relative_score=0.0, limit=8) == []


def test_rrf_limit_and_floor_apply():
    legs = [("fts", 1.0, ["A", "B", "C"])]
    fused = fuse_rrf(legs, {"A": "a", "B": "b", "C": "c"}, k=60, min_relative_score=0.0, limit=2)
    assert [h for h, _ in fused] == ["A", "B"]


# --- Source affinity (SourceAffinityRanker.Rank, lambda=0.1, threshold=0.1, Max) ---
# h1(s.md,c0,1.0) h2(s.md,c1,0.95) h3(other.md,c0,0.9) h4(null,0.85):
#   maxRaw=1.0; h1 sibs={h2: 0.95>=0.9} -> 1.1 ; h2 sibs={h1} -> 1.05 ; h3 -> 0.9 ; h4 -> 0.85
#   doc s.md=1.1, other.md=0.9 ; order h1,h2,h3,h4 ; consolidation gap h1-h2=0.05<0.1 kept
#   renormalized /1.1: [1.0, 1.05/1.1, 0.9/1.1, 0.85/1.1]


def test_affinity_hand_worked_boost_and_renormalize():
    cands = [
        _hit("h1", 1.0, path="s.md", source_file="s.md", chunk_index=0),
        _hit("h2", 0.95, path="s.md", source_file="s.md", chunk_index=1),
        _hit("h3", 0.9, path="other.md", source_file="other.md", chunk_index=0),
        _hit("h4", 0.85, path="z.md", source_file=None),
    ]
    ranked = rank_affinity(cands, source_lambda=0.1, consolidation_threshold=0.1,
                           formula=DocScoreFormula.MAX)
    assert [h.hash for h in ranked] == ["h1", "h2", "h3", "h4"]
    assert [h.ranking for h in ranked] == pytest.approx([1.0, 1.05 / 1.1, 0.9 / 1.1, 0.85 / 1.1])


def test_affinity_consolidation_drops_weak_adjacent_sibling():
    # h2 at 0.5 -> boosted 0.6; gap 1.1-0.6=0.5 >= 0.1 and adjacent -> merged away.
    cands = [
        _hit("h1", 1.0, path="s.md", source_file="s.md", chunk_index=0),
        _hit("h2", 0.5, path="s.md", source_file="s.md", chunk_index=1),
        _hit("h3", 0.9, path="other.md", source_file="other.md", chunk_index=0),
    ]
    ranked = rank_affinity(cands, source_lambda=0.1, consolidation_threshold=0.1,
                           formula=DocScoreFormula.MAX)
    assert [h.hash for h in ranked] == ["h1", "h3"]


def test_affinity_negative_chunk_index_scores_zero_siblings():
    # GH #371: chunk_index=-1 is "position unknown" and never adjacent, even to chunk 0.
    cands = [
        _hit("h1", 1.0, path="s.md", source_file="s.md", chunk_index=0),
        _hit("h2", 0.95, path="s.md", source_file="s.md", chunk_index=-1),
    ]
    ranked = rank_affinity(cands, source_lambda=0.1, consolidation_threshold=0.1,
                           formula=DocScoreFormula.MAX)
    assert [h.ranking for h in ranked] == pytest.approx([1.0, 0.95])
    assert [h.hash for h in ranked] == ["h1", "h2"]


def test_affinity_empty_source_string_counts_as_null():
    # M7: "" coerces like null — no boost, own score as doc score.
    cands = [
        _hit("h1", 1.0, path="a.md", source_file="", chunk_index=0),
        _hit("h2", 0.9, path="b.md", source_file="", chunk_index=1),
    ]
    ranked = rank_affinity(cands, source_lambda=0.1, consolidation_threshold=0.1,
                           formula=DocScoreFormula.MAX)
    assert [h.ranking for h in ranked] == pytest.approx([1.0, 0.9])


def test_affinity_lambda_zero_is_noop_identity():
    cands = [_hit("h2", 0.5), _hit("h1", 0.9)]
    assert rank_affinity(cands, source_lambda=0.0, consolidation_threshold=0.1,
                         formula=DocScoreFormula.MAX) == cands


def test_affinity_sum_formula_uses_boosted_scores():
    cands = [
        _hit("h1", 1.0, path="s.md", source_file="s.md", chunk_index=0),
        _hit("h2", 0.95, path="s.md", source_file="s.md", chunk_index=1),
        _hit("h3", 0.99, path="o.md", source_file="o.md", chunk_index=0),
    ]
    ranked = rank_affinity(cands, source_lambda=0.1, consolidation_threshold=0.1,
                           formula=DocScoreFormula.SUM)
    # doc s.md = 1.1+1.05 = 2.15 beats o.md = 0.99 on the secondary key.
    assert ranked[0].hash == "h1"


# --- Structure fusion (StructureFusion.Fused + Rank, alpha=0.5) ---


def test_structure_fused_missing_scores_zero():
    assert structure_fused(0.9, None, 0.5) == pytest.approx(0.45)
    assert structure_fused(0.5, 0.8, 0.5) == pytest.approx(0.65)


def test_structure_rank_union_orders_by_fused_with_hash_tiebreak():
    ranked = structure_rank({"A": 0.9, "B": 0.5}, {"B": 0.8}, alpha=0.5, limit=100)
    assert [h for h, _ in ranked] == ["B", "A"]
    assert structure_rank({"A": 0.5}, {"A": 0.5}, alpha=0.5, limit=100) == [("A", pytest.approx(0.5))]


def test_structure_rank_structure_only_hash_participates():
    ranked = structure_rank({}, {"Z": 0.7}, alpha=0.5, limit=100)
    assert ranked == [("Z", pytest.approx(0.35))]


# --- Merger chain (SearchResultMerger.Merge: single-list re-fuse, affinity, floor, take) ---


def test_merger_floor_is_relative_to_boosted_max():
    # Merger input is the fused list, already rank-ordered desc (the bank never
    # passes unordered candidates here): the single-list re-fuse preserves it.
    cands = [
        _hit("h1", 1.0, path="s.md", source_file="s.md", chunk_index=0),
        _hit("h3", 0.9, path="o.md", source_file="o.md", chunk_index=0),
        _hit("h2", 0.5, path="s.md", source_file="s.md", chunk_index=5),
    ]
    merged = merge_results(cands, limit=8, min_relative_score=0.6, rrf_k=60,
                           source_lambda=0.0, consolidation_threshold=float("inf"),
                           formula=DocScoreFormula.MAX)
    # Single-list re-fuse: 1/61, 1/62, 1/63 -> /max -> [1.0, 61/62, 61/63]; floor 0.6 keeps all.
    assert [h.hash for h in merged] == ["h1", "h3", "h2"]
    assert merged[0].ranking == pytest.approx(1.0)
    assert merged[1].ranking == pytest.approx(61 / 62)


def test_merger_take_limits_after_floor():
    cands = [_hit(f"h{i}", 1.0 - i * 0.01, path=f"{i}.md") for i in range(10)]
    merged = merge_results(cands, limit=8, min_relative_score=0.0, rrf_k=60,
                           source_lambda=0.0, consolidation_threshold=float("inf"),
                           formula=DocScoreFormula.MAX)
    assert len(merged) == 8


def test_relative_floor_drops_below_fraction_of_top():
    screening = apply_relative_floor([("A", 1.0), ("B", 0.59), ("C", 0.6)], 0.6)
    assert [h for h, _ in screening] == ["A", "C"]
