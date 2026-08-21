"""Hand-computed unit tests for retrieval_tuning.scoring (pure functions).

RED-first: every expected value below is computed by hand from the definitions in
docs/work/2026-08-21-parameter-tuning-plan.md section 7.1 (binary gains, log2
discount, nDCG@5 / MRR@5 / hit@3 / hit@1) — no implementation was consulted.
"""

import math

import pytest

from retrieval_tuning.scoring import (
    Metrics,
    QueryScore,
    hit_at_k,
    mrr_at_5,
    ndcg_at_5,
    rank_of_first_relevant,
    resolve_gain,
    score_query,
    summarize,
)

LOG2_3 = math.log2(3)
LOG2_4 = math.log2(4)
LOG2_5 = math.log2(5)
LOG2_6 = math.log2(6)


class TestNdcgAt5:
    """Hand-computed nDCG@5 for binary gains with the log2 discount."""

    def test_target_at_rank_1_is_perfect(self):
        assert ndcg_at_5([1, 0, 0, 0, 0]) == pytest.approx(1.0)

    def test_target_at_rank_2(self):
        # DCG = 1/log2(3); IDCG = 1 -> nDCG = 1/log2(3)
        assert ndcg_at_5([0, 1, 0, 0, 0]) == pytest.approx(1.0 / LOG2_3)

    def test_target_at_rank_3(self):
        assert ndcg_at_5([0, 0, 1, 0, 0]) == pytest.approx(1.0 / LOG2_4)

    def test_target_at_rank_4(self):
        assert ndcg_at_5([0, 0, 0, 1, 0]) == pytest.approx(1.0 / LOG2_5)

    def test_target_at_rank_5(self):
        assert ndcg_at_5([0, 0, 0, 0, 1]) == pytest.approx(1.0 / LOG2_6)

    def test_two_targets_at_top_is_perfect(self):
        assert ndcg_at_5([1, 1, 0, 0, 0]) == pytest.approx(1.0)

    def test_two_targets_with_gap(self):
        # DCG = 1 + 1/log2(4) = 1.5; IDCG = 1 + 1/log2(3)
        expected = (1.0 + 1.0 / LOG2_4) / (1.0 + 1.0 / LOG2_3)
        assert ndcg_at_5([1, 0, 1, 0, 0]) == pytest.approx(expected)

    def test_no_relevant_results_is_zero(self):
        assert ndcg_at_5([0, 0, 0, 0, 0]) == 0.0

    def test_empty_list_is_zero(self):
        assert ndcg_at_5([]) == 0.0

    def test_only_first_five_ranks_count(self):
        # A gain at rank 6 must not change the score of the 5-length list.
        assert ndcg_at_5([0, 1, 0, 0, 0, 1, 1]) == pytest.approx(1.0 / LOG2_3)


class TestMrrAt5:
    def test_rank_1(self):
        assert mrr_at_5([1, 0, 0, 0, 0]) == pytest.approx(1.0)

    def test_rank_2(self):
        assert mrr_at_5([0, 1, 0, 0, 0]) == pytest.approx(0.5)

    def test_rank_4(self):
        assert mrr_at_5([0, 0, 0, 1, 0]) == pytest.approx(0.25)

    def test_no_relevant_is_zero(self):
        assert mrr_at_5([0, 0, 0, 0, 0]) == 0.0

    def test_empty_is_zero(self):
        assert mrr_at_5([]) == 0.0


class TestHitAtK:
    def test_hit_at_3_when_target_at_3(self):
        assert hit_at_k([0, 0, 1, 0, 0], 3) == 1

    def test_miss_at_2_when_target_at_3(self):
        assert hit_at_k([0, 0, 1, 0, 0], 2) == 0

    def test_hit_at_1(self):
        assert hit_at_k([1, 0, 0, 0, 0], 1) == 1


class TestRankOfFirstRelevant:
    def test_rank_is_one_based(self):
        assert rank_of_first_relevant([0, 0, 1, 0, 0]) == 3

    def test_none_when_no_relevant(self):
        assert rank_of_first_relevant([0, 0, 0, 0, 0]) is None


class TestReversedOrderDiscrimination:
    """Plan 7.1: a REVERSED result list must fail a floor the normal ranking passes."""

    def test_normal_ranking_passes_the_floor(self):
        assert ndcg_at_5([1, 0, 0, 0, 0]) >= 0.9

    def test_reversed_ranking_fails_the_floor(self):
        assert ndcg_at_5([0, 0, 0, 0, 1]) < 0.5

    def test_reversed_is_strictly_worse(self):
        assert ndcg_at_5([0, 0, 0, 0, 1]) < ndcg_at_5([1, 0, 0, 0, 0])


