"""Code-eval scoring extensions (plan §E3): line_overlap_gain + language/sizeBand/split grouping.

RED-first: line_overlap_gain's three interval shapes (disjoint/touching/contained) and the
new grouping breakdowns are hand-computed here, independent of the implementation.
"""

from __future__ import annotations

import pytest

from retrieval_tuning.scoring import (
    QueryScore,
    line_overlap_gain,
    resolve_gain,
    score_query,
    span_gain,
    summarize,
)


class TestLineOverlapGain:
    """True interval overlap: disjoint -> 0, touching -> 1, contained -> 1."""

    def test_disjoint_intervals_score_zero(self):
        assert line_overlap_gain((1, 5), (10, 15)) == 0
        assert line_overlap_gain((10, 15), (1, 5)) == 0  # symmetric

    def test_touching_at_a_shared_boundary_line_scores_one(self):
        # hit ends exactly where expected begins: line 5 is shared.
        assert line_overlap_gain((1, 5), (5, 10)) == 1
        assert line_overlap_gain((5, 10), (1, 5)) == 1

    def test_contained_interval_scores_one(self):
        # hit fully inside expected.
        assert line_overlap_gain((3, 7), (1, 10)) == 1
        # expected fully inside hit.
        assert line_overlap_gain((1, 10), (3, 7)) == 1

    def test_partial_overlap_scores_one(self):
        assert line_overlap_gain((1, 8), (5, 12)) == 1

    def test_identical_intervals_score_one(self):
        assert line_overlap_gain((4, 9), (4, 9)) == 1

    def test_a_gap_of_one_line_is_still_disjoint(self):
        # hit ends at 5, expected starts at 7: line 6 is the gap, nothing shared.
        assert line_overlap_gain((1, 5), (7, 12)) == 0


class TestSpanGain:
    """span_gain requires BOTH the file match (resolve_gain) and a line overlap."""

    def _entry(self, **overrides):
        entry = {"expectedSource": "src:widget.py", "expectedLines": [10, 20]}
        entry.update(overrides)
        return entry

    def test_matching_file_and_overlapping_lines_is_a_span_hit(self):
        result = {"path": "src/widget.py", "lineStart": 15, "lineEnd": 25}
        assert span_gain(result, self._entry()) == 1

    def test_matching_file_but_disjoint_lines_is_not_a_span_hit(self):
        result = {"path": "src/widget.py", "lineStart": 100, "lineEnd": 120}
        assert span_gain(result, self._entry()) == 0

    def test_wrong_file_is_not_a_span_hit_even_with_overlapping_lines(self):
        result = {"path": "src/other.py", "lineStart": 10, "lineEnd": 20}
        assert span_gain(result, self._entry()) == 0

    def test_entry_without_expected_lines_is_never_a_span_hit(self):
        result = {"path": "src/widget.py", "lineStart": 10, "lineEnd": 20}
        entry = {"expectedSource": "src:widget.py"}
        assert span_gain(result, entry) == 0

    def test_result_without_a_line_range_is_never_a_span_hit(self):
        result = {"path": "src/widget.py"}
        assert span_gain(result, self._entry()) == 0

    def test_resolve_gain_itself_matches_the_path_field_for_code_results(self):
        # Code results carry 'path', not 'sourceFile' — resolve_gain must still resolve them.
        assert resolve_gain({"path": "src/widget.py"}, self._entry()) == 1


