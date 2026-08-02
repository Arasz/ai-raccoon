"""Generic BM25 ranking over fused per-document term-frequency maps.

k1=1.2, b=0.75 — see docs/adr/0012-bm25-retrieval-with-a-falsifiable-eval.md. No
knowledge of skills or MCP tools: a caller builds one term-frequency Counter per
document (see fuse_document below) and reads back ScoredDoc results.
"""
from __future__ import annotations

import math
from collections import Counter
from dataclasses import dataclass
from typing import Iterable, List, Mapping, Optional, Sequence, Tuple

K1 = 1.2
B = 0.75


@dataclass(frozen=True)
class ScoredDoc:
    """One document's BM25 score and coverage against a query."""

    doc_id: str
    score: float
    coverage: float
    matched_terms: int


def fuse_document(fields: Iterable[Tuple[Sequence[str], float]]) -> Counter:
    """Fuse per-field token lists into one term-frequency Counter, weighted per field.

    `fields` is an iterable of (tokens, weight) pairs. Each token contributes
    `weight` to its count rather than 1, which is what lets `name` outweigh
    `description` in the fused document.
    """
    fused: Counter = Counter()
    for tokens, weight in fields:
        for token in tokens:
            fused[token] += weight
    return fused


class Bm25Corpus:
    """BM25 over a fixed set of documents, each a fused weighted term-frequency map."""

    def __init__(self, term_freqs: Mapping[str, Counter]):
        if not term_freqs:
            raise ValueError("Bm25Corpus requires at least one document")
        self._term_freqs = dict(term_freqs)
        self._doc_len = {doc_id: sum(tf.values()) for doc_id, tf in self._term_freqs.items()}
        self._n = len(self._term_freqs)
        self._avgdl = sum(self._doc_len.values()) / self._n
        self._df: Counter = Counter()
        for tf in self._term_freqs.values():
            for term in tf:
                self._df[term] += 1

    def idf(self, term: str) -> float:
        """log(1 + (N - df + 0.5) / (df + 0.5)) — 0 for a term absent from the corpus."""
        df = self._df.get(term, 0)
        return math.log(1 + (self._n - df + 0.5) / (df + 0.5))

    def coverage_denominator(
        self, query_terms: Sequence[str], coverage_cap: Optional[int] = None
    ) -> float:
        """Summed idf of the query's `coverage_cap` most informative terms, or of all of them.

        Capping is what stops coverage falling as roughly 1/len(query) (issue #165): without
        it every extra word enlarges the denominator and can only ever be matched by a
        document that happens to contain it. At or below the cap this is the same sum as
        before, so short queries keep exactly the coverage they had.
        """
        distinct = set(query_terms)
        if coverage_cap is not None and len(distinct) > coverage_cap:
            distinct = set(sorted(distinct, key=lambda t: (-self.idf(t), t))[:coverage_cap])
        return sum(self.idf(t) for t in distinct)

    def rank(
        self, query_terms: Sequence[str], coverage_cap: Optional[int] = None
    ) -> List[ScoredDoc]:
        """Score every document against `query_terms`, sorted (-score, -coverage, doc_id).

        `coverage_cap` bounds the coverage denominator only; scores are untouched.
        """
        query_counts = Counter(query_terms)
        total_idf = self.coverage_denominator(query_terms, coverage_cap)
        results = []
        for doc_id, tf in self._term_freqs.items():
            dl = self._doc_len[doc_id]
            length_norm = 1 - B + B * (dl / self._avgdl if self._avgdl else 1.0)
            score = 0.0
            matched_idf = 0.0
            matched_terms = 0
            for term in query_counts:
                freq = tf.get(term, 0)
                if freq == 0:
                    continue
                matched_terms += 1
                idf = self.idf(term)
                matched_idf += idf
                score += idf * (freq * (K1 + 1)) / (freq + K1 * length_norm)
            # min(): the numerator sums every matched term, so it can exceed a capped
            # denominator, and a coverage above 1.0 means nothing against a [0, 1] threshold.
            coverage = min(1.0, matched_idf / total_idf) if total_idf > 0 else 0.0
            results.append(ScoredDoc(doc_id, score, coverage, matched_terms))
        results.sort(key=lambda r: (-r.score, -r.coverage, r.doc_id))
        return results