class TestResolveGain:
    """Relevance resolution: expectedHash prefix, or expectedSource suffix (+ section anchor)."""

    def test_expected_hash_prefix_match(self):
        entry = {"expectedHash": "abc123"}
        result = {"hash": "abc123def4567890"}
        assert resolve_gain(result, entry) == 1

    def test_expected_hash_prefix_mismatch(self):
        entry = {"expectedHash": "abc124"}
        result = {"hash": "abc123def4567890"}
        assert resolve_gain(result, entry) == 0

    def test_expected_hash_is_case_insensitive(self):
        entry = {"expectedHash": "ABC123"}
        result = {"hash": "abc123def4567890"}
        assert resolve_gain(result, entry) == 1

    def test_expected_source_suffix_match(self):
        entry = {"expectedSource": "docs:adr:0042-*.md"}
        result = {"sourceFile": "docs/adr/0042-widget.md"}
        assert resolve_gain(result, entry) == 1

    def test_expected_source_suffix_mismatch(self):
        entry = {"expectedSource": "docs:adr:0042-*.md"}
        result = {"sourceFile": "docs/adr/0043-widget.md"}
        assert resolve_gain(result, entry) == 0

    def test_expected_source_with_anchor_matches_any_heading_segment(self):
        entry = {"expectedSource": "docs:adr:0042-*.md#decision"}
        result = {"sourceFile": "docs/adr/0042-widget.md", "headingPath": "Overview > Decision"}
        assert resolve_gain(result, entry) == 1

    def test_expected_source_with_anchor_heading_mismatch(self):
        entry = {"expectedSource": "docs:adr:0042-*.md#decision"}
        result = {"sourceFile": "docs/adr/0042-widget.md", "headingPath": "Overview > Context"}
        assert resolve_gain(result, entry) == 0

    def test_expected_source_anchor_unverifiable_falls_back_to_file_match(self):
        # The MCP result carries no heading field: the file-level match stands.
        entry = {"expectedSource": "docs:adr:0042-*.md#decision"}
        result = {"sourceFile": "docs/adr/0042-widget.md"}
        assert resolve_gain(result, entry) == 1

    def test_non_file_query_matches_hash_only(self):
        entry = {"nonFileTarget": True, "expectedHash": "abc123", "expectedSource": None}
        assert resolve_gain({"hash": "abc1239999"}, entry) == 1
        assert resolve_gain({"hash": "zzz9999999"}, entry) == 0

    def test_result_without_any_identifying_field_is_not_a_match(self):
        entry = {"expectedHash": "abc123"}
        assert resolve_gain({"snippet": "nothing to see"}, entry) == 0


class TestScoreQuery:
    def test_query_score_fields(self):
        entry = {
            "id": "E001",
            "category": "ADR (Decision)",
            "query": "what does adr 42 decide?",
            "expectedSource": "docs:adr:0042-*.md#decision",
        }
        results = [
            {"hash": "deadbeef", "sourceFile": "docs/adr/0042-widget.md", "headingPath": "Overview"},
            {"hash": "cafebabe", "sourceFile": "docs/adr/0042-widget.md", "headingPath": "Decision"},
        ]
        qs = score_query(results, entry)
        assert isinstance(qs, QueryScore)
        assert qs.entry_id == "E001"
        assert qs.category == "ADR (Decision)"
        assert qs.ndcg5 == pytest.approx(1.0 / LOG2_3)  # target at rank 2
        assert qs.mrr5 == pytest.approx(0.5)
        assert qs.hit3 == 1
        assert qs.hit1 == 0
        assert qs.first_relevant_rank == 2


class TestSummarize:
    def test_means_over_queries(self):
        q1 = QueryScore(
            entry_id="a", category="file", ndcg5=1.0, mrr5=1.0, hit3=1, hit1=1, first_relevant_rank=1
        )
        q2 = QueryScore(
            entry_id="b", category="file", ndcg5=0.0, mrr5=0.0, hit3=0, hit1=0, first_relevant_rank=None
        )
        metrics = summarize([q1, q2])
        assert isinstance(metrics, Metrics)
        assert metrics.mean_ndcg5 == pytest.approx(0.5)
        assert metrics.mean_mrr5 == pytest.approx(0.5)
        assert metrics.hit3_rate == pytest.approx(0.5)
        assert metrics.hit1_rate == pytest.approx(0.5)
        assert len(metrics.per_query) == 2
        assert metrics.per_category["file"].mean_ndcg5 == pytest.approx(0.5)

    def test_empty_summary_is_all_zero(self):
        metrics = summarize([])
        assert metrics.mean_ndcg5 == 0.0
        assert metrics.mean_mrr5 == 0.0
        assert metrics.hit3_rate == 0.0
        assert metrics.hit1_rate == 0.0
        assert metrics.per_query == []
        assert metrics.per_category == {}

    def test_per_category_breakdown(self):
        q1 = QueryScore("a", "file", 1.0, 1.0, 1, 1, 1)
        q2 = QueryScore("b", "non-file", 0.0, 0.0, 0, 0, None)
        q3 = QueryScore("c", "non-file", 0.5, 0.25, 1, 0, 3)
        metrics = summarize([q1, q2, q3])
        assert metrics.per_category["file"].mean_ndcg5 == pytest.approx(1.0)
        assert metrics.per_category["non-file"].mean_ndcg5 == pytest.approx(0.25)
        assert metrics.per_category["non-file"].hit3_rate == pytest.approx(0.5)