class TestScoreQueryCodeFields:
    def test_ndcg10_mrr10_hit5_and_span_hit5_are_populated(self):
        entry = {
            "id": "C1",
            "category": "identifier-fragment",
            "expectedSource": "src:widget.py",
            "expectedLines": [10, 20],
            "language": "python",
            "sizeBand": "small",
            "split": "held-out",
            "family": "self",
        }
        results = [
            {"path": "src/other.py", "lineStart": 1, "lineEnd": 2},
            {"path": "src/widget.py", "lineStart": 12, "lineEnd": 18},
        ]
        qs = score_query(results, entry)
        assert qs.ndcg10 > 0.0
        assert qs.mrr10 == pytest.approx(0.5)
        assert qs.hit5 == 1
        assert qs.span_hit5 == 1
        assert qs.language == "python"
        assert qs.size_band == "small"
        assert qs.split == "held-out"
        assert qs.family == "self"
        assert qs.negative_test is False

    def test_negative_test_flag_and_absent_grouping_fields_default_safely(self):
        entry = {"id": "N1", "category": "negative", "negativeTest": True}
        qs = score_query([], entry)
        assert qs.negative_test is True
        assert qs.language is None
        assert qs.size_band is None
        assert qs.split is None
        assert qs.family is None
        assert qs.ndcg5 == 0.0
        assert qs.span_hit5 == 0


class TestGroupingBySplitLanguageSizeBand:
    def _qs(self, entry_id, *, language=None, size_band=None, split=None, ndcg5=1.0):
        return QueryScore(
            entry_id=entry_id,
            category="identifier-fragment",
            ndcg5=ndcg5,
            mrr5=ndcg5,
            hit3=1 if ndcg5 else 0,
            hit1=1 if ndcg5 else 0,
            first_relevant_rank=1 if ndcg5 else None,
            language=language,
            size_band=size_band,
            split=split,
        )

    def test_per_language_breaks_down_by_language(self):
        scores = [
            self._qs("a", language="python", ndcg5=1.0),
            self._qs("b", language="python", ndcg5=0.0),
            self._qs("c", language="rust", ndcg5=1.0),
        ]
        metrics = summarize(scores)
        assert metrics.per_language["python"].count == 2
        assert metrics.per_language["python"].mean_ndcg5 == pytest.approx(0.5)
        assert metrics.per_language["rust"].count == 1
        assert metrics.per_language["rust"].mean_ndcg5 == pytest.approx(1.0)

    def test_per_size_band_breaks_down_by_size_band(self):
        scores = [
            self._qs("a", size_band="small", ndcg5=1.0),
            self._qs("b", size_band="large", ndcg5=0.0),
        ]
        metrics = summarize(scores)
        assert metrics.per_size_band["small"].mean_ndcg5 == pytest.approx(1.0)
        assert metrics.per_size_band["large"].mean_ndcg5 == pytest.approx(0.0)

    def test_per_split_breaks_down_tuning_vs_held_out(self):
        scores = [
            self._qs("a", split="tuning", ndcg5=1.0),
            self._qs("b", split="held-out", ndcg5=0.5),
            self._qs("c", split="held-out", ndcg5=0.5),
        ]
        metrics = summarize(scores)
        assert metrics.per_split["tuning"].count == 1
        assert metrics.per_split["held-out"].count == 2
        assert metrics.per_split["held-out"].mean_ndcg5 == pytest.approx(0.5)

    def test_entries_without_a_grouping_field_produce_an_empty_group(self):
        scores = [self._qs("a")]  # no language/sizeBand/split at all
        metrics = summarize(scores)
        assert metrics.per_language == {}
        assert metrics.per_size_band == {}
        assert metrics.per_split == {}
        # per_category is untouched by the absence of the new fields.
        assert metrics.per_category["identifier-fragment"].count == 1

    def test_overall_ndcg10_mrr10_hit5_and_span_hit5_rate_are_reported(self):
        scores = [
            QueryScore("a", "cat", 1.0, 1.0, 1, 1, 1, ndcg10=1.0, mrr10=1.0, hit5=1, span_hit5=1),
            QueryScore("b", "cat", 0.0, 0.0, 0, 0, None, ndcg10=0.0, mrr10=0.0, hit5=0, span_hit5=0),
        ]
        metrics = summarize(scores)
        assert metrics.hit5_rate == pytest.approx(0.5)
        assert metrics.mean_ndcg10 == pytest.approx(0.5)
        assert metrics.mean_mrr10 == pytest.approx(0.5)
        assert metrics.span_hit5_rate == pytest.approx(0.5)
